// SPDX-License-Identifier: MIT
// GPU linear-blend skinning + animation half of SkinnedSplatRenderer.
//
// Builds a bone-Transform hierarchy from the rest skeleton under this object, drives it with
// an Animator, and every frame computes the per-bone skinning matrix
//     M[b] = charWorldToLocal * bone.localToWorld * stretch[b] * restInv[b]
// (identity at rest), dispatches SplatSkinning.compute to deform every splat, and activates
// the base renderer's skin hooks so the deformed splats are drawn.
//
// The .bin picks the skeleton (SplatSkeleton.For): the 86-bone body rig, or the 441-bone
// body + facial rig. The two agree bone-for-bone over the 86 body bones, so everything that
// resolves bones BY NAME behaves identically on either rig; the facial bones are extra
// leaves hanging off head / neck_02.
//
// This lives in the same component as the loading/rendering half (SkinnedSplatRenderer.cs) for
// the same reason SkinnedMeshRenderer does skinning and holds the bones: they are one object's
// worth of state. Splitting them across two MonoBehaviours was never Unity's shape.

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
// The skinning jobs live in a separate always-loaded assembly so this one stays unloadable:
// scheduling a job registers its Execute with the engine permanently and there is no API to
// take that back. Nothing here may name a job type or call Schedule - go through
// SplatSkinningJobs instead.
using Viggle.SplatEngine.Jobs;

namespace Viggle.SplatEngine
{
    public partial class SkinnedSplatRenderer
    {
        /// <summary>
        /// Controller driving the character. Leave empty to keep it at rest. How its clips bind to
        /// the bones is the character file's own Rig Mode, set on the importer - see
        /// <see cref="SplatRigMode"/>.
        /// </summary>
        [Tooltip("An AnimatorController to drive the character. Leave empty to keep the character " +
                 "at rest. How its clips bind to the bones is the character file's Rig Mode, set " +
                 "on the .vsplat importer.")]
        [Header("Animation")]
        public RuntimeAnimatorController animatorController;

        /// <summary>
        /// Re-pose only every Nth frame. On skipped frames the splats hold their last pose, so
        /// both the skinning dispatch and the renderer's re-sort/re-projection are skipped.
        /// Per-character phase is staggered so a crowd's cost spreads across frames.
        /// </summary>
        [HideInInspector]
        [Tooltip("Re-pose (rebuild the skinning palette + run the GPU skin, and let the renderer " +
                 "re-sort / re-project) only every Nth frame. 1 = every frame (full rate). 2 = half " +
                 "rate, etc. On skipped frames the splats hold their last pose, so the GPU skin " +
                 "dispatch AND CalcViewData both skip for this character - cutting cost roughly " +
                 "proportionally. Per-character phase is staggered so the crowd's skinning load " +
                 "spreads across frames instead of spiking. Raise for dense/background crowds; keep " +
                 "at 1 for close hero characters where a reduced pose rate would be visible.")]
        public int poseEveryNFrames = 1;

        /// <summary>
        /// Re-sort the splats by depth only every Nth pose frame. Camera or character motion
        /// always re-sorts promptly regardless of this.
        /// </summary>
        [HideInInspector]
        [Tooltip("Re-sort the splats by depth only every Nth frame while animating (sort-LOD). 1 = " +
                 "every pose frame. Under a fixed camera the depth order drifts slowly, so 2-4 is " +
                 "usually invisible yet skips most of the per-frame radix sort. Camera or character " +
                 "motion always re-sorts promptly regardless of this. Per-character phase is staggered.")]
        public int sortEveryNFrames = 1;

        /// <summary>
        /// Keep the Animator and the GPU skin running while the character is frustum-culled by
        /// every camera. Off by default: the pose freezes and resumes on re-entry. Turn on when
        /// animation must stay in step while unseen (synchronized motion, animation events).
        /// Code-only, like the two rate knobs above.
        /// </summary>
        /// <remarks>
        /// The name echoes <see cref="SkinnedMeshRenderer.updateWhenOffscreen"/> but the meaning is
        /// not the same one. Unity's property recomputes bounds every frame so a mesh is not culled
        /// wrongly; this is the <see cref="AnimatorCullingMode.AlwaysAnimate"/> question - whether
        /// to keep evaluating while nothing can see the character.
        /// </remarks>
        [Tooltip("Keep the Animator and the GPU skin running while no camera can see this character " +
                 "(its renderer is frustum-culled). Off (default): both pause while off-screen - the " +
                 "pose freezes where it was and resumes on re-entry, saving the Animator evaluation, " +
                 "the bone-palette rebuild/upload, and the skinning dispatch for the whole off-screen " +
                 "crowd. Turn on when animation must stay in step while unseen (synchronized dances, " +
                 "gameplay driven by animation events).")]
        [UnityEngine.Serialization.FormerlySerializedAs("animateWhenOffscreen")]
        [HideInInspector]
        public bool updateWhenOffscreen = false;

        // Stable per-character phase so staggered characters re-pose on different frames.
        int m_PhaseOffset = -1;

        /// <summary>
        /// SplatSkinning.compute. Auto-resolved from the package in the editor; a ComputeShader
        /// cannot be looked up by name at runtime, so it has to be a serialized reference.
        /// </summary>
        [SerializeField, HideInInspector] internal ComputeShader skinningCompute;

        // The rest skeleton this character is skinned against, chosen from its .bin's bone count.
        // Per-character, not static: a scene can hold both rigs side by side.
        SplatSkeletonDef m_Skel;
        // The authored rig, when the importer emitted one. An FBX character's bones are part of
        // the asset, and after stage 3b a .vsplat's are too. When these are empty - a component
        // added by hand, or a character assigned from bytes at runtime - the rig is built at load
        // instead, which is what the package did for every character before the prefab existed.
        [SerializeField, HideInInspector] internal Transform rigRoot;
        [SerializeField, HideInInspector] internal Transform[] bones;
        [SerializeField, HideInInspector] internal Transform bonePrefix;   // the Generic "Armature" node, if any

        Transform m_RigRoot;
        Transform m_BonePrefix;   // the kGenericPathPrefix node, when inserted (Generic mode)
        Transform[] m_Bones;
        // The character file's Rig Mode, cached from the file this renderer points at. Resolved
        // before the rig is built, since both the "Armature" prefix node and the Generic bind-pose
        // handling branch on it.
        SplatRigMode m_RigMode = SplatRigMode.Humanoid;
        Avatar m_Avatar;
        // True when m_Avatar was built here rather than taken off the character file. The baked one
        // is a sub-asset shared by every instance of that file and must never be destroyed.
        bool m_OwnsAvatar;
        Animator m_Animator;
        Matrix4x4[] m_RestInv;
        // Palette-job data: inverse-bind matrices and output palette as native arrays, plus a
        // TransformAccessArray so the job reads bone poses off the main thread. Allocated in
        // PrepareRig, disposed in TeardownSkinning.
        NativeArray<float4x4> m_RestInvNative;
        NativeArray<float4x4> m_BoneStretch;
        NativeArray<BonePose> m_Palette;
        // One int, set by the palette job when any bone entry differs from last frame.
        NativeArray<int> m_PaletteChanged;
        TransformAccessArray m_BoneAccess;
        Vector3[] m_BoneRestLocalPositions;

