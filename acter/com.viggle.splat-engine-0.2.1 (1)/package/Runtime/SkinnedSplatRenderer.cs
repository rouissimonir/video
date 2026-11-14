// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using GaussianSplatting.Runtime;   // SplatNoComputeData, the UseNoComputePath source textures

namespace Viggle.SplatEngine
{
    /// <summary>
    /// Loads a splat character file, renders it, and animates it with GPU linear-blend skinning.
    /// The splat analogue of <see cref="SkinnedMeshRenderer"/>: it holds the character asset, owns
    /// the bone hierarchy and draws the deformed result. Add this component, assign a file, press
    /// Play; assign an <see cref="animatorController"/> to animate it.
    /// </summary>
    /// <remarks>
    /// A character IS a <see cref="SplatRenderer"/> - it renders the same way a static capture
    /// does, and only differs in what it loads and how it poses it. Everything on the base type
    /// (<see cref="baseColor"/>, <see cref="opacityScale"/>, <see cref="Loaded"/>,
    /// <see cref="TryGetWorldBounds"/>, ...) applies here unchanged.
    /// <para>
    /// Split across two files: this one loads the file and drives the renderer,
    /// SkinnedSplatRenderer.Skinning.cs builds the rig and runs the per-frame skin.
    /// </para>
    /// Hot-reload stable ABI: Unity message methods and serialized fields must stay stable.
    /// </remarks>
    [ExecuteAlways] // so it can load + render in the editor (see SplatSettings.renderInEditor)
    [DisallowMultipleComponent]
    [AddComponentMenu("Viggle Splat Engine/Skinned Splat Renderer")]
    // Belt and braces for the SplatCharacterViewer rename: scenes and prefabs resolve the script by
    // the .cs.meta GUID, which is unchanged, but anything that resolves the type by name is covered
    // by this.
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, null, null, "SplatCharacterViewer")]
    public partial class SkinnedSplatRenderer : SplatRenderer
    {
        // Characters sit out the occlusion reveal (see SplatOcclusionReveal) - the
        // reveal exists to show a character THROUGH scenery, so a character dissolving
        // with the wall would defeat it. A constructor rather than a field override:
        // the field is the base's serialized storage, and this sets only its default.
        public SkinnedSplatRenderer()
        {
            m_OcclusionReveal = false;
        }

        /// <summary>The splat character file to render.</summary>
        /// <remarks>
        /// Hides <see cref="SplatRenderer.splatFile"/>, which is a static capture and means nothing
        /// here. The base stores its own behind a differently-named serialized field precisely so
        /// this one can keep the name and the storage it has always had - two serialized fields
        /// with the same name in one hierarchy is a Unity serialization conflict.
        /// </remarks>
        [Tooltip("The splat character file (.vsplat, or a legacy .bin). Drag the asset here.")]
        [UnityEngine.Serialization.FormerlySerializedAs("binFile")]
        public new SplatCharacterAsset splatFile;

        SplatCharacterBinData m_Bin;

        // Decoded rest pose, weights and splat count. Also read by the skinning half.
        internal SplatCharacterBinData Bin => m_Bin;

#if UNITY_EDITOR
        // The base resolves the render resources; the skinning compute is this half's own.
        internal override bool NeedsResolve() => base.NeedsResolve() || skinningCompute == null;

        internal override void AutoResolve()
        {
            bool changed = ResolveShared();
            changed |= Resolve(ref skinningCompute, "SplatSkinning");
            // Persist the references or the build strips the shaders. This has to cover the
            // skinning compute too: it used to sit outside this flag, so its reference could fail
            // to reach disk and a player build would come up with a null compute shader.
            if (changed) UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        /// <summary>
        /// Loads a character from raw <c>.vsplat</c> bytes - content downloaded or generated at
        /// runtime rather than imported - replacing whatever this component was showing.
        /// <see cref="SplatRenderer.Loaded"/> is raised when it is ready.
        /// </summary>
        /// <remarks>
        /// Named for what it will be, not only for what it is. Today the work completes before this
        /// returns; the load is a single main-thread burst of several hundred milliseconds on a
        /// large character, and splitting it across frames is the planned fix. That change lands
        /// behind this signature without breaking callers, which it could not do behind a
        /// synchronous one - so write the calling code as if it were already asynchronous, and wait
        /// on <see cref="SplatRenderer.Loaded"/> rather than assuming completion on return.
        /// </remarks>
        public void LoadAsync(byte[] vsplat, SplatRigMode rigMode = SplatRigMode.Humanoid)
        {
            var asset = SplatCharacterAsset.FromBytes(vsplat, rigMode);
            if (asset == null) return;   // FromBytes already reported why
            LoadAsync(asset);
        }

        /// <summary>
        /// Loads an imported character, replacing whatever this component was showing.
        /// <see cref="SplatRenderer.Loaded"/> is raised when it is ready. See the remarks on
        /// <see cref="LoadAsync(byte[], SplatRigMode)"/> for why this is named async.
        /// </summary>
        public void LoadAsync(SplatCharacterAsset character)
        {
            if (character == null) { Debug.LogError("SkinnedSplatRenderer: LoadAsync got a null character.", this); return; }
            // Release the previous character first: its rig, GPU buffers and render asset are all
            // sized to the file being replaced.
            if (m_SetupDone) Teardown();
            splatFile = character;
            if (ShouldRender) TrySetup();
        }

        internal override void Setup()
        {
#if UNITY_EDITOR
            if (splatUtilities == null) AutoResolve();
#endif
            // Covers skinningCompute as well as the eight the base declares. See
            // SplatShaderResources for why a runtime-added renderer has nothing else.
            SplatShaderResources.Apply(this);
            // No file yet is not a failure, it is an unconfigured component, so this returns
            // without disabling and Update retries once one is assigned. Disabling here did real
            // damage twice over: a component added by hand switched itself off before the user
            // could drag a file in, and the importer serialized a DISABLED component into every
            // prefab it emitted, because AddComponent fires OnEnable one line before the file can
            // be assigned. An empty slot in the inspector says this well enough on its own.
            if (splatFile == null) return;
            if (splatUtilities == null)
            {
                Debug.LogError("SkinnedSplatRenderer: no SplatUtilities.compute - neither this " +
                    $"component nor the package's Resources/{SplatShaderResources.kResourceName}" +
                    ".asset supplied one, so it cannot draw.", this);
                enabled = false;
                return;
            }

            // 1. Parse the file into decoded splat arrays.
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            m_Bin = SplatCharacterBin.Parse(splatFile.bytes);
            double tParse = swPhase.Elapsed.TotalMilliseconds; swPhase.Restart();
            int splatsParsed = m_Bin.numSplats;

            // 2. Build the rig, before the asset exists. Anything PrepareRig applies to m_Bin here
            // ends up in the asset's own positions and bounds, which is what the frustum cull, the
            // sort's depth range and every un-skinned render path read. Keep it in this position.
            s_LastRigMs = 0;
            PrepareRig(m_Bin);
            double tRig = swPhase.Elapsed.TotalMilliseconds; swPhase.Restart();

            // 3. Build a runtime SplatAsset in the packed layout the renderer expects. The asset is
            // built here, so this component owns it and destroys it on teardown.
            m_Asset = SplatRenderAssetBuilder.Build(m_Bin);
            m_OwnsAsset = true;
            shOrder = 0; // the file carries no SH; force flat color
            double tAsset = swPhase.Elapsed.TotalMilliseconds; swPhase.Restart();

            // 4. Wire the render resources and build the GPU data. Same two calls the base makes
            // for a capture; replaces the `renderer.enabled = false; renderer.enabled = true;`
            // toggle that used to make a separate component rebuild its buffers, which with
            // inheritance would toggle THIS component and re-enter OnEnable and Setup.
            WireRenderResources("SkinnedSplatRenderer");
            ApplyVisuals();
            ReleaseRenderResources();
            CreateRenderResources();   // -> CreateNoComputeResources, which bakes the textures
            double tGpu = swPhase.Elapsed.TotalMilliseconds;

            // The renderer reports its own reason for refusing to set up (missing shaders, a compute
            // shader whose kernels did not compile). Stop here rather than leave a half-loaded
            // character whose skinning half then retries a dispatch it can never complete.
            if (!HasValidRenderSetup)
            {
                Debug.LogError($"SkinnedSplatRenderer: '{name}' loaded but the renderer could not set " +
                               "up, so nothing will draw. The error above says why.", this);
                enabled = false;
                return;
            }

            if (SplatLog.Info)
            {
                // The Stopwatch calls are free and the interpolated string is not, so the phases are
                // always measured and only the report is gated. "gpu" recreates every GPU buffer and
                // blocks on the driver; it can dwarf the CPU phases even though no per-splat C#
                // runs in it.
                Debug.Log($"[SplatLoad] {name}: {splatsParsed:N0} -> {m_Bin.numSplats:N0} splats, " +
                          $"total {swTotal.Elapsed.TotalMilliseconds:F0}ms | " +
                          $"parse {tParse:F0} " +
                          $"rig {tRig:F0} " +
                          $"asset {tAsset:F0} gpu {tGpu:F0}", this);
            }
        }

        /// <summary>
        /// Clears process-wide shader globals and cached state. Call when tearing the engine down.
        /// </summary>
        public static void ResetStatics()
        {
            ResetSkinningStatics();
        }

        // Bakes the no-compute source textures from the decoded bin and hands them to the renderer,
        // which takes ownership. Silently does nothing off the no-compute path (AdoptNoComputeData
        // disposes what it will not keep), so callers never have to test the path themselves.
        internal override void CreateNoComputeResources()
        {
            if (!UseNoComputePath || m_Bin == null) return;
            AdoptNoComputeData(SplatNoComputeData.Build(
                m_Bin.positions, m_Bin.rotations, m_Bin.scales, m_Bin.colors,
                m_Bin.opacities, m_Bin.boneIndices, m_Bin.boneWeights));
        }

        // Rebuild the render asset from the current m_Bin, for when something changed the decoded
        // splat data after Setup already built it. No-op before the first build.
        internal void RebuildAssetFromBin()
        {
            if (!m_SetupDone || m_Bin == null) return;
            var stale = m_Asset;
            m_Asset = SplatRenderAssetBuilder.Build(m_Bin);
            m_OwnsAsset = true;
            // Same pair Setup uses to rebuild the GPU resources for a new asset. The bin changed,
            // so the baked no-compute textures are stale too; CreateRenderResources rebuilds them
            // through CreateNoComputeResources.
            ReleaseRenderResources();
            CreateRenderResources();
            if (stale != null)
            {
                if (Application.isPlaying) Destroy(stale);
                else DestroyImmediate(stale);
            }
        }

        // Full teardown: the skinning half first, then the render half.
        //
        // ORDER. The skinning half hands the renderer its own GraphicsBuffers as skin overrides, so
        // those pointers must be taken back before the buffers are disposed - hence TeardownSkinning
        // runs first, and it clears the overrides before it disposes anything. Releasing the render
        // resources and dropping the bin/asset can only happen after that. Doing it the other way
        // round leaves the renderer holding freed buffers, and any draw that slipped through reads
        // released GPU memory.
        internal override void Teardown()
        {
            TeardownSkinning();

            base.Teardown();   // releases the render resources, then destroys the packed asset

            m_Bin?.Dispose();
            m_Bin = null;
        }
    }
}