        // Packed GPU layout: per-splat bind pose and bone binding share one buffer so the skinning
        // kernel binds only 4 SSBOs, the most GLES 3.1 guarantees per compute stage. Strides must
        // match the structs declared in SplatSkinning.compute exactly.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SplatBind { public Vector4 pos; public Vector4 rot; public uint4 idx; public Vector4 w; } // 64 bytes

        GraphicsBuffer m_SplatBind, m_BonePose, m_SkinPos, m_SkinRot;
        // Renamed from m_SkinSplatCount: SplatRenderer declares a private field of that name too.
        // Both are private so neither shadows the other in C#, but Unity's serializer walks
        // the hierarchy by NAME and rejects the duplicate outright:
        //   "The same field name is serialized multiple times ... Base(SkinnedSplatRenderer) m_SkinSplatCount"
        // which fails the player build. Distinct names also remove a real shadowing trap.
        int m_Kernel, m_SkinSplatCount, m_BoneCount;
        // m_Init is the SKINNING lifecycle (rig + buffers + animator live); m_SetupDone, over in
        // SkinnedSplatRenderer.cs, is the RENDER lifecycle (file parsed, asset built). They are not
        // the same thing: the render half sets up in edit mode too, where nothing skins, and the
        // skinning half can be refused outright by a GPU that rejects the kernel while the
        // character still renders at rest.
        bool m_Init, m_Active;
        // Latched when skinning cannot run on this device or with this setup, so LateUpdate stops
        // retrying a dispatch that will never complete. It replaces the `enabled = false` the
        // separate skinner component used: disabling the component now would take the whole
        // character with it, and the point of both failure paths is that the character keeps
        // rendering at its rest pose.
        bool m_SkinningDisabled;


        void LateUpdate()
        {
            // Edit mode drives the skin directly off the bone transforms, so moving a bone in the
            // Scene view deforms the splats. No Animator, no off-screen pause, no pose-rate LOD:
            // those are all about a running game, and Unity does not evaluate an Animator at edit
            // time either. Requires an authored rig, which PrepareRig has already enforced.
            if (!Application.isPlaying)
            {
                if (!UseAuthoredRig) return;
                if (!m_Init && !TryInit()) return;
                UpdatePaletteAndDispatch();
                return;
            }

            if (!m_Init && !TryInit()) return;

            // Off-screen cull: pause the Animator and skip all per-frame pose work while the
            // renderer is frustum-culled by every camera. The first pose always runs (m_Active is
            // still false), so a character that spawns off-screen never shows its bind pose.
            if (!updateWhenOffscreen)
            {
                bool visible = !m_Active || IsRecentlyVisible();
                if (m_Animator != null && m_Animator.enabled != visible)
                    m_Animator.enabled = visible;
                if (!visible) return;
            }
            else if (m_Animator != null && !m_Animator.enabled)
                m_Animator.enabled = true; // updateWhenOffscreen toggled on while paused

            // Animation LOD. The instance-id phase staggers the crowd so their pose frames don't
            // all land together.
            if (m_PhaseOffset < 0) m_PhaseOffset = GetInstanceID() & 0x7fffffff;
            if (poseEveryNFrames <= 1 || !m_Active || (Time.frameCount + m_PhaseOffset) % poseEveryNFrames == 0)
                UpdatePaletteAndDispatch();
        }

        // Wall time of the current TryInit, reported by FinishInit's diagnostic line.
        System.Diagnostics.Stopwatch m_SwInit;

        bool TryInit()
        {
            if (m_SkinningDisabled) return false;
            m_SwInit = System.Diagnostics.Stopwatch.StartNew();
            var bin = m_Bin;
            if (bin == null || !HasValidRenderSetup) return false; // wait for the render half
            if (skinningCompute == null)
            {
                Debug.LogError("SkinnedSplatRenderer: no SplatSkinning.compute - neither this " +
                    $"component nor the package's Resources/{SplatShaderResources.kResourceName}" +
                    ".asset supplied one, so this character cannot be skinned.", this);
                m_SkinningDisabled = true;
                TeardownSkinning(); // drop the rig Setup already built; nothing is going to drive it
                return false;
            }

            m_SkinSplatCount = bin.numSplats;
            // Everything downstream (rig build, palette, avatar, buffers) sizes itself off m_Skel,
            // so this one line is the whole difference between the two bin formats.
            ResolveSkeleton(bin);
            m_BoneCount = m_Skel.NumBones;

            // No kernel to find or probe on the no-compute path: the deformation runs in the
            // render vertex shader. Everything the block below does - locate CSBoneSkinning, log
            // the compute-limit snapshot, ask the GPU whether the kernel is usable - can only
            // report the absence that selected this path in the first place. The rig, Animator,
            // palette and bin grounding after it are shared and still run.
            if (!UseNoComputePath)
            {
            m_Kernel = skinningCompute.FindKernel("CSBoneSkinning");

            // Device and compute-limit snapshot: everything needed to diagnose a kernel that the
            // GPU refuses, in one line.
            if (SplatLog.Info) Debug.Log($"[SplatSkinning DIAG] api={SystemInfo.graphicsDeviceType} " +
                      $"gpu={SystemInfo.graphicsDeviceName} " +
                      $"vendor={SystemInfo.graphicsDeviceVendor} " +
                      $"driver={SystemInfo.graphicsDeviceVersion} " +
                      $"shaderLevel={SystemInfo.graphicsShaderLevel} " +
                      $"supportsCompute={SystemInfo.supportsComputeShaders} " +
                      $"maxComputeBufferInputs={SystemInfo.maxComputeBufferInputsCompute} " +
                      $"maxWorkGroupSize={SystemInfo.maxComputeWorkGroupSize} " +
                      $"maxWorkGroupSizeX={SystemInfo.maxComputeWorkGroupSizeX} " +
                      $"splatCount={m_SkinSplatCount} rig={m_Skel.Name} boneCount={m_BoneCount} " +
                      $"kernelSupported={skinningCompute.IsSupported(m_Kernel)}");

            // Some mobile GPUs reject the skinning kernel (too many SSBOs, or a workgroup size
            // above their limit). FindKernel still returns a valid-looking index, and dispatching
            // it silently leaves the output buffer unwritten, so the splats collapse. Check up
            // front and fall back to the asset's rest pose so the character at least loads.
            if (!skinningCompute.IsSupported(m_Kernel))
            {
                Debug.LogWarning("SkinnedSplatRenderer: the GPU rejected the skinning kernel " +
                    $"(api={SystemInfo.graphicsDeviceType}, gpu={SystemInfo.graphicsDeviceName}, " +
                    $"maxComputeBufferInputs={SystemInfo.maxComputeBufferInputsCompute})" +
                    ". Rendering the character at rest pose without GPU skinning.");
                m_SkinningActive = 0;            // show the asset's bind pose, not a collapsed buffer
                m_SkinningDisabled = true;       // stop retrying the failing dispatch every frame
                // Drop the rig we built; we are not going to skin. Only the SKINNING half - the
                // loaded character has to keep rendering, at rest, so the render half stands.
                TeardownSkinning();
                return false;
            }
            } // !UseNoComputePath

            // Normally already done, since Setup calls PrepareRig before it builds the asset. This
            // covers skinning initialised after the render half set up, where the asset already
            // exists and has to be rebuilt from the shifted bin.
            if (!m_RigPrepared)
            {
                PrepareRig(bin);
                if (m_RigPrepared) RebuildAssetFromBin();
            }

            // Play mode only. An Animator does not evaluate at edit time, and adding one from an
            // [ExecuteAlways] component would be a component the user did not ask for appearing in
            // their scene. The edit-mode path drives the bones directly instead.
            if (Application.isPlaying) SetupAnimator();
            CreateBuffers(bin);
            return FinishInit(bin);
        }

        // True once the rig exists and the bin has been grounded, so TryInit does not redo it.
        bool m_RigPrepared;

        // Cache the character file's Rig Mode. Must run before BuildRig: the "Armature" prefix node
        // and the Generic bind-pose capture both depend on it. A missing file keeps the default,
        // which is the Humanoid a freshly imported character gets.
        //
        // The mode lives on the file, not on this component, the same split Unity uses for meshes,
        // where Humanoid vs Generic is a property of the model and not of the SkinnedMeshRenderer.
        void ResolveRigMode()
        {
            m_RigMode = splatFile != null ? splatFile.rigMode : SplatRigMode.Humanoid;
        }

        // The rest skeleton this character poses on: the bundled table its bone count selects, with
        // the file's own rest translations merged over it where they can be trusted. See
        // SplatSkeleton.For and SplatCharacterRest.
        //
        // Resolved once and cached, because the merged rig is per-character data and not the shared
        // static def. TeardownSkinning clears it, so a bin swap picks the new file's rig.
        //
        // The IMPORTER resolves the same rig, off SplatCharacterBin.PeekRest, and bakes it into the
        // prefab's bones and Avatar. That is the path a character imported from disk actually
        // takes; this one covers a renderer given its bytes at runtime, or added by hand.
        void ResolveSkeleton(SplatCharacterBinData bin)
        {
            m_Skel ??= SplatSkeleton.For(bin.Rest());
        }

        // Build the bone hierarchy and capture the inverse-bind pose.
        //
        // Must run before Setup builds the SplatAsset: the asset's positions and bounds
        // drive the frustum cull and the sort's depth range, and the un-skinned paths (edit
        // preview and the rejected-kernel fallback) render the asset directly, so
        // anything applied only to the live rig would miss them.
        internal void PrepareRig(SplatCharacterBinData bin)
        {
            if (m_RigPrepared || bin == null) return;

            // Setup calls this before TryInit runs, so m_Skel is still null here even though
            // everything below resolves off it. Idempotent: TryInit resolves the identical
            // skeleton from the same bin.
            ResolveSkeleton(bin);

            // Edit mode skins only an AUTHORED rig. Rotating a bone should deform the character
            // the way it does on a SkinnedMeshRenderer, and now that the importer emits the bones
            // there is a rig sitting in the scene to rotate. Generating one here instead would
            // create 86 to 441 GameObjects inside the user's open scene, which they would then
            // save - so a hand-placed component with no authored rig still waits for Play.
            if (!Application.isPlaying && !UseAuthoredRig) return;

            m_BoneCount = m_Skel.NumBones;

            // Before BuildRig: the "Armature" prefix node and the bind-pose handling below both
            // branch on the mode.
            ResolveRigMode();

            var swRig = System.Diagnostics.Stopwatch.StartNew();
            BuildRig();
            s_LastRigMs = swRig.Elapsed.TotalMilliseconds;
            // Generic mode: re-pose the rig to the baked bind pose before capturing the
            // inverse-bind. The table expresses the same physical rest pose the rig was just built
            // in, but in the animation clips' bone-frame conventions, so capturing there makes M[b]
            // identity at bind for the clips that will actually drive the bones.
            // Skipped in edit mode: this re-poses the bones to the clips' bind frame, and doing
            // that to an authored rig would move transforms the user can see and save. Nothing
            // plays clips in edit mode anyway, so capturing the rest against the pose as authored
            // is both correct and what direct manipulation expects.
            bool bindPoseSampling = Application.isPlaying;
            if (bindPoseSampling && m_RigMode == SplatRigMode.Generic && !SplatSkeletonBindPose.HasData)
                Debug.LogWarning("SkinnedSplatRenderer: SplatSkeletonBindPose is unbaked - " +
                    "Generic parts will render with wrong rotations. Select the bind-pose clip " +
                    "(skel441_bindpose.fbx) and run Assets/Viggle/Bake Generic Bind Pose once.");
            if (bindPoseSampling && m_RigMode == SplatRigMode.Generic && SplatSkeletonBindPose.HasData)
            {
                // The rig is currently at the bin rest, the orientation the splat cloud is stored
                // in; the bind pose is the same physical pose globally re-oriented. Measure the
                // rigid delta between them so CaptureRest can carry the cloud onto the clips'
                // frame. The delta MUST be fit from joint POSITIONS: bone rotation frames differ
                // per bone between the two conventions, so no single bone's rotation delta
                // measures the global re-orientation.
                Vector3 P(string n) =>
                    transform.InverseTransformPoint(m_Bones[m_Skel.IndexOf(n)].position);
                Quaternion Frame() =>
                    Quaternion.LookRotation(
                        Vector3.Cross(P("upperarm_l") - P("upperarm_r"), P("head") - P("pelvis")),
                        P("head") - P("pelvis"));
                Vector3 pelB = P("pelvis"), handB = P("hand_l");
                Quaternion frameB = Frame();
                // Only the 86 body bones are baked, so on the full rig the facial bones keep their
                // rest-table locals. That is exact as long as no clip animates them: a facial
                // bone's local stays constant, so its palette entry works out to its body
                // ancestor's and the face rides the head rigidly. Animating the face would need a
                // 441-bone bake; see SplatSkeletonBindPose.
                SplatSkeletonBindPose.Apply(m_Bones, m_BonePrefix);
                Quaternion R = Frame() * Quaternion.Inverse(frameB);
                m_BindDelta = Matrix4x4.TRS(P("pelvis") - R * pelB, R, Vector3.one);
                // Mirror check: the delta must carry left onto left. If it lands the left hand on
                // the right one instead, the two conventions are mirrored and no rigid delta can
                // fix it - the fix belongs in the bind/animation export.
                Vector3 mapped = m_BindDelta.MultiplyPoint3x4(handB);
                float dL = (mapped - P("hand_l")).magnitude, dR = (mapped - P("hand_r")).magnitude;
                if (SplatLog.Verbose) Debug.Log($"bind delta: rot={R.eulerAngles:F1} hand_l err={dL:F3} " +
                          $"(vs hand_r {dR:F3}{(dR < dL ? " - MIRRORED!" : "")})");
                DumpRigDebug("bind capture");
            }
            CaptureRest();

            m_Palette = new NativeArray<BonePose>(m_BoneCount, Allocator.Persistent);
            m_PaletteChanged = new NativeArray<int>(1, Allocator.Persistent);
            m_RestInvNative = new NativeArray<float4x4>(m_BoneCount, Allocator.Persistent);
            m_BoneStretch = new NativeArray<float4x4>(m_BoneCount, Allocator.Persistent);
            m_BoneRestLocalPositions = new Vector3[m_BoneCount];
            for (int i = 0; i < m_BoneCount; i++)
            {
                m_RestInvNative[i] = m_RestInv[i];
                m_BoneStretch[i] = float4x4.identity;
                m_BoneRestLocalPositions[i] = m_Bones[i].localPosition;
            }
            m_BoneAccess = new TransformAccessArray(m_Bones);

            m_RigPrepared = true;
        }

        bool FinishInit(SplatCharacterBinData bin)
        {
            // Cross-buffer size audit. The renderer indexes _SplatSkinPos / _SplatSkinRot with
            // splat indices derived from its own splat count, so any disagreement about that count
            // makes every frame read out of bounds on the GPU - silent zeros on D3D11, a page fault
            // on Metal. Nothing else in the pipeline reports it, hence the check here.
            int rendererCount = splatCount;

            // The audit below is about the four skinning StructuredBuffers, none of which exists on
            // the no-compute path - the vertex shader skins straight from the bind data, so there
            // are no intermediate buffers to disagree about a count. Dereferencing them here threw
            // an NRE out of TryInit, which aborted init before skinning could activate: the
            // characters rendered, at rest pose, with no animation and no explanation.
            //
            // The one thing still worth checking is the count agreement itself.
            if (UseNoComputePath)
            {
                if (rendererCount != m_SkinSplatCount)
                    Debug.LogError("[SplatSkinning DIAG] SPLAT COUNT MISMATCH - the no-compute vertex " +
                                   $"shader indexes per-splat textures by splat index. skinner={m_SkinSplatCount} " +
                                   $"renderer={rendererCount}", this);
                m_Init = true;
                return true;
            }

            if (rendererCount != m_SkinSplatCount || m_SkinPos.count != m_SkinSplatCount || m_SkinRot.count != m_SkinSplatCount)
            {
                Debug.LogError($"[SplatSkinning DIAG] BUFFER SIZE MISMATCH - GPU reads will go out of bounds. " +
                               $"skinner={m_SkinSplatCount} renderer={rendererCount} " +
                               $"skinPos={m_SkinPos.count}x{m_SkinPos.stride}B skinRot={m_SkinRot.count}x{m_SkinRot.stride}B " +
                               $"bind={m_SplatBind.count}x{m_SplatBind.stride}B bonePose={m_BonePose.count}x{m_BonePose.stride}B", this);
            }
            else
            {
                // Strides matter as much as counts: _SplatSkinPos is a float4 buffer and must be 16
                // bytes. See the stride note in SplatSkinning.compute.
                if (SplatLog.Info) Debug.Log($"[SplatSkinning DIAG] buffers ok: splats={m_SkinSplatCount} bones={m_BoneCount} " +
                                 $"initMs={(m_SwInit != null ? m_SwInit.Elapsed.TotalMilliseconds : 0):F0} " +
                                 $"skinPos={m_SkinPos.count}x{m_SkinPos.stride}B (want 16) " +
                                 $"skinRot={m_SkinRot.count}x{m_SkinRot.stride}B (want 16) " +
                                 $"bind={m_SplatBind.stride}B (want 64) bonePose={m_BonePose.stride}B (want 80)");
            }

            m_Init = true;
            return true;
        }

        // Shared with the importer, which builds the identical hierarchy to derive the Avatar from.
        // See SplatRigBuilder for why the two must agree exactly. Note in particular that the rest
        // table's root bone carries the +Z forward yaw, which must stay on a bone and never move up
        // onto the rig root: the Humanoid Avatar is built FROM the rig root, so a yaw parked there
        // is invisible to HumanDescription and gets applied twice at retarget time.
        void BuildRig()
        {
            if (UseAuthoredRig)
            {
                m_RigRoot = rigRoot;
                m_BonePrefix = bonePrefix;
                m_Bones = bones;
                m_OwnsRig = false;
                return;
            }
            var rig = SplatRigBuilder.Build(transform, m_Skel, m_RigMode == SplatRigMode.Generic);
            m_RigRoot = rig.root;
            m_BonePrefix = rig.prefix;
            m_Bones = rig.bones;
            m_OwnsRig = true;
        }

        // An authored rig has to match the skeleton the file actually declares. A character file
        // swapped for one on the other rig (86 vs 441 bones) would otherwise skin against a bone
        // array of the wrong length and index past its end.
        bool UseAuthoredRig =>
            rigRoot != null && bones != null && m_Skel != null && bones.Length == m_Skel.NumBones;

        // False when the rig came from the prefab: it belongs to the instance, not to us, and
        // tearing down must not destroy the GameObjects the user can see in their own hierarchy.
        bool m_OwnsRig;

        // Attachment sockets, one per bone, created on demand. Children of the bone, so they die
        // with the rig.
        Dictionary<int, Transform> m_Sockets;

        /// <summary>
        /// Every bone name on this character's rig, in skeleton order. Empty until the character
        /// has loaded - see <see cref="IsLoaded"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="AttachToBone"/> and <see cref="GetBoneSocket"/> take a bone NAME, so without
        /// this there was no way to discover a valid one short of reading the package source. Which
        /// names exist depends on the character file: the body rig carries 86, the body-and-face rig
        /// 441, and the two agree bone-for-bone over the body, so anything resolved by name behaves
        /// identically on either.
        /// </remarks>
        public IReadOnlyList<string> BoneNames =>
            m_Skel != null ? m_Skel.Names : System.Array.Empty<string>();

        /// <summary>
        /// Applies non-propagating per-bone proportions. Each value is
        /// (length along the bone, width, thickness); omitted bones remain unchanged.
        /// Passing null clears the stretch.
        /// </summary>
        [UnityEngine.Scripting.Preserve]
        public bool SetBoneStretch(IReadOnlyDictionary<string, Vector3> factors)
        {
            if (!m_RigPrepared || m_Bones == null || !m_BoneStretch.IsCreated) return false;

            var scale = new Vector3[m_BoneCount];
            for (int i = 0; i < scale.Length; i++) scale[i] = Vector3.one;
            if (factors != null)
                foreach (var pair in factors)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    int i = m_Skel.IndexOf(pair.Key);
                    if (i >= 0) scale[i] = SafeScale(pair.Value);
                }

            // Segment length lives in the child's local translation, so each parent changes only
            // its own segment instead of scale compounding down the hierarchy.
            for (int i = 0; i < m_BoneCount; i++)
            {
                int parent = m_Skel.Parents[i];
                float along = parent >= 0 ? scale[parent].x : 1f;
                m_Bones[i].localPosition = m_BoneRestLocalPositions[i] * along;
            }

            for (int i = 0; i < m_BoneCount; i++)
                m_BoneStretch[i] = BoneStretchMatrix(i, scale);
            m_Active = false;
            return true;
        }

        static Vector3 SafeScale(Vector3 value) => new(
            SafeScale(value.x), SafeScale(value.y), SafeScale(value.z));

        static float SafeScale(float value) =>
            value > 0f && !float.IsNaN(value) && !float.IsInfinity(value) ? value : 1f;

        Matrix4x4 BoneStretchMatrix(int bone, Vector3[] scale)
        {
            Vector3 s = scale[bone];
            if (s == Vector3.one) return Matrix4x4.identity;

            int child = -1;
            float longest = -1f;
            for (int i = 0; i < m_BoneCount; i++)
            {
                if (m_Skel.Parents[i] != bone) continue;
                if (scale[i] != Vector3.one) { child = i; break; }
                float length = m_BoneRestLocalPositions[i].sqrMagnitude;
                if (length > longest) { child = i; longest = length; }
            }

            Vector3 axis = child >= 0
                ? m_BoneRestLocalPositions[child]
                : m_BoneRestLocalPositions[bone];
            if (axis.sqrMagnitude < 1e-12f) return Matrix4x4.identity;
            axis.Normalize();

            Vector3 seed = Mathf.Abs(axis.x) <= Mathf.Abs(axis.y) && Mathf.Abs(axis.x) <= Mathf.Abs(axis.z)
                ? Vector3.right
                : Mathf.Abs(axis.y) <= Mathf.Abs(axis.z) ? Vector3.up : Vector3.forward;
            Vector3 across = (seed - Vector3.Dot(seed, axis) * axis).normalized;
            Vector3 depth = Vector3.Cross(axis, across);
            var result = Matrix4x4.zero;
            AddOuter(ref result, axis, s.x);
            AddOuter(ref result, across, s.y);
            AddOuter(ref result, depth, s.z);
            result.m33 = 1f;
            return result;
        }

        static void AddOuter(ref Matrix4x4 m, Vector3 v, float weight)
        {
            m.m00 += weight * v.x * v.x; m.m01 += weight * v.x * v.y; m.m02 += weight * v.x * v.z;
            m.m10 += weight * v.y * v.x; m.m11 += weight * v.y * v.y; m.m12 += weight * v.y * v.z;
            m.m20 += weight * v.z * v.x; m.m21 += weight * v.z * v.y; m.m22 += weight * v.z * v.z;
        }

        /// <summary>
        /// The shared source skeleton's parent-local rest transform for a bone, before this
        /// character file's per-character joint translations are merged into the live rig.
        /// </summary>
        /// <remarks>
        /// Shared skeletal clips are authored as absolute poses on this canonical skeleton. A
        /// caller retargeting one onto a corrected character needs to subtract this source rest,
        /// then compose the resulting delta with the live bone's own rest transform. Reading the
        /// live bone alone cannot recover the source rest once per-character joints were merged.
        /// </remarks>
        public bool TryGetCanonicalBoneRest(string boneName, out Vector3 localPosition,
                                            out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            if (m_Skel == null || string.IsNullOrEmpty(boneName)) return false;

            SplatSkeletonDef canonical = SplatSkeleton.For(m_Skel.NumBones);
            int i = canonical.IndexOf(boneName);
            if (i < 0) return false;
            localPosition = canonical.LocalPosition(i);
            localRotation = canonical.LocalRotation(i);
            return true;
        }

        /// <summary>
        /// True when this character's rig has a bone by that name. Cheaper than catching the
        /// warning from <see cref="GetBoneSocket"/>, and usable before attaching anything.
        /// </summary>
        public bool HasBone(string boneName) => m_Skel != null && m_Skel.IndexOf(boneName) >= 0;

        /// <summary>
        /// Attaches <paramref name="obj"/> to a bone so it keeps the size and orientation it was
        /// authored at and then rigidly follows that bone.
        /// </summary>
        /// <remarks>
        /// Prefer this over parenting to the bone Transform directly. Bone frames follow the source
        /// rig's conventions, and in Generic mode the armature node carries the FBX unit conversion
        /// as a scale of 100 (paired with 1/100 bone translations, so the world pose is correct but
        /// the raw transforms are not). Splats never notice, because the skinning palette discards
        /// scale, but anything parented to a bone inherits both. The socket cancels them: unit scale
        /// relative to the character, and axes aligned with the character at attach time.
        /// </remarks>
        /// <returns>False if the rig is not built yet or the bone name is unknown.</returns>
        public bool AttachToBone(string boneName, Transform obj, Vector3 localOffset = default,
                                 Quaternion localRotation = default)
        {
            if (obj == null) return false;
            var socket = GetBoneSocket(boneName);
            if (socket == null) return false;
            obj.SetParent(socket, false);
            obj.localPosition = localOffset;
            obj.localRotation = localRotation == default ? Quaternion.identity : localRotation;
            return true;
        }

        /// <summary>
        /// The socket Transform for a bone, created on first request. Attach to this rather than to
        /// the bone itself; see <see cref="AttachToBone"/> for why. Sockets are children of the
        /// bone, so they follow it and are destroyed with it.
        /// </summary>
        /// <returns>Null if the rig is not built yet or the bone name is unknown.</returns>
        public Transform GetBoneSocket(string boneName)
        {
            if (!m_Init && !TryInit())
            {
                Debug.LogWarning($"SkinnedSplatRenderer: cannot attach to '{boneName}' - the rig is " +
                                 "not initialised yet. Call this after the character has loaded.", this);
                return null;
            }
            int i = m_Skel.IndexOf(boneName);
            if (i < 0 || m_Bones == null || i >= m_Bones.Length || m_Bones[i] == null)
            {
                Debug.LogWarning($"SkinnedSplatRenderer: no bone named '{boneName}' on this rig.", this);
                return null;
            }

            m_Sockets ??= new Dictionary<int, Transform>();
            if (m_Sockets.TryGetValue(i, out var cached) && cached != null)
                return cached;

            var bone = m_Bones[i];
            var t = new GameObject($"Socket_{boneName}").transform;
            t.SetParent(bone, false);

            // Cancel whatever scale this bone chain carries so the socket ends up at the
            // character's own scale. The chain is uniformly scaled (only the armature node scales
            // it), so a component-wise ratio is exact here.
            Vector3 boneScale = bone.lossyScale, charScale = transform.lossyScale;
            t.localScale = new Vector3(SafeRatio(charScale.x, boneScale.x),
                                       SafeRatio(charScale.y, boneScale.y),
                                       SafeRatio(charScale.z, boneScale.z));
            // Align the socket's axes with the character's. Assigning world rotation resolves to
            // whatever local rotation cancels the bone's frame; that local is then fixed, so the
            // socket rotates rigidly with the bone from here on.
            t.rotation = transform.rotation;

            m_Sockets[i] = t;
            return t;
        }

        /// <summary>
        /// The live Transform for a bone, or null if the rig is not built or the bone is unknown.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="GetBoneSocket"/> this hands back the bone ITSELF, with the rig's own
        /// scale and axis frame, and creates nothing. That is what a SkinnedMeshRenderer needs in
        /// its bones array - a socket would skin the mesh against a re-oriented, re-scaled frame.
        /// Use GetBoneSocket to hang props off the character; use this to bind another skinned
        /// mesh (a relighting proxy) to the same skeleton.
        ///
        /// The skeleton is built at runtime, so this returns null until the character has loaded.
        /// </remarks>
        public Transform GetBone(string boneName)
        {
            if (!m_Init && !TryInit())
                return null;
            int i = m_Skel.IndexOf(boneName);
            if (i < 0 || m_Bones == null || i >= m_Bones.Length)
                return null;
            return m_Bones[i];
        }

        /// <summary>True once the bone hierarchy exists and <see cref="GetBone"/> can resolve.</summary>
        public bool RigReady => m_Init && m_Bones != null;

        // For SplatRelight: the rig transforms indexed exactly as the bin's per-splat bone
        // indices address them, and the inverse-bind matrices the compute skinning uses. A
        // SkinnedMeshRenderer given these as bones/bindposes deforms in lockstep with the
        // splats (SMR applies the bones' full localToWorld where the compute path uses the
        // rigid part only - identical unless something scales a bone at runtime).
        internal Transform[] Bones => m_Bones;
        internal Matrix4x4[] RestInv => m_RestInv;

        static float SafeRatio(float num, float den) => Mathf.Abs(den) < 1e-9f ? 1f : num / den;

        // Attach an Animator to the rig. Humanoid mode needs an Avatar so any Humanoid clip
        // retargets onto the bones; Generic mode plays clips straight onto the bone Transforms.
        void SetupAnimator()
        {
            // An authored rig ships its own Animator, and it may already carry a controller set in
            // the inspector. Adopt it even when this component has none of its own, or the
            // off-screen pause in LateUpdate has nothing to disable and the whole crowd keeps
            // evaluating while unseen.
            if (animatorController == null)
            {
                if (m_RigRoot != null) m_Animator = m_RigRoot.GetComponent<Animator>();
                return;
            }

            if (m_RigMode == SplatRigMode.Humanoid)
            {
                // Prefer the avatar the importer baked as a sub-asset of the character file: it is
                // built from the identical rig (SplatRigBuilder) and shared by every instance, so a
                // crowd of the same character pays for it once instead of once each.
                var baked = splatFile != null ? splatFile.avatar : null;
                if (baked != null && baked.isValid)
                {
                    m_Avatar = baked;
                    m_OwnsAvatar = false;
                }
                else
                {
                    // No baked avatar: a file imported before the importer built them, or one
                    // assigned from raw bytes at runtime. Build one here so the character still
                    // animates, and say so - it is per-instance work the import should have done.
                    if (SplatLog.Warning)
                        Debug.LogWarning($"SkinnedSplatRenderer: '{name}' has no baked Avatar, so one is " +
                            "being built per instance. Reimport the character file to bake it.", this);

                    // Build with the rig root at the origin, whatever the live rig's offset.
                    // SplatHumanoidAvatar.Build copies root.localPosition straight into the
                    // HumanDescription as the root's bind pose, so an offset left on this node is
                    // baked into the avatar and still applied by the live transform - counted
                    // twice, and the character floats by that offset the moment a Humanoid clip
                    // retargets onto it.
                    Vector3 lifted = m_RigRoot.localPosition;
                    m_RigRoot.localPosition = Vector3.zero;
                    m_Avatar = SplatHumanoidAvatar.Build(m_RigRoot.gameObject, m_Skel); // T-pose correction always on
                    m_RigRoot.localPosition = lifted;
                    m_OwnsAvatar = true;
                }
                if (m_Avatar == null || !m_Avatar.isValid)
                {
                    Debug.LogError("SkinnedSplatRenderer: failed to build a valid Humanoid Avatar from the rig.");
                    return;
                }
                if (SplatLog.Info) Debug.Log("SkinnedSplatRenderer: Humanoid avatar " +
                          $"({(m_OwnsAvatar ? "built at load" : "baked at import")}), " +
                          $"translationDoF={m_Avatar.humanDescription.hasTranslationDoF}");
            }

            // The prefab ships an Animator on the rig root, so reuse it rather than adding a
            // second one - AddComponent would silently fail on a GameObject that already has one.
            m_Animator = m_RigRoot.GetComponent<Animator>();
            if (m_Animator == null) m_Animator = m_RigRoot.gameObject.AddComponent<Animator>();
            m_Animator.avatar = m_Avatar; // null in Generic mode
            m_Animator.applyRootMotion = false;
            // AlwaysAnimate because Unity's own culling keys off Renderer visibility and splat
            // characters have no Renderer component, so it would treat them as never visible. The
            // frustum-based pause in LateUpdate replaces it.
            m_Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // Survive that pause: LateUpdate disables the component while culled, and without this
            // the state machine rewinds to its default state on every re-entry.
            m_Animator.keepAnimatorStateOnDisable = true;
            m_Animator.runtimeAnimatorController = animatorController;
        }

        // Inverse-bind: bone rest transform in this object's space.
        // Captured right after BuildRig (bones at rest) so M[b] == identity at rest.
        void CaptureRest()
        {
            m_RestInv = new Matrix4x4[m_BoneCount];
            for (int i = 0; i < m_BoneCount; i++)
            {
                // Rigid capture (position + rotation only), mirroring UpdatePaletteAndDispatch: a
                // sampled bind-pose clip can park unit-conversion scale on the armature prefix
                // node, and letting that into restInv rescales every splat offset. m_BindDelta
                // (identity outside Generic bind sampling) re-orients the splat cloud from the bin
                // rest frame into the clips' bind frame, so the palette maps cloud to current pose
                // in one step.
                m_RestInv[i] = CharSpaceRigid(i).inverse * m_BindDelta;
            }
        }

        Matrix4x4 m_BindDelta = Matrix4x4.identity;

        // Bone i's rigid transform (position + rotation, unit scale) in character space.
        Matrix4x4 CharSpaceRigid(int i)
        {
            var bone = m_Bones[i];
            return transform.worldToLocalMatrix * Matrix4x4.TRS(bone.position, bone.rotation, Vector3.one);
        }

        // ---- UseNoComputePath skinning -------------------------------------------------------
        // GLES 3.0 has neither the skinning kernel nor a vertex-stage SSBO to read its output
        // from, so the palette goes to the vertex shader as a texture and the deformation happens
        // there (see RenderGaussianSplatsNoCompute.shader). 4 RGBAFloat texels per bone: matrix
        // rows 0..2, then the delta-quat.
        Texture2D m_BonePoseTex;
        Vector4[] m_BonePoseTexels;

        const int kBonePoseTexelsPerBone = 4;

        void CreateBonePoseTexture()
        {
            m_BonePoseTexels = new Vector4[m_BoneCount * kBonePoseTexelsPerBone];
            m_BonePoseTex = new Texture2D(m_BoneCount * kBonePoseTexelsPerBone, 1,
                                          TextureFormat.RGBAFloat, false, true)
            {
                name = "SplatNCBonePose",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            m_NoComputeBonePoseTex = m_BonePoseTex; // the renderer binds it; this half owns it
        }

        float4[] m_SortDepthRows;

        // Per-bone depth rows for the CPU sorter. A skinned splat's world position is
        // localToWorld * M_b * bindPos, so its view-space depth is row 2 of
        // (worldToCamera * localToWorld * M_b) dotted with the raw bind position - which is what
        // lets the sorter stay one dot product per splat instead of a full skin.
        //
        // Approximate by construction: each splat uses its DOMINANT bone only. Splat sorting is
        // already approximate and throttled, so this is in keeping with the compute path.
        internal override bool BuildNoComputeDepthRows(Camera cam, out float4[] rows)
        {
            if (m_NoComputeSkinningActive == 0 || !m_Palette.IsCreated)
                return base.BuildNoComputeDepthRows(cam, out rows); // bind pose: one shared row

            if (m_SortDepthRows == null || m_SortDepthRows.Length != m_BoneCount)
                m_SortDepthRows = new float4[m_BoneCount];

            Matrix4x4 vm = cam.worldToCameraMatrix * transform.localToWorldMatrix;
            for (int b = 0; b < m_BoneCount; b++)
            {
                float4x4 m = m_Palette[b].m;
                // Matrix4x4's constructor takes COLUMNS, and float4x4 stores columns, so c0..c3
                // map straight across with no transpose - unlike the bone-pose texture, which
                // needs rows. See UploadBonePoseTexture.
                Matrix4x4 f = vm * new Matrix4x4(m.c0, m.c1, m.c2, m.c3);
                m_SortDepthRows[b] = new float4(f.m20, f.m21, f.m22, f.m23);
            }
            rows = m_SortDepthRows;
            return true;
        }

        // Publishes m_Palette to m_BonePoseTex. The shader reconstructs the position with
        // dot(row.xyz, p) + row.w, i.e. mul(M, float4(p,1)) - so it needs M's ROWS, while
        // float4x4 stores COLUMNS (c0..c3), which is what CSBoneSkinning's mul(M, v) consumes.
        // Hence the transpose: row j is (c0[j], c1[j], c2[j], c3[j]).
        void UploadBonePoseTexture()
        {
            for (int b = 0; b < m_BoneCount; b++)
            {
                BonePose bp = m_Palette[b];
                float4x4 t = math.transpose(bp.m);
                int o = b * kBonePoseTexelsPerBone;
                m_BonePoseTexels[o + 0] = t.c0;
                m_BonePoseTexels[o + 1] = t.c1;
                m_BonePoseTexels[o + 2] = t.c2;
                m_BonePoseTexels[o + 3] = bp.dq;
            }
            m_BonePoseTex.SetPixelData(m_BonePoseTexels, 0);
            m_BonePoseTex.Apply(false, false);
        }

        void CreateBuffers(SplatCharacterBinData bin)
        {
            if (UseNoComputePath)
            {
                // Only the palette texture. The bind data lives in SplatNoComputeData's textures
                // and the skinned result never leaves the vertex shader, so none of the four
                // StructuredBuffers below has a consumer on this path.
                CreateBonePoseTexture();
                return;
            }

            m_SplatBind = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SkinSplatCount, 64);
            m_BonePose = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_BoneCount, 80);
            // 16, not 12: _SkinPos is a float4 buffer (.w unused). A 12-byte float3 stride reads
            // out of bounds on the GPU - a page fault on Metal. See SplatSkinning.compute.
            m_SkinPos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SkinSplatCount, 16);
            m_SkinRot = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SkinSplatCount, 16);

            // The bind pose already faces +Z (SplatCharacterBin.Parse converted it, see
            // SplatConvention), so it lines up with the rig and is copied straight through and
            // skinned as standard LBS. pos.w is unused padding; float4 avoids the GLES std430
            // vec3-array stride mismatch.
            var bind = new SplatBind[m_SkinSplatCount];
            uint boneLimit = (uint)m_BoneCount;   // _BonePose is sized to the rig's bone count
            uint maxBoneIdx = 0;
            for (int i = 0; i < m_SkinSplatCount; i++)
            {
                float3 p = bin.positions[i];
                float4 q = bin.rotations[i].value;
                bind[i].pos = new Vector4(p.x, p.y, p.z, 1f);
                bind[i].rot = new Vector4(q.x, q.y, q.z, q.w);
                // Clamp bone indices to the palette size. The skinning shader reads _BonePose[idx]
                // unconditionally, before the weight multiply, so an index >= boneCount is an
                // out-of-bounds GPU read - silent zeros on D3D11, a page fault on Metal. A .bin
                // whose bone count matches no known rig, or one carrying a zero-weight "no bone"
                // sentinel, will do exactly that. The warning below reports it.
                uint4 bi = bin.boneIndices[i];
                maxBoneIdx = math.max(maxBoneIdx, math.cmax(bi));
                bind[i].idx = math.min(bi, new uint4(boneLimit - 1u));
                bind[i].w = (Vector4)bin.boneWeights[i];
            }
            m_SplatBind.SetData(bind);
            if (maxBoneIdx >= boneLimit)
                Debug.LogWarning($"[SplatSkinning] .bin carries bone index {maxBoneIdx} >= the {m_Skel.Name} " +
                                 $"rig's {boneLimit} bones (clamped to {boneLimit - 1}); those splats will " +
                                 "collapse onto the last bone. Check the .bin's numBones against its weights.", this);
        }

        void UpdatePaletteAndDispatch()
        {
            // Rigid bone matrices (position + rotation, scale discarded). The skeleton is rigid, but
            // Generic clips imported from FBX can carry the unit/axis conversion as a uniform scale
            // on the armature prefix node - scale 100 with 1/100 bone translations, world pose
            // correct, raw matrices not - so localToWorldMatrix would inflate every splat offset
            // 100x. The job completes immediately; the upload below needs its result this frame.
            m_PaletteChanged[0] = 0;
            SplatSkinningJobs.BuildPalette(transform.worldToLocalMatrix, m_RestInvNative, m_BoneStretch,
                m_Palette, m_BoneAccess, m_PaletteChanged);

            // Nothing moved, so there is nothing to re-skin. A character with no Animator - or one
            // whose Animator is idle, or paused - stops paying for the palette upload and the
            // dispatch entirely, which is what lets one .vsplat serve an animated character and a
            // still one at the right price, the way one FBX does.
            //
            // Deliberately does NOT stamp m_LastPosedFrame. The renderer re-sorts and re-projects
            // only on frames the splats actually moved, so a character that stops posing also stops
            // sorting, and the whole object converges to nearly free without either half knowing
            // about the other.
            //
            // The first pose always runs: m_Active is still false until the splats have been
            // skinned once, and until then the renderer would be showing the un-skinned bind pose.
            //
            // Note the palette is character-RELATIVE (worldToLocal cancels the character's own
            // transform), so moving or rotating the whole character correctly counts as no change.
            if (m_Active && m_PaletteChanged[0] == 0)
                return;

            // Everything above this point - the palette job, the did-anything-move check, the
            // first-pose rule - and everything below it is shared by both paths on purpose, so
            // the two cannot disagree about when a character re-skins or when it re-sorts. Only
            // the "how do the posed splats reach the renderer" step differs.
            if (UseNoComputePath)
            {
                // No kernel to dispatch: publish the palette and let the render vertex shader do
                // the deformation itself. No skin-position/rotation overrides either - there is
                // no intermediate buffer on this path, the vertex shader skins straight from the
                // bind data, so m_SkinningActive stays 0 and the renderer binds the palette
                // texture instead (see m_NoComputeSkinningActive).
                UploadBonePoseTexture();
                m_NoComputeSkinningActive = 1;
            }
            else
            {
                m_BonePose.SetData(m_Palette);

                int activeCount = m_SkinSplatCount;

                var cs = skinningCompute;
                cs.SetInt("_SplatCount", activeCount);
                cs.SetInt("_BoneCount", m_BoneCount); // palette length, for the bone-index clamp
                cs.SetBuffer(m_Kernel, "_SplatBind", m_SplatBind);
                cs.SetBuffer(m_Kernel, "_BonePose", m_BonePose);
                cs.SetBuffer(m_Kernel, "_SkinPos", m_SkinPos);
                cs.SetBuffer(m_Kernel, "_SkinRot", m_SkinRot);
                cs.GetKernelThreadGroupSizes(m_Kernel, out uint gsX, out _, out _);
                cs.Dispatch(m_Kernel, (activeCount + (int)gsX - 1) / (int)gsX, 1, 1);

                // Per-instance skin hooks, so multiple characters do not collide.
                m_SkinPosOverride = m_SkinPos;
                m_SkinRotOverride = m_SkinRot;
                m_SkinningActive = 1;
            }
            // Stamp the frame the splats actually moved on. The renderer re-sorts and re-projects
            // only on those frames and reuses the previous result otherwise; read back in
            // GaussianSplatRenderSystem.SortAndRenderSplats.
            m_LastPosedFrame = Time.frameCount;
            m_SortNthFrame = Mathf.Max(1, sortEveryNFrames);
            m_Active = true;

            if (++m_DebugFrames == 10) DumpRigDebug("playback f10");
        }

        int m_DebugFrames;

        // Recompute the palette with managed math and return the largest absolute element
        // difference against the job's current output. Verification hook; free unless called.
        internal float DebugPaletteMaxErrorVsManaged()
        {
            if (!m_Init || !m_Palette.IsCreated) return -1f;
            Matrix4x4 w2l = transform.worldToLocalMatrix;
            float maxErr = 0f;
            for (int b = 0; b < m_BoneCount; b++)
            {
                var bone = m_Bones[b];
                Matrix4x4 rigid = w2l * Matrix4x4.TRS(bone.position, bone.rotation, Vector3.one)
                    * m_RestInv[b];
                Matrix4x4 M = w2l * Matrix4x4.TRS(bone.position, bone.rotation, Vector3.one)
                    * (Matrix4x4)m_BoneStretch[b] * m_RestInv[b];
                Quaternion q = rigid.rotation;
                Matrix4x4 jm = m_Palette[b].m;
                for (int e = 0; e < 16; e++)
                    maxErr = Mathf.Max(maxErr, Mathf.Abs(M[e] - jm[e]));
                float4 jq = m_Palette[b].dq;
                // Quaternion sign is arbitrary; compare against both hemispheres.
                float dqErr = Mathf.Min(
                    Mathf.Max(Mathf.Abs(q.x - jq.x), Mathf.Max(Mathf.Abs(q.y - jq.y), Mathf.Max(Mathf.Abs(q.z - jq.z), Mathf.Abs(q.w - jq.w)))),
                    Mathf.Max(Mathf.Abs(q.x + jq.x), Mathf.Max(Mathf.Abs(q.y + jq.y), Mathf.Max(Mathf.Abs(q.z + jq.z), Mathf.Abs(q.w + jq.w)))));
                maxErr = Mathf.Max(maxErr, dqErr);
            }
            return maxErr;
        }

        // Camera.main does a tagged-object scene search, so cache it: one lookup per frame, shared
        // by every character.
        static Camera s_MainCam;
        static int s_MainCamFrame = -1;

        // Last PrepareRig split, in ms, for the load-timing line in Setup. A static rather than a
        // return value: PrepareRig is called for its side effect on the bin, and this type's public
        // signatures are a stable ABI.
        internal static double s_LastRigMs;

        static Camera CachedMainCamera()
        {
            if (s_MainCamFrame != Time.frameCount || s_MainCam == null)
            {
                s_MainCam = Camera.main;
                s_MainCamFrame = Time.frameCount;
            }
            return s_MainCam;
        }

        // Clears the skinning half's process-wide cached state. Called by ResetStatics, which lives
        // with the render half's statics in SkinnedSplatRenderer.cs.
        static void ResetSkinningStatics()
        {
            s_MainCam = null;
            s_MainCamFrame = -1;
        }

        // Prints the path-prefix node's TRS (where the unit/axis conversion lands) and each key
        // bone's position and orientation in character space. Called once at bind capture and once
        // during playback: a constant rotation difference between the two on the same bone means
        // the bind-pose clip and the animation clip disagree on conventions.
        void DumpRigDebug(string label)
        {
            var sb = new System.Text.StringBuilder($"SkinnedSplatRenderer rig debug [{label}]:\n");
            if (m_Bones[0] != null && m_Bones[0].parent != m_RigRoot)
            {
                var pfx = m_Bones[0].parent;
                sb.AppendLine($"prefix '{pfx.name}': localPos={pfx.localPosition:F4} " +
                              $"localEuler={pfx.localRotation.eulerAngles:F2} localScale={pfx.localScale:F4}");
            }
            Quaternion charInv = Quaternion.Inverse(transform.rotation);
            foreach (var name in new[] { "pelvis", "head", "hand_l", "foot_l" })
            {
                int i = m_Skel.IndexOf(name);
                if (i < 0 || m_Bones[i] == null) continue;
                Vector3 charPos = transform.InverseTransformPoint(m_Bones[i].position);
                Vector3 charEuler = (charInv * m_Bones[i].rotation).eulerAngles;
                string pal = m_Palette.IsCreated ? $" paletteScale={((Matrix4x4)m_Palette[i].m).lossyScale:F4}" : "";
                string loc = "";
                sb.AppendLine($"{name}: charPos={charPos:F4} charEuler={charEuler:F2}{pal}{loc}");
            }
            if (SplatLog.Verbose) Debug.Log(sb.ToString());
        }

        // Releases the SKINNING half only: rig, animator, avatar, GPU buffers, native arrays.
        //
        // Separate from the full Teardown because TryInit's rejected-kernel path has to drop the rig
        // while LEAVING the loaded character rendering at its rest pose - tearing the render half
        // down there would unload the character the fallback exists to keep on screen.
        //
        // Internal order matters: the skin overrides handed to the render half are cleared FIRST,
        // so no draw can read a GraphicsBuffer between its Dispose and the renderer being told to
        // stop using it.
        void TeardownSkinning()
        {
            if (m_Active)
            {
                m_SkinningActive = 0;
                m_SkinPosOverride = null;
                m_SkinRotOverride = null;
                ClearLod(); // stop LODding to a stale top-K prefix once we stop driving it
                m_Active = false;
            }

            m_SplatBind?.Dispose(); m_BonePose?.Dispose();
            m_SkinPos?.Dispose(); m_SkinRot?.Dispose();
            m_SplatBind = m_BonePose = m_SkinPos = m_SkinRot = null;

            m_NoComputeSkinningActive = 0;
            m_NoComputeBonePoseTex = null;   // drop the renderer's pointer before freeing it
            if (m_BonePoseTex != null)
            {
                if (Application.isPlaying) Destroy(m_BonePoseTex);
                else DestroyImmediate(m_BonePoseTex);
                m_BonePoseTex = null;
            }
            m_BonePoseTexels = null;

            if (m_Palette.IsCreated) m_Palette.Dispose();
            if (m_PaletteChanged.IsCreated) m_PaletteChanged.Dispose();
            if (m_RestInvNative.IsCreated) m_RestInvNative.Dispose();
            if (m_BoneStretch.IsCreated) m_BoneStretch.Dispose();
            if (m_BoneAccess.isCreated) m_BoneAccess.Dispose();

            if (m_RigRoot != null && m_OwnsRig)
            {
                if (Application.isPlaying) Destroy(m_RigRoot.gameObject);
                else DestroyImmediate(m_RigRoot.gameObject);
            }
            m_RigRoot = null;
            m_OwnsRig = false;
            m_Animator = null;
            // Only destroy an avatar we built. The baked one is a sub-asset of the character file,
            // shared by every instance, and destroying it would take the whole project's copies of
            // that character down with this one.
            if (m_Avatar != null && m_OwnsAvatar)
            {
                if (Application.isPlaying) Destroy(m_Avatar);
                else DestroyImmediate(m_Avatar);
            }
            m_Avatar = null;
            m_OwnsAvatar = false;
            // The next init picks the skeleton again from the file, which may be a different one
            // after a bin swap.
            m_Skel = null;
            m_Bones = null;
            m_RestInv = null;
            m_BoneRestLocalPositions = null;
            m_BonePrefix = null;
            // Destroying the rig already took the sockets with it; drop the dangling lookup so the
            // next init rebuilds them against the new bones.
            m_Sockets?.Clear();
            m_PhaseOffset = -1;
            m_DebugFrames = 0;
            m_RigPrepared = false;
            m_Init = false;
        }

    }
}
