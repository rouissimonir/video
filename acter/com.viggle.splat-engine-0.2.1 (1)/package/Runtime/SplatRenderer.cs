// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using GaussianSplatting.Runtime;   // the vendored leaf assembly: SplatAsset, the GPU sorters
using Viggle.SplatEngine.Jobs;     // SplatChunkInfo: the chunk layout handed to the GPU

namespace Viggle.SplatEngine
{
    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<SplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(SplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();
        readonly List<(SplatRenderer, MaterialPropertyBlock)> m_FilteredSplats = new();
        // Scratch for the per-camera frustum cull in GatherSplatsForCamera (non-alloc overload).
        readonly Plane[] m_FrustumPlanes = new Plane[6];

        // Draw-order comparer for m_ActiveSplats: by renderOrder, then far-to-near using the
        // camera-space depth GatherSplatsForCamera precomputes into m_CamSortDepth (one transform
        // per renderer instead of two per comparison). A cached IComparer instance, not a lambda,
        // so the per-frame sort allocates no closure.
        class ActiveSplatComparer : IComparer<(SplatRenderer, MaterialPropertyBlock)>
        {
            public static readonly ActiveSplatComparer instance = new();
            public int Compare((SplatRenderer, MaterialPropertyBlock) a, (SplatRenderer, MaterialPropertyBlock) b)
            {
                var orderA = a.Item1.renderOrder;
                var orderB = b.Item1.renderOrder;
                // ASCENDING, and it matters. Position in this list is draw order, the target has
                // no depth buffer, and blending is "over" - so whatever draws LAST ends up on top.
                // A higher render order therefore has to sort LATER. This read descending until
                // the comparer was rewritten for perf, which silently inverted the field's own
                // documented meaning: order 1 drew underneath order 0. It takes two overlapping
                // splat objects at different orders to see it, which is why it survived.
                if (orderA != orderB)
                    return orderA.CompareTo(orderB);
                // Camera-local space: camera looks down +Z, so larger z == farther away.
                // Draws go to a shared offscreen target with "over" blending and no depth
                // buffer, so draw order is painter's order (last drawn wins / on top). To get
                // correct back-to-front compositing the farthest renderer must draw FIRST, so
                // sort descending by z. (Ascending here made far-origin characters draw last
                // and incorrectly appear in front of nearer ones.)
                return b.Item1.m_CamSortDepth.CompareTo(a.Item1.m_CamSortDepth);
            }
        }

        CommandBuffer m_CommandBuffer;

        /// <summary>
        /// Appends every registered renderer to <paramref name="results"/> (cleared first).
        /// </summary>
        /// <remarks>
        /// For features that need to walk the characters in the scene outside the normal render
        /// path - SplatRelightingFeature does, to hand each one the frame's lighting texture
        /// before the splat pass runs. Takes a caller-owned list so the walk allocates nothing;
        /// the registry itself stays private, since handing out the dictionary would let a
        /// caller mutate the registration.
        /// </remarks>
        public void GetRegisteredSplats(List<SplatRenderer> results)
        {
            results.Clear();
            foreach (var kvp in m_Splats)
                results.Add(kvp.Key);
        }

        public void RegisterSplat(SplatRenderer r)
        {
            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(SplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_FilteredSplats.Clear();
                m_Unified?.Release();
                m_Unified = null;
                m_NoComputeGlobal?.Dispose();
                m_NoComputeGlobal = null;
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        public static void TryUnregisterSplat(SplatRenderer r)
        {
            ms_Instance?.UnregisterSplat(r);
        }

        public static void ReleaseAll()
        {
            var inst = ms_Instance;
            if (inst == null)
                return;

            Camera.onPreCull -= inst.OnPreCullCamera;
            if (inst.m_CommandBuffer != null)
            {
                foreach (var cam in inst.m_CameraCommandBuffersDone)
                {
                    if (cam)
                        cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, inst.m_CommandBuffer);
                }
                inst.m_CommandBuffer.Dispose();
                inst.m_CommandBuffer = null;
            }
            inst.m_CameraCommandBuffersDone.Clear();
            inst.m_ActiveSplats.Clear();
            inst.m_FilteredSplats.Clear();
            // Deactivate before clearing the registry: the renderers still need their own view
            // buffers back if anything survives this (edit mode re-enables rather than reloads).
            inst.m_Unified?.Deactivate(inst.m_Splats);
            inst.m_Unified?.Release();
            inst.m_Unified = null;
            inst.m_NoComputeGlobal?.Deactivate(inst.m_Splats);
            inst.m_NoComputeGlobal?.Dispose();
            inst.m_NoComputeGlobal = null;
            inst.m_Splats.Clear();
            SplatUnifiedSort.ClearRequest();

            s_FreezeComputeAfterWarmup = false;
            s_SkipStaticSort = true;
            s_SkipStaticView = true;
            s_LogSortSkips = false;
            s_LogFrameCounter = 0;
            ms_Instance = null;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // Frustum cull: a renderer whose (padded) world bounds are entirely outside this
            // camera's frustum is dropped from m_ActiveSplats, which skips its sort, CalcViewData,
            // and draw for this camera. For an off-screen crowd this removes essentially the whole
            // per-character render cost; the visibility stamps below additionally let the skinner
            // pause animation/skinning for characters no camera can see.
            GeometryUtility.CalculateFrustumPlanes(cam, m_FrustumPlanes);
            int frame = Time.frameCount;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            var camTr = cam.transform;
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                {
                    if (gs != null && gs.isActiveAndEnabled && !gs.m_SkippedLogged)
                    {
                        gs.m_SkippedLogged = true;
                        Debug.LogError($"SplatRenderer '{gs.name}': skipped from rendering. " +
                                       $"validAsset={gs.HasValidAsset} (asset={(gs.m_Asset ? gs.m_Asset.name : "null")}, " +
                                       $"formatVersion={(gs.m_Asset ? gs.m_Asset.formatVersion : -1)}/{SplatAsset.kCurrentVersion}) " +
                                       $"validRenderSetup={gs.HasValidRenderSetup}", gs);
                    }
                    continue;
                }
                gs.m_LastCullTestFrame = frame;
                // Layer cull. This renderer is a MonoBehaviour rather than a Renderer, so Unity
                // does not apply Camera.cullingMask to it for free; without this test a splat draws
                // into every camera regardless of its GameObject's layer. Tested after the cull-test
                // stamp and before the visible stamp so a character no camera's mask admits counts
                // as "tested but not visible", which also lets the skinner pause it (IsRecentlyVisible).
                if ((cam.cullingMask & (1 << gs.gameObject.layer)) == 0)
                    continue;
                if (gs.m_FrustumCull && gs.TryGetWorldCullBounds(out Bounds wb) &&
                    !GeometryUtility.TestPlanesAABB(m_FrustumPlanes, wb))
                    continue;
                gs.m_LastVisibleFrame = frame;
                // Camera-space depth for the cached draw-order comparer (see ActiveSplatComparer).
                gs.m_CamSortDepth = camTr.InverseTransformPoint(gs.transform.position).z;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort them by order and depth from camera
            m_ActiveSplats.Sort(ActiveSplatComparer.instance);

            return true;
        }

        // DEBUG PROBE: when true, stop recording sort + view-data compute after a few warmup
        // frames, so draws read stable buffers. Distinguishes a GPU compute-vs-draw race
        // (missing memory barrier for vertex-stage SSBO reads on GLES) from data errors:
        // at rest pose the frozen data is identical to what each frame would recompute.
        // MUST default to false: with it on, a camera move after warmup renders stale data.
        // (SkinnedSplatRenderer re-stamps it every frame from its own debug toggle, which used
        // to mask this default for character scenes - raw SplatRenderer setups froze.)
        public static bool s_FreezeComputeAfterWarmup = false;
        internal const int kFreezeAfterFrames = 5;

        // EXPERIMENT: skip the per-frame depth sort for a renderer whose camera and transform
        // have not moved since its last sort (a static scene). The sort order is unchanged when
        // nothing moves, so the previously-sorted keys stay valid. This isolates the sort's share
        // of the frame: CalcViewData + draw still run every frame, so any FPS gain vs the
        // unconditional-sort baseline is attributable to the sort. Set false to restore the old
        // behaviour (sort every frame, gated only by m_SortNthFrame).
        public static bool s_SkipStaticSort = true;

        // EXPERIMENT: same static-scene skip, but for the CalcViewData projection pass. When neither
        // the camera nor the character moved (and it is not being skinned), the projected view data
        // is identical to the previous frame, so the persistent view buffer stays valid and the
        // compute dispatch can be skipped. Toggle independently of s_SkipStaticSort to isolate how
        // much of the frame is projection compute vs rasterization/overdraw.
        public static bool s_SkipStaticView = true;

        // Diagnostic: when true, log a once-per-second summary of how many renderers actually ran the
        // depth sort vs skipped it (and how many still report active skinning, which forces a sort).
        // Use it to confirm the static skip is engaging. Set false to silence.
        public static bool s_LogSortSkips = false;
        static int s_LogFrameCounter;

        // Cross-object sorting, off unless a URP renderer feature asks for it. Null while off, which
        // is the only state the default path ever sees.
        SplatUnifiedSort m_Unified;
        // The same job on the no-compute path, where m_Unified cannot run: a shared view-data
        // texture filled by a fragment pre-pass, one CPU sort over all of it, one draw. Allocated
        // on first use, which for most projects is never. See SplatNoComputeGlobalSort.cs.
        SplatNoComputeGlobalSort m_NoComputeGlobal;

        // What the caller bound for the splat draws this pass, so the no-compute global path can
        // put it back after detouring to its own view-data targets. Reset every pass: a caller
        // that stops declaring must not hand the next one a target that has since been released.
        SplatRenderTarget m_SplatTarget;

        /// <summary>
        /// Tell the render system which render target the splat draws are going to. Call it
        /// alongside your own <c>SetRenderTarget</c>, before <see cref="SortAndRenderSplats"/>.
        /// </summary>
        /// <remarks>
        /// Only the no-compute cross-object path needs this, and only because it has to bind its
        /// own view-data targets partway through the pass and a CommandBuffer cannot be asked what
        /// was bound before. Without a declared target that path stands down and the per-object
        /// one runs instead, so a caller that does not call this still renders - just without
        /// cross-object ordering on WebGL2.
        /// </remarks>
        public void DeclareSplatTarget(RenderTargetIdentifier color)
            => m_SplatTarget = new SplatRenderTarget(color);

        /// <inheritdoc cref="DeclareSplatTarget(RenderTargetIdentifier)"/>
        public void DeclareSplatTarget(RenderTargetIdentifier color, RenderTargetIdentifier depth)
            => m_SplatTarget = new SplatRenderTarget(color, depth);

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb, int layerMask = -1)
        {
            // Consumed once. A caller declares the target for the pass it is about to run, not for
            // every pass that follows it.
            SplatRenderTarget target = m_SplatTarget;
            m_SplatTarget = default;

            List<(SplatRenderer, MaterialPropertyBlock)> renderSplats = m_ActiveSplats;
            if (layerMask != -1)
            {
                m_FilteredSplats.Clear();
                foreach (var splat in m_ActiveSplats)
                    if ((layerMask & (1 << splat.Item1.gameObject.layer)) != 0)
                        m_FilteredSplats.Add(splat);
                renderSplats = m_FilteredSplats;
            }
            if (renderSplats.Count == 0)
                return null;

            // Unified (cross-object) sorting. Decided once, here, so the per-object path below is
            // reached by exactly the code it always was - no conditionals threaded through the loop.
            // Only a URP renderer feature can request it (see SplatRendererFeature.unifiedSorting);
            // HDRP and the built-in pipeline never do, and URP/HDRP/BiRP cannot be active at once,
            // so those two call sites keep today's behaviour by construction rather than by check.
            if (UpdateUnifiedSorting())
                return SortAndRenderSplatsUnified(cam, cmb, renderSplats);
            if (UpdateNoComputeGlobalSorting(target))
                return SortAndRenderSplatsNoComputeGlobal(cam, cmb, renderSplats, target);

            return SortAndRenderSplatsPerObject(cam, cmb, renderSplats);
        }

        // Reconcile the shared-buffer state with what the renderer features are asking for, and
        // report whether the unified path can run this pass. Costs one static bool test in the
        // overwhelmingly common case where nothing ever asked.
        bool UpdateUnifiedSorting()
        {
            // Unified sorting is compute end to end - a shared view buffer filled by CSCalcViewData
            // and ordered by the GPU sorters - so it cannot run on the no-compute path at all.
            // Treated exactly like "nobody asked for it", which also tears down any shared state a
            // feature set up before the path was known.
            bool wanted = SplatUnifiedSort.Requested && !SplatRenderer.UseNoComputePath;
            if (!wanted)
            {
                if (m_Unified != null)
                {
                    m_Unified.Deactivate(m_Splats);   // hands every renderer its own view buffer back
                    m_Unified.Release();
                    m_Unified = null;
                }
                return false;
            }

            m_Unified ??= new SplatUnifiedSort();
            // A debug render mode draws through a shader that reads splat data and the object
            // matrix rather than view data, so it cannot join a merged draw. Rather than mix two
            // draw paths (and two view-buffer layouts) in one frame, unified sorting steps aside
            // entirely while any renderer is in one. These are editor diagnostics.
            bool ok = !AnyDebugRenderMode() && m_Unified.TryActivate(m_Splats);
            // Unconditionally on failure, not only when it is still active: an activation can fail
            // AFTER releasing the shared buffer (a resize that could not be satisfied), and a
            // renderer left pointing at that is a dangling handle rather than a missing one.
            if (!ok)
                m_Unified.Deactivate(m_Splats);
            return ok;
        }

        bool AnyDebugRenderMode()
        {
            foreach (var gs in m_Splats.Keys)
            {
                if (gs != null && gs.m_RenderMode != SplatRenderer.RenderMode.Splats)
                    return true;
            }
            return false;
        }

        // The no-compute twin of UpdateUnifiedSorting: reconcile the shared view-data textures with
        // what the renderer features are asking for, and report whether the global path can run this
        // pass. Costs one static bool test in the overwhelmingly common case where nothing asked.
        bool UpdateNoComputeGlobalSorting(in SplatRenderTarget target)
        {
            // Same switch the compute path reads, and the same frame stamp - a project asks for
            // cross-object ordering once, on the renderer feature, and gets whichever
            // implementation its device can run. A pass that did not say where the splats are
            // going cannot be detoured to the view-data targets and back, so it stands down.
            bool wanted = SplatUnifiedSort.Requested && SplatRenderer.UseNoComputePath && target.IsValid;
            if (!wanted)
            {
                if (m_NoComputeGlobal != null)
                {
                    m_NoComputeGlobal.Deactivate(m_Splats);
                    m_NoComputeGlobal.Dispose();
                    m_NoComputeGlobal = null;
                }
                return false;
            }

            m_NoComputeGlobal ??= new SplatNoComputeGlobalSort();
            // A debug render mode draws through a shader that reads splat data and the object
            // matrix rather than view data, so it cannot join a merged draw (see the unified
            // path's note). Those materials are never even created here, but the check is what
            // keeps the two paths' preconditions identical rather than accidentally aligned.
            bool ok = !AnyDebugRenderMode() && m_NoComputeGlobal.TryActivate(m_Splats);
            // Unconditionally on failure, not only when it is still active: an activation can fail
            // AFTER releasing the shared textures (a resize that could not be satisfied), and a
            // renderer left holding a base into them is a dangling index rather than a missing one.
            if (!ok)
                m_NoComputeGlobal.Deactivate(m_Splats);
            return ok;
        }

        /// <summary>
        /// The no-compute counterpart of <see cref="SortAndRenderSplatsUnified"/>: project every
        /// visible object into the shared view-data textures, order all of their splats together
        /// on the CPU, and draw them as one draw per render order.
        /// </summary>
        /// <remarks>
        /// The per-object CPU sorts do not run here, for the reason they do not run on the compute
        /// path either: they would be re-deriving, at a granularity the merged draw cannot use, the
        /// same thing the global order already answers. What IS kept is the static-scene skip that
        /// made them cheap - an object whose transform, camera and pose have not changed is not
        /// re-projected, and if nothing at all changed the global order is reused untouched.
        /// <para>
        /// renderOrder still groups: all of order 0 is ordered and drawn, then all of order 1, so
        /// it remains a layering control rather than being silently ignored.
        /// </para>
        /// </remarks>
        Material SortAndRenderSplatsNoComputeGlobal(Camera cam, CommandBuffer cmb,
            List<(SplatRenderer, MaterialPropertyBlock)> renderSplats, in SplatRenderTarget target)
        {
            Material matComposite = null;
            foreach (var kvp in renderSplats)
            {
                kvp.Item1.EnsureMaterials();
                matComposite = kvp.Item1.m_MatComposite;
                kvp.Item2.Clear();
            }

            // Phase 1: projection into the shared view textures, for whatever moved.
            bool anyViewRan = m_NoComputeGlobal.ProjectVisible(cmb, cam, renderSplats, target);

            // Phases 2 and 3: one global order for the whole frame, one draw per render-order group.
            m_NoComputeGlobal.SortAndDraw(cmb, cam, renderSplats, anyViewRan);

            if (s_LogSortSkips && renderSplats.Count > 0 && (s_LogFrameCounter++ % 60 == 0))
                Debug.Log($"[GaussianSplat] no-compute global sort: {renderSplats.Count} objects, " +
                          $"reprojected={anyViewRan}");

            return matComposite;
        }

        Material SortAndRenderSplatsPerObject(Camera cam, CommandBuffer cmb,
            List<(SplatRenderer, MaterialPropertyBlock)> renderSplats)
        {
            Material matComposite = null;
            int dbgSorted = 0, dbgSkipped = 0, dbgSkinning = 0, dbgViewRan = 0;

            foreach (var kvp in renderSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;

                bool freeze = s_FreezeComputeAfterWarmup && gs.m_FrameCounter > kFreezeAfterFrames;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                // Static-scene skip: has anything that affects the splat depth order or projected
                // view data changed since the last snapshot - the camera pose, this character's
                // transform, active skinning (splats deforming), or the processed splat count (LOD)?
                // When nothing changed, both the prior sort AND the prior CalcViewData output remain
                // valid, so both can be reused. The m_SkinningActive guard keeps this safe in mixed
                // scenes: any actively-animated character is always treated as moved.
                Matrix4x4 camMat = cam.transform.localToWorldMatrix;
                bool camObjMoved = camMat != gs.m_LastSortCamera
                                   || matrix != gs.m_LastSortModel
                                   || gs.ProcCount != gs.m_LastSortProcCount;
                bool posed = gs.m_LastPosedFrame == Time.frameCount;   // skinner re-posed the splats this frame
                bool moved = !gs.m_HasSortedOnce || camObjMoved || posed;

                // Sort-LOD: the depth order changes FAST on camera/object motion (sort promptly) but
                // only SLOWLY under animation drift, so throttle the drift case. Re-sort on the first
                // frame or on any camera/object motion; under pure animation (fixed camera) only on
                // this character's staggered scheduled frame - every m_SortNthFrame frames. Fully
                // static → reuse the last sort. m_SortNthFrame == 1 sorts every pose frame (LOD off).
                if (gs.m_SortPhase < 0) gs.m_SortPhase = gs.GetInstanceID() & 0x7fffffff;
                bool doSort = !freeze && (!s_SkipStaticSort
                              || !gs.m_HasSortedOnce
                              || camObjMoved
                              || (posed && (Time.frameCount + gs.m_SortPhase) % Mathf.Max(1, gs.m_SortNthFrame) == 0));
                if (doSort)
                {
                    // On the no-compute path the sort is a CPU counting sort that uploads an order
                    // TEXTURE, so it runs in BindNoCompute below where the property block exists.
                    // Only the GPU dispatch is skipped here; all the bookkeeping below is shared,
                    // which is what keeps the throttling identical on both paths.
                    if (!SplatRenderer.UseNoComputePath)
                        gs.SortPoints(cmb, cam, matrix);
                    gs.m_LastSortCamera = camMat;
                    gs.m_LastSortModel = matrix;
                    gs.m_LastSortProcCount = gs.ProcCount;
                    gs.m_HasSortedOnce = true;
                }
                ++gs.m_FrameCounter;
                if (doSort) ++dbgSorted; else ++dbgSkipped;
                if (gs.m_SkinningActive != 0) ++dbgSkinning;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    SplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    SplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    SplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    SplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                // Every binding in this block is a StructuredBuffer that does not exist on the
                // no-compute path, and the .count reads below would dereference null. The
                // no-compute shader declares none of them - it is fed entirely by BindNoCompute.
                if (!SplatRenderer.UseNoComputePath)
                {
                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(SplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(SplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(SplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                // Also bind the vertex-stage buffers directly on this renderer's own material
                // instance: per-draw MaterialPropertyBlock SSBO bindings are unreliable on
                // OpenGL ES, where consecutive draws can end up reading the previous draw's
                // buffers (visible with multiple splat renderers in a scene).
                displayMat.SetBuffer(SplatRenderer.Props.SplatViewData, gs.m_GpuView);
                displayMat.SetBuffer(SplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                // Lengths for the vertex-stage bounds clamps. Bound on both, mirroring the buffers.
                mpb.SetInteger(SplatRenderer.Props.OrderBufferCount, gs.m_GpuSortKeys.count);
                mpb.SetInteger(SplatRenderer.Props.SplatViewDataCount, gs.m_GpuView.count);
                displayMat.SetInteger(SplatRenderer.Props.OrderBufferCount, gs.m_GpuSortKeys.count);
                displayMat.SetInteger(SplatRenderer.Props.SplatViewDataCount, gs.m_GpuView.count);
                // Vertex-texture-fetch path: the splat shader reads view data from this texture
                // (built by CSBuildViewTex in CalcViewData) instead of the buffers above. Bound
                // on both the MPB and the material instance, matching the SSBO bindings.
                if (SplatRenderer.UseVertexTextureFetch && gs.m_GpuViewTex != null)
                {
                    mpb.SetTexture(SplatRenderer.Props.SplatViewDataTex, gs.m_GpuViewTex);
                    mpb.SetInteger(SplatRenderer.Props.SplatViewTexWidth, gs.m_GpuViewTex.width);
                    displayMat.SetTexture(SplatRenderer.Props.SplatViewDataTex, gs.m_GpuViewTex);
                    displayMat.SetInteger(SplatRenderer.Props.SplatViewTexWidth, gs.m_GpuViewTex.width);
                }
                } // !UseNoComputePath
                // Same value CalcViewData used for the axes; the shader must not rely on the
                // _ScreenParams built-in (not reliably populated in custom passes on GLES).
                int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
                var screenPar = new Vector4(eyeW != 0 ? eyeW : cam.pixelWidth, eyeH != 0 ? eyeH : cam.pixelHeight, 0, 0);
                mpb.SetVector(SplatRenderer.Props.VecScreenParams, screenPar);
                displayMat.SetVector(SplatRenderer.Props.VecScreenParams, screenPar);
                mpb.SetFloat(SplatRenderer.Props.SplatScale, gs.splatScale);
                mpb.SetFloat(SplatRenderer.Props.SplatOpacityScale, gs.opacityScale);
                mpb.SetFloat(SplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(SplatRenderer.Props.SHOrder, gs.SHOrderEffective);
                mpb.SetInteger(SplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
                mpb.SetInteger(SplatRenderer.Props.DisplayIndex, gs.m_RenderMode == SplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(SplatRenderer.Props.DisplayChunks, gs.m_RenderMode == SplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                // Static-scene skip: reuse the persistent view buffer when nothing moved (same signal
                // as the sort). When s_SkipStaticView is off this runs every non-frozen frame as before.
                bool doView = !freeze && (moved || !s_SkipStaticView);
                if (SplatRenderer.UseNoComputePath)
                {
                    // There is no projection pass to run: the vertex shader projects each splat
                    // itself. What this step still owes the shader is its source textures and a
                    // fresh draw order, so bind them here and let the CPU sort follow the same
                    // doSort throttle the compute path uses.
                    gs.BindNoCompute(mpb, cam, doSort);
                    if (doView) ++dbgViewRan;
                }
                else if (doView)
                {
                    cmb.BeginSample(s_ProfCalcView);
                    gs.CalcViewData(cmb, cam);
                    cmb.EndSample(s_ProfCalcView);
                    ++dbgViewRan;
                }

                // draw (ProcCount, not splatCount, so distance LOD draws only the top-K prefix)
                int indexCount = 6;
                int instanceCount = gs.ProcCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is SplatRenderer.RenderMode.DebugBoxes or SplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == SplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                cmb.BeginSample(s_ProfDraw);
                // Screen-size-cull compaction: in normal splat mode the sort has parked culled
                // splats at the back and written the visible instance count into m_GpuDrawArgs, so
                // draw indirectly over just that prefix (this is what makes the tri/vert count drop).
                // Debug topologies (boxes/chunks) keep the plain full-count path.
                bool useIndirect = SplatRenderer.GlobalScreenSizeCull > 0f
                                   && gs.m_RenderMode == SplatRenderer.RenderMode.Splats
                                   && gs.m_GpuDrawArgs != null;
                if (useIndirect)
                    cmb.DrawProceduralIndirect(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, gs.m_GpuDrawArgs, 0, mpb);
                else
                    cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }

            // Diagnostic: once-per-second summary so you can confirm the sort is actually being
            // skipped for static characters. "skinning=N" > 0 means N renderers still report active
            // skinning and are therefore force-sorted (disable/idle the skinner to let them skip).
            if (s_LogSortSkips && renderSplats.Count > 0 && (s_LogFrameCounter++ % 60 == 0))
                Debug.Log($"[GaussianSplat] this frame of {renderSplats.Count}: sort ran={dbgSorted} " +
                          $"skipped={dbgSkipped} | view ran={dbgViewRan} skipped={renderSplats.Count - dbgViewRan} " +
                          $"(skinning={dbgSkinning}, skipSort={s_SkipStaticSort}, skipView={s_SkipStaticView})");

            return matComposite;
        }

        /// <summary>
        /// The unified counterpart of <see cref="SortAndRenderSplatsPerObject"/>: project every
        /// visible object into one shared buffer, order all of their splats together, and draw them
        /// as one draw per render order.
        /// </summary>
        /// <remarks>
        /// The per-object depth sorts do not run here. They would be re-deriving, at a granularity
        /// the merged draw cannot use, the same thing the global order already answers - and their
        /// output (a permutation of one object's splats) is not something a single draw over many
        /// objects can consume. What IS kept is the static-scene skip that made them cheap: an
        /// object whose transform, camera and pose have not changed is not re-projected, and if
        /// nothing at all changed the global order is reused untouched.
        /// <para>
        /// renderOrder still groups: all of order 0 is merged and drawn, then all of order 1, so it
        /// remains a layering control rather than being silently ignored. Splats only interleave
        /// with other splats at the same render order, which is what "higher draws on top" means.
        /// </para>
        /// </remarks>
        Material SortAndRenderSplatsUnified(Camera cam, CommandBuffer cmb,
            List<(SplatRenderer, MaterialPropertyBlock)> renderSplats)
        {
            Material matComposite = null;
            bool anyViewRan = false;
            int frame = Time.frameCount;
            Matrix4x4 camMat = cam.transform.localToWorldMatrix;

            // Phase 1: projection. Same static-scene skip as the per-object path, driven by the same
            // stamps - here they are refreshed when the projection runs rather than when a sort does,
            // since there is no per-object sort to hang them off.
            foreach (var kvp in renderSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                if (gs.m_MatSplats == null)
                    continue;

                bool freeze = s_FreezeComputeAfterWarmup && gs.m_FrameCounter > kFreezeAfterFrames;
                var matrix = gs.transform.localToWorldMatrix;
                bool camObjMoved = camMat != gs.m_LastSortCamera
                                   || matrix != gs.m_LastSortModel
                                   || gs.ProcCount != gs.m_LastSortProcCount;
                bool posed = gs.m_LastPosedFrame == frame;
                bool moved = !gs.m_HasSortedOnce || camObjMoved || posed;
                ++gs.m_FrameCounter;

                bool doView = !freeze && (moved || !s_SkipStaticView);
                if (!doView)
                    continue;

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam);
                cmb.EndSample(s_ProfCalcView);
                gs.m_LastSortCamera = camMat;
                gs.m_LastSortModel = matrix;
                gs.m_LastSortProcCount = gs.ProcCount;
                gs.m_HasSortedOnce = true;
                anyViewRan = true;
            }

            // Phase 2: one global order and one draw per render-order group. renderSplats is
            // already sorted by render order, so the groups are contiguous runs.
            m_Unified.UpdateDepthRange(cam, renderSplats);
            int groupCount = 0;
            for (int i = 0; i < renderSplats.Count; )
            {
                int order = renderSplats[i].Item1.renderOrder;
                while (i < renderSplats.Count && renderSplats[i].Item1.renderOrder == order) ++i;
                ++groupCount;
            }
            for (int i = 0; i < renderSplats.Count; )
            {
                int order = renderSplats[i].Item1.renderOrder;
                int end = i;
                while (end < renderSplats.Count && renderSplats[end].Item1.renderOrder == order) ++end;
                m_Unified.DrawGroup(cmb, cam, renderSplats, i, end, anyViewRan, groupCount == 1);
                i = end;
            }

            if (s_LogSortSkips && renderSplats.Count > 0 && (s_LogFrameCounter++ % 60 == 0))
                Debug.Log($"[GaussianSplat] unified sort: {renderSplats.Count} objects in {groupCount} " +
                          $"render-order group(s), reprojected={anyViewRan}");

            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
            {
                // m_CommandBuffer is shared across cameras and stays attached once added, so
                // returning without clearing would let this camera replay the commands last
                // recorded for a different camera. Harmless when nothing ever drew here, but a
                // camera that used to see a splat (before a layer/mask change culled it) would
                // otherwise keep drawing it forever.
                if (m_CameraCommandBuffersDone.Contains(cam))
                    m_CommandBuffer?.Clear();
                return;
            }

            InitialClearCmdBuffer(cam);

            // Offscreen splat accumulation target, optionally at reduced resolution (see
            // SplatRenderScale). The composite reads _GaussianSplatRTParams to map full-res
            // fragment positions to RT texels (Load at scale 1, bilinear upsample below 1).
            bool scaled = SplatRenderer.RenderScaleActive;
            Vector2Int rtSize = SplatRenderer.ScaledRTSize(cam.pixelWidth, cam.pixelHeight);
            m_CommandBuffer.SetGlobalVector(SplatRenderer.Props.GaussianSplatRTParams,
                new Vector4(1f / cam.pixelWidth, 1f / cam.pixelHeight, SplatRenderer.EffectiveRenderScale, 0));
            m_CommandBuffer.GetTemporaryRT(SplatRenderer.Props.GaussianSplatRT, rtSize.x, rtSize.y, 0,
                scaled ? FilterMode.Bilinear : FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            // A depth attachment must match the color target's size, so the camera depth buffer
            // can only be bound at full scale. When scaled - or on GL with SplatDepthOcclusion on,
            // where the attachment orientation disagrees with our offscreen target - splats
            // depth-test manually in the fragment shader against the full-res _CameraDepthTexture
            // (ensured to exist below via depthTextureMode).
            bool isGL = SplatRenderer.IsGLDevice;
            // BiRP has no camera target descriptor here; MSAA comes from quality settings, and a
            // camera can opt out of it individually.
            bool msaa = SplatRenderer.MsaaBlocksDepthAttachment(
                cam.allowMSAA ? QualitySettings.antiAliasing : 1);
            bool manualDepth = SplatRenderer.ManualDepthWanted(isGL, msaa);
            if (manualDepth)
                cam.depthTextureMode |= DepthTextureMode.Depth;
            m_CommandBuffer.SetGlobalInteger(SplatRenderer.Props.SplatManualDepthTest, manualDepth ? 1 : 0);
            m_CommandBuffer.SetGlobalVector(SplatRenderer.Props.SplatDepthTexParams,
                new Vector4(1f / rtSize.x, 1f / rtSize.y, 0, 0));
            // Declared as well as bound: the no-compute cross-object path detours the target to
            // its own view-data textures partway through the pass and has to put this back.
            if (scaled || msaa)
            {
                m_CommandBuffer.SetRenderTarget(SplatRenderer.Props.GaussianSplatRT);
                DeclareSplatTarget(SplatRenderer.Props.GaussianSplatRT);
            }
            else
            {
                m_CommandBuffer.SetRenderTarget(SplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
                DeclareSplatTarget(SplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            }
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(SplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(SplatRenderer.Props.GaussianSplatRT);
        }
    }

    /// <summary>
    /// Renders a static Gaussian splat capture: assign a <see cref="SplatCaptureAsset"/> and it
    /// draws. The splat analogue of <see cref="MeshRenderer"/>, and the base of
    /// <see cref="SkinnedSplatRenderer"/>, which adds a rig and GPU skinning on top of exactly this
    /// rendering.
    /// </summary>
    /// <remarks>
    /// Unlike a character, a capture carries spherical harmonics, so this component also exposes
    /// <see cref="shOrder"/> and <see cref="shOnly"/>.
    /// <para>
    /// The GPU-ready data is built when this component loads, from the compressed <c>.spz</c>
    /// bytes. Nothing is converted at import.
    /// </para>
    /// Hot-reload stable ABI type: Unity message methods and serialized fields must stay stable.
    /// <para>
    /// Everything below the curated public surface is <c>internal</c>. The vendored renderer this
    /// grew out of exposed 167 public members; a host needs the handful documented here, and
    /// <see cref="SplatCustomPass"/> / <see cref="SplatQuality"/> / <see cref="SplatEngineLifecycle"/>
    /// re-expose the rest of what is genuinely useful in the shape the package wants.
    /// </para>
    /// </remarks>
    [ExecuteAlways] // so it can load + render in the editor (see SplatSettings.renderInEditor)
    [DisallowMultipleComponent]
    [AddComponentMenu("Viggle Splat Engine/Splat Renderer")]
    public class SplatRenderer : MonoBehaviour
    {
        internal enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }

        // ------------------------------------------------------------------------------------
        // The curated public surface. Everything else on this type is internal.
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// The splat capture to render.
        /// </summary>
        /// <remarks>
        /// A property over a differently-named serialized field on purpose:
        /// <see cref="SkinnedSplatRenderer"/> derives from this type and carries its own
        /// <c>splatFile</c> (a character, not a capture). Two serialized fields with the same name
        /// in one hierarchy is a Unity serialization conflict, so the storage names have to differ
        /// even though the API names should not.
        /// </remarks>
        [SerializeField, HideInInspector]
        [Tooltip("The imported splat capture (.spz). Drag the asset here.")]
        SplatCaptureAsset m_SplatFile;
        public SplatCaptureAsset splatFile
        {
            get => m_SplatFile;
            set => m_SplatFile = value;
        }

        /// <summary>
        /// HDR color multiplier applied to this object's splats; white leaves them unchanged.
        /// Live-editable in play mode.
        /// </summary>
        [Tooltip("Brightness / color multiplier for this object's splats. White = unchanged, " +
                 "and it is a plain multiply, so 0.7 is 70% as bright. HDR, so values above 1 " +
                 "brighten. Live-editable in play mode.")]
        [Header("Material")]
        [ColorUsage(false, true)] public Color baseColor = Color.white;

        /// <summary>
        /// Opacity multiplier for the whole object. 1 leaves it unchanged, 0 makes it invisible.
        /// Use it to fade the object in or out.
        /// </summary>
        [Range(0f, 1f)]
        [Tooltip("Opacity multiplier for the whole object. 1 = unchanged, 0 = invisible.")]
        public float opacityScale = 1.0f;

        /// <summary>
        /// Size multiplier applied to every splat. Slightly above 1 closes gaps on a sparse
        /// capture at the cost of sharpness; below 1 does the reverse. Code-only: it is not drawn
        /// in the inspector, because it is a per-asset correction rather than something to tune
        /// per instance.
        /// </summary>
        [HideInInspector]
        public float splatScale = 1.0f;

        /// <summary>
        /// Draw order against other splat objects. Higher draws on top; equal orders sort by
        /// distance. Only matters where two splat objects overlap on screen. Code-only: it is not
        /// drawn in the inspector.
        /// </summary>
        [HideInInspector]
        public int renderOrder;

        /// <summary>
        /// How much of the capture's spherical harmonics to evaluate, 0 to 3. 3 is what was
        /// captured: view-dependent shading, so reflections and sheen shift as the camera moves.
        /// Lower orders are cheaper and flatter; 0 is unlit base color.
        /// </summary>
        /// <remarks>
        /// Capped by what the file carries (see <see cref="SHOrderEffective"/>). Plenty of captures
        /// - most object scans - are exported at degree 0 and store no spherical harmonics at all;
        /// on one of those this has no effect whatever it is set to.
        /// <para>
        /// Only static captures have SH at all. A splat character carries none, so on a
        /// <see cref="SkinnedSplatRenderer"/> this is clamped to 0 by that same cap and is hidden
        /// from the inspector - which is why it is [HideInInspector] here and drawn explicitly by
        /// the capture's own inspector instead.
        /// </para>
        /// </remarks>
        [HideInInspector]
        [Range(0, 3)]
        [Tooltip("How much of the capture's spherical harmonics to evaluate. 3 = as captured " +
                 "(view-dependent shading); lower is cheaper and flatter; 0 is unlit base color.")]
        public int shOrder = 3;

        /// <summary>
        /// Show only the spherical-harmonics contribution, in grey. A diagnostic for how much
        /// view-dependent detail a capture actually carries.
        /// </summary>
        [HideInInspector]
        [Tooltip("Diagnostic: show only the spherical-harmonics contribution, in grey.")]
        public bool shOnly;

        // ------------------------------------------------------------------------------------
        // Serialized resource references. Auto-resolved in the editor (see AutoResolve): a
        // ComputeShader cannot be looked up by name at runtime, and a player build strips any
        // shader no serialized asset references, so both have to be serialized here.
        // ------------------------------------------------------------------------------------

        /// <summary>SplatUtilities.compute.</summary>
        [SerializeField, HideInInspector] internal ComputeShader splatUtilities;

        /// <summary>
        /// DeviceRadixSort.compute, the GPU sort kernels. Optional: when it is missing or the
        /// platform lacks the wave intrinsics it uses, splats render unsorted.
        /// </summary>
        [SerializeField, HideInInspector] internal ComputeShader deviceRadixSort;

        [SerializeField, HideInInspector] internal Shader shaderSplats;
        [SerializeField, HideInInspector] internal Shader shaderComposite;
        [SerializeField, HideInInspector] internal Shader shaderDebugPoints;
        [SerializeField, HideInInspector] internal Shader shaderDebugBoxes;

        /// <summary>
        /// RenderGaussianSplatsNoCompute.shader / GaussianCompositeNoCompute.shader - the
        /// UseNoComputePath pair. Serialized for the same reason as the others: a player build
        /// strips any shader no serialized asset references, and a WebGL2 build is exactly the
        /// one that needs these. They are the only shaders that load on GLES 3.0, since the
        /// compute-path pair declares "#pragma require compute".
        /// </summary>
        [SerializeField, HideInInspector] internal Shader shaderSplatsNoCompute;
        [SerializeField, HideInInspector] internal Shader shaderCompositeNoCompute;

        // The packed GPU asset. Built from splatFile at load and owned by this component (see
        // m_OwnsAsset), or assigned directly by a subclass that builds its own.
        [SerializeField, HideInInspector] internal SplatAsset m_Asset;

        // True when m_Asset was built here and must be destroyed on teardown. False for an asset
        // that arrived serialized on the component, which this does not own.
        [System.NonSerialized] internal bool m_OwnsAsset;

        // What m_Asset was built from, so Update can notice the file field being swapped.
        [System.NonSerialized] SplatCaptureAsset m_LoadedFrom;

        // ------------------------------------------------------------------------------------
        // Loading. A capture is decoded and packed when the component loads, never at import:
        // converting at import turned a 27MB .spz into a 432MB project asset.
        // ------------------------------------------------------------------------------------

#if UNITY_EDITOR
        internal virtual void Reset() => AutoResolve();

        internal virtual void OnValidate()
        {
            if (NeedsResolve()) AutoResolve();
            ApplyVisuals();   // live inspector tuning, in edit and play mode
        }

        // Virtual so a subclass can fold its own resources into the same check. Splitting the
        // question ("is anything missing?") from the answer ("resolve everything") keeps a
        // subclass from having to restate this list.
        internal virtual bool NeedsResolve() =>
            splatUtilities == null || deviceRadixSort == null || shaderSplats == null ||
            shaderComposite == null || shaderDebugPoints == null || shaderDebugBoxes == null ||
            shaderSplatsNoCompute == null || shaderCompositeNoCompute == null;

        internal virtual void AutoResolve()
        {
            // Persist the references or the build strips the shaders.
            if (ResolveShared()) UnityEditor.EditorUtility.SetDirty(this);
        }

        internal bool ResolveShared()
        {
            bool changed = false;
            changed |= Resolve(ref splatUtilities, "SplatUtilities");
            changed |= Resolve(ref deviceRadixSort, "DeviceRadixSort");
            changed |= Resolve(ref shaderSplats, "RenderGaussianSplats");
            changed |= Resolve(ref shaderComposite, "GaussianComposite");
            changed |= Resolve(ref shaderDebugPoints, "GaussianDebugRenderPoints");
            changed |= Resolve(ref shaderDebugBoxes, "GaussianDebugRenderBoxes");
            // Resolved on every platform, not just when UseNoComputePath is true: resolution
            // happens in the editor, where compute always exists, so gating on the probe would
            // leave the references null in exactly the build that needs them.
            changed |= Resolve(ref shaderSplatsNoCompute, "RenderGaussianSplatsNoCompute");
            changed |= Resolve(ref shaderCompositeNoCompute, "GaussianCompositeNoCompute");
            return changed;
        }

        // Found by asset search rather than a literal path, so a project that vendors the package
        // anywhere else - copied under Assets/, renamed, embedded - still resolves them.
        internal static bool Resolve<T>(ref T field, string assetName) where T : UnityEngine.Object
        {
            if (field != null) return false;
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets($"{assetName} t:{typeof(T).Name}"))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets matches substrings, so "SplatSkinning" also hits "SplatSkinningJobs".
                if (System.IO.Path.GetFileNameWithoutExtension(path) != assetName) continue;
                var found = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
                if (found == null) continue;
                field = found;
                return true;
            }
            return false;
        }
#endif

        // The RENDER lifecycle: file parsed, SplatAsset built, GPU resources created.
        [System.NonSerialized] internal bool m_SetupDone;

        // Always render in Play mode; in edit mode, including Prefab Mode, only when the project
        // setting asks for it.
        internal bool ShouldRender => Application.isPlaying || SplatSettings.Instance.renderInEditor;

        /// <summary>True once the object's GPU resources exist and it is drawing.</summary>
        public bool IsLoaded => m_SetupDone;

        /// <summary>
        /// Raised once the object is on the GPU and drawing. Fires on the main thread. A handler
        /// added after loading has already finished is called immediately, so there is no race
        /// between subscribing and the load completing.
        /// </summary>
        /// <remarks>
        /// Typed as <see cref="SplatRenderer"/> rather than once per component: a character IS a
        /// <see cref="SplatRenderer"/>, so one event serves both. Cast the argument when you need
        /// the character-specific API.
        /// </remarks>
        public event Action<SplatRenderer> Loaded
        {
            add
            {
                m_Loaded += value;
                if (m_SetupDone) value?.Invoke(this);
            }
            remove => m_Loaded -= value;
        }
        [System.NonSerialized] Action<SplatRenderer> m_Loaded;

        /// <summary>
        /// Loads a capture from raw <c>.spz</c> bytes - content downloaded or generated at runtime
        /// rather than imported - replacing whatever this component was rendering.
        /// <see cref="Loaded"/> is raised when it is ready.
        /// </summary>
        /// <remarks>
        /// Named for what it will be, not only for what it is. Today the work completes before
        /// this returns; decoding and packing a large capture is a single main-thread burst of
        /// several hundred milliseconds, and splitting it across frames is the planned fix. That
        /// change lands behind this signature without breaking callers, which it could not do
        /// behind a synchronous one - so write the calling code as if it were already
        /// asynchronous, and wait on <see cref="Loaded"/> rather than assuming completion on
        /// return.
        /// </remarks>
        public void LoadAsync(byte[] spz)
        {
            var asset = SplatCaptureAsset.FromBytes(spz);
            if (asset == null) return;   // FromBytes already reported why
            LoadAsync(asset);
        }

        /// <summary>
        /// Shows a different capture, replacing whatever this component was rendering.
        /// <see cref="Loaded"/> is raised when it is ready. See the remarks on
        /// <see cref="LoadAsync(byte[])"/> for why this is named async.
        /// </summary>
        public void LoadAsync(SplatCaptureAsset capture)
        {
            if (capture == null) { Debug.LogError("SplatRenderer: LoadAsync got a null capture.", this); return; }
            // Release the previous capture first: its GPU buffers are sized to the file it is
            // replacing.
            if (m_SetupDone) Teardown();
            m_SplatFile = capture;
            if (ShouldRender) TrySetup();
        }

        internal void TrySetup()
        {
            if (m_SetupDone) return;
            // Must run before Setup: the settings have to be in effect before GPU resources exist.
            SplatSettings.EnsureApplied();

            try { Setup(); m_SetupDone = true; m_Loaded?.Invoke(this); }
            catch (Exception e) { Debug.LogError($"{GetType().Name}: setup failed: {e}"); enabled = false; }
        }

        /// <summary>
        /// World-space bounds of the object at rest. False until it has loaded. Use it to frame a
        /// camera or to place the object against other geometry.
        /// </summary>
        /// <remarks>
        /// Rest bounds, not the animated silhouette, on a skinned character: animation barely
        /// changes a character's on-screen footprint, and recomputing per frame would mean reading
        /// the skinned positions back from the GPU.
        /// </remarks>
        public bool TryGetWorldBounds(out Bounds bounds)
        {
            var a = m_Asset;
            if (a == null) { bounds = default; return false; }
            bounds = new Bounds(transform.TransformPoint((a.boundsMin + a.boundsMax) * 0.5f),
                                Vector3.Scale(a.boundsMax - a.boundsMin, transform.lossyScale));
            return true;
        }

        internal virtual void Setup()
        {
#if UNITY_EDITOR
            if (splatUtilities == null) AutoResolve();
#endif
            // Whatever the editor did not fill in, the asset the package ships under Resources/
            // does. That is the whole of it for a renderer created at runtime with AddComponent:
            // it has never been serialized by anything, so in a player every reference above is
            // null and there is no AssetDatabase to search. See SplatShaderResources.
            SplatShaderResources.Apply(this);
            // 1. Decompress the capture, then pack it into the GPU layout, both at load time.
            //    No file yet is not a failure, it is an unconfigured component, so this returns
            //    without disabling and Update retries once one is assigned. Disabling here meant a
            //    component added by hand switched itself off before the user could drag a file in,
            //    and it meant the importer serialized a DISABLED component into every prefab,
            //    because AddComponent fires OnEnable one line before the file can be assigned.
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            double tRead = 0, tAsset = 0;
            int shDegree = 0;
            if (m_SplatFile != null && m_SplatFile.bytes != null)
            {
                if (splatUtilities == null)
                {
                    Debug.LogError("SplatRenderer: no SplatUtilities.compute - neither this " +
                        $"component nor the package's Resources/{SplatShaderResources.kResourceName}" +
                        ".asset supplied one, so it cannot draw.", this);
                    enabled = false;
                    return;
                }

                var packed = SplatSpzFile.Read(m_SplatFile.bytes);
                tRead = swPhase.Elapsed.TotalMilliseconds; swPhase.Restart();
                shDegree = ShDegreeForCoeffs(packed.shCoeffs);
                // Before Build: it takes ownership of the packed sections and frees them as it
                // consumes them, so this is the last moment the decoded values can be obtained.
                DecodeCaptureForNoCompute(packed);
                try
                {
                    // Build takes ownership and frees the decompressed source as soon as it is done
                    // with it; the finally is the safety net for the path where it throws first.
                    m_Asset = SplatRenderAssetBuilder.Build(ref packed, shDegree);
                }
                finally
                {
                    SplatSpzFile.Dispose(ref packed);
                }
                tAsset = swPhase.Elapsed.TotalMilliseconds; swPhase.Restart();
                if (m_Asset == null) { enabled = false; return; }   // Build already reported why
                m_OwnsAsset = true;
                m_LoadedFrom = m_SplatFile;
            }
            // Nothing to draw and nothing to complain about: an unconfigured component, or one
            // whose asset arrived serialized on it, which is handled below.
            if (m_Asset == null) return;

            // 2. Wire the render resources and build the GPU data.
            WireRenderResources("SplatRenderer");
            ApplyVisuals();
            ReleaseRenderResources();
            CreateRenderResources();
            double tGpu = swPhase.Elapsed.TotalMilliseconds;

            // The renderer reports its own reason for refusing to set up (missing shaders, a
            // compute shader whose kernels did not compile, an asset with no data). Stop here
            // rather than leave the component claiming to be loaded when nothing will draw.
            if (!HasValidRenderSetup)
            {
                Debug.LogError($"SplatRenderer: '{name}' loaded but could not set up, so nothing " +
                               "will draw. The error above says why.", this);
                enabled = false;
                return;
            }

            if (SplatLog.Info)
            {
                // The Stopwatch calls are free and the interpolated string is not, so the phases
                // are always measured and only the report is gated.
                Debug.Log($"[SplatLoad] {name}: static capture " +
                          $"'{(m_SplatFile != null ? m_SplatFile.name : m_Asset.name)}', " +
                          $"{m_Asset.splatCount:N0} splats, SH degree {shDegree}, " +
                          $"total {swTotal.Elapsed.TotalMilliseconds:F0}ms | " +
                          $"read {tRead:F0} pack {tAsset:F0} gpu {tGpu:F0}", this);
            }
        }

        // Shader.Find is an editor-only fallback: the serialized references above are what survive
        // player-build shader stripping. Shared with the character path, which wires the identical
        // set before building its own asset.
        internal void WireRenderResources(string who)
        {
            if (shaderSplats == null)      shaderSplats      = Shader.Find("Gaussian Splatting/Render Splats");
            if (shaderComposite == null)   shaderComposite   = Shader.Find("Hidden/Gaussian Splatting/Composite");
            if (shaderDebugPoints == null) shaderDebugPoints = Shader.Find("Gaussian Splatting/Debug/Render Points");
            if (shaderDebugBoxes == null)  shaderDebugBoxes  = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            if (deviceRadixSort == null)   // optional: null/unsupported -> unsorted splats
                Debug.LogWarning($"{who}: DeviceRadixSort.compute not assigned; splats will render unsorted", this);
        }

        // SPZ stores a degree, the reader hands back a coefficient count, and the packer wants the
        // degree back. One table, inverted, rather than threading both through every signature.
        static int ShDegreeForCoeffs(int coeffs) => coeffs switch { 15 => 3, 8 => 2, 3 => 1, _ => 0 };

        /// <summary>
        /// Pushes every visual field on this component onto the render state. Call after changing
        /// any of them from code; inspector edits already trigger it.
        /// </summary>
        /// <remarks>
        /// The values are read straight out of the fields at dispatch time, so all this really has
        /// to do is invalidate. The static-scene skip reuses the previous CalcViewData output while
        /// the camera and the object are still - which for a static capture is most of the time -
        /// so without this a color change would not appear until something moved.
        /// </remarks>
        public void ApplyVisuals()
        {
            InvalidateViewData();
#if UNITY_EDITOR
            // In edit mode nothing drives the loop either, so without a repaint the Scene view does
            // not redraw and the invalidated data is never recomputed.
            if (!Application.isPlaying)
                UnityEditor.SceneView.RepaintAll();
#endif
        }

        /// <summary>
        /// Stops drawing this object and releases the GPU resources it holds. The
        /// <see cref="splatFile"/> itself is a project asset this component does not own and is
        /// left alone. Assigning a new file, or re-enabling, sets it up again.
        /// </summary>
        public void ReleaseResources() => Teardown();

        // Stop drawing and release the GPU resources. The packed asset was built here at load, so
        // it is destroyed here too - unless it arrived serialized on the component, which this
        // does not own (m_OwnsAsset). The file it was built from is a project asset this component
        // does not own either, and is left alone.
        internal virtual void Teardown()
        {
            ReleaseRenderResources();
            // Persistent, and they outlive the per-asset resources on purpose - a Release/Create
            // pair has to be able to rebuild the textures from them - so they are freed here
            // rather than in DisposeResourcesForAsset.
            ReleaseCaptureArrays();
            if (m_Asset != null && m_OwnsAsset)
            {
                if (Application.isPlaying) Destroy(m_Asset);
                else DestroyImmediate(m_Asset);
            }
            if (m_OwnsAsset) m_Asset = null;
            m_OwnsAsset = false;
            m_LoadedFrom = null;
            m_SetupDone = false;
        }

        // Global quality setting shared by every splat renderer (not authored per instance):
        // cull splats whose on-screen footprint radius is below this many pixels. Cheap way to
        // stop distant/small splats from paying full alpha-blend overdraw, which dominates the
        // frame when a crowd shrinks into a few pixels (thousands of sub-pixel quads serialize
        // on the same ROP pixels). 0 = disabled (draw every splat). Set once from game/quality
        // code, e.g. SplatRenderer.GlobalScreenSizeCull = 1.0f;
        internal static float GlobalScreenSizeCull = 0.0f;

        // Resolution scale for the offscreen RT the splats render into, shared by every splat
        // renderer (BiRP, URP and HDRP paths). 1 = full camera resolution (default, bit-identical
        // to the old behavior). Lower values render the splats at reduced resolution and
        // bilinearly upsample in the composite - a large fill-rate/overdraw win on mobile, and
        // soft gaussians upscale gracefully (0.5-0.75 is usually hard to spot). Trade-off: below
        // 1.0 the splat pass can no longer bind the camera depth buffer (attachment sizes must
        // match), so splats are not depth-occluded by opaque scene geometry while scaled - same
        // as the existing OpenGL ES behavior. Clamped to [0.25, 1].
        internal static float SplatRenderScale = 1.0f;

        // Depth-occlude splats against opaque scene geometry on OpenGL ES / OpenGL, where it is
        // otherwise skipped entirely and splats therefore draw on top of everything.
        //
        // The GL path cannot bind the camera's depth ATTACHMENT to our offscreen colour target - the
        // orientations disagree (GL render-into-texture y-flip, worse through ANGLE) and the splats
        // come out with holes punched through them. But the reduced-render-scale path already
        // sidesteps that by testing manually in the fragment shader against the camera depth
        // TEXTURE, sampled by normalised screen position, which has no attachment-size or
        // orientation coupling. That path was simply never enabled on GL. This turns it on.
        //
        // ON by default: splats silently drawing on top of the whole scene is a correctness bug,
        // and correctness should not require opting in. It is not free though - it forces URP's
        // depth copy (ConfigureInput(Depth)), a full-resolution pass that costs most on exactly the
        // low-end phones that need the occlusion. That is why the switch survives: on a device
        // fighting for frames, being able to trade occlusion back for the depth copy matters.
        internal static bool SplatDepthOcclusion = true;

        // Severity gate for this assembly's own instrumentation ([SplatRender DIAG],
        // [SplatSort DIAG], sorter fallback notices). This assembly cannot see
        // Viggle.SplatEngine.SplatLog - the assembly reference runs the other way - so
        // SplatSettings.Apply pushes the level down into this flag, exactly as it already pushes
        // GlobalScreenSizeCull and SplatRenderScale. False means "problems only", which is what a
        // shipping build should have.
        internal static bool s_LogDiagnostics = false;


        // The scale actually applied this frame (clamped), and the scaled RT size for a camera.
        internal static float EffectiveRenderScale => Mathf.Clamp(SplatRenderScale, 0.25f, 1.0f);
        internal static bool RenderScaleActive => EffectiveRenderScale < 0.999f;

        // True when the splat pass should depth-test in the fragment shader against the camera depth
        // texture rather than via a bound depth attachment. Three reasons to be here:
        //   - a scaled RT cannot bind the full-res depth attachment (sizes must match),
        //   - the camera is MSAA and our color target is not (see MsaaBlocksDepthAttachment),
        //   - we are on GL, where the attachment orientation disagrees and SplatDepthOcclusion
        //     opted us in.
        internal static bool ManualDepthWanted(bool isGL, bool msaa) =>
            RenderScaleActive || msaa || (isGL && SplatDepthOcclusion);

        /// <summary>
        /// Whether a multisampled camera makes the depth attachment unbindable for the splat pass.
        ///
        /// The splat color target is deliberately single-sample (accumulating premultiplied alpha
        /// per-sample would cost bandwidth for no visible gain, since the result is composited
        /// through a full-screen blit anyway). A render pass cannot mix a 1-sample color
        /// attachment with an N-sample depth attachment: Metal enforces it as a hard validation
        /// assert, which is a CRASH on iOS -
        ///     "For depth attachment, the texture sample count (2) does not match the
        ///      renderPipelineState rasterSampleCount (1)"
        /// - and other backends are free to misrender instead. So when the camera is multisampled
        /// we skip the attachment entirely and depth-test in the shader against the resolved
        /// _CameraDepthTexture, exactly as the scaled-RT path already does.
        /// </summary>
        internal static bool MsaaBlocksDepthAttachment(int cameraMsaaSamples) => cameraMsaaSamples > 1;

        internal static bool IsGLDevice =>
            SystemInfo.graphicsDeviceType is GraphicsDeviceType.OpenGLES3 or GraphicsDeviceType.OpenGLCore;
        internal static Vector2Int ScaledRTSize(int width, int height)
        {
            float s = EffectiveRenderScale;
            return new Vector2Int(Mathf.Max(1, Mathf.CeilToInt(width * s)), Mathf.Max(1, Mathf.CeilToInt(height * s)));
        }

        // 16-bit depth sort keys: quantize each splat's camera-space depth into 65534 buckets over
        // the renderer's own depth range, so the radix sort runs 2 passes instead of 4 - roughly
        // halving per-frame sort cost. Ordering can only differ from the full-float sort between
        // splats whose depths fall in the same bucket (bounds size / 65534 apart - fractions of a
        // millimeter for characters), which is far below any visible blend-order difference. Escape
        // hatch: set false to restore full 32-bit float keys. The bitonic/GLES fallback sorts the
        // same quantized keys and is unaffected by the pass-count win.
        internal static bool s_Sort16BitKeys = true;

        // The SH order actually uploaded. An asset only stores SH up to the degree its source file
        // carried, and a degree-0 capture stores a 32-byte stub instead of 32 bytes a splat - so
        // asking the shader for order 1 or higher on one would index a megabyte past the end of
        // that stub. D3D and Vulkan return zeroes for that; Metal faults the device. Clamping here
        // rather than at each of the two upload sites means neither can be forgotten.
        //
        // This is also what makes shOrder safe on a SkinnedSplatRenderer: a character's asset is
        // built with shDegree 0, so whatever the inherited field is set to, 0 is what is uploaded.
        internal int SHOrderEffective => m_Asset != null ? math.min(shOrder, m_Asset.shDegree) : shOrder;

        [SerializeField, HideInInspector]
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        internal int m_SortNthFrame = 1;
        [SerializeField, HideInInspector]
        [Tooltip("Skip sorting, view computation and drawing when the splat bounds (padded, see " +
                 "cull bounds padding) are entirely outside a camera's frustum. Practically free " +
                 "and a large win for crowds; turn off only if splats visibly vanish at screen " +
                 "edges because they deform far outside the asset's rest bounds.")]
        internal bool m_FrustumCull = true;
        [SerializeField, HideInInspector]
        [Range(0f, 1f)]
        [Tooltip("Fraction by which the asset bounds are expanded for the frustum cull, to cover " +
                 "animation moving splats outside the rest-pose bounds. 0.2 = 20% larger on each axis.")]
        internal float m_CullBoundsPad = 0.2f;

        [SerializeField, HideInInspector] internal RenderMode m_RenderMode = RenderMode.Splats;
        [SerializeField, HideInInspector]
        [Range(1.0f,15.0f)] internal float m_PointDisplaySize = 3.0f;

        // Fresnel rim, the second per-splat color hook (ModifySplatColor in
        // GaussianSplatColorModifier.hlsl). Not on the public surface and not drawn: the tint half
        // of that hook is what baseColor drives, and nothing re-exposes these. Kept serialized so
        // the values a scene already carries survive.
        [SerializeField, HideInInspector] [ColorUsage(false, true)] internal Color m_FresnelColor = Color.white;
        [SerializeField, HideInInspector] [Range(0f, 1f)] internal float m_FresnelStrength;
        [SerializeField, HideInInspector] [Range(0.25f, 16f)] internal float m_FresnelPower = 2f;
        [SerializeField, HideInInspector] [Range(0f, 0.99f)] internal float m_FresnelThreshold;

        // Occlusion-reveal participation (see SplatOcclusionReveal): whether this
        // renderer's splats may dissolve when they stand between the camera and the
        // reveal focus. Scenery participates by default; SkinnedSplatRenderer's
        // constructor flips the default off, so characters - least of all the focus
        // itself - never dissolve. Serialized so a scene can exempt a specific prop.
        [SerializeField, HideInInspector] internal bool m_OcclusionReveal = true;

        // Relight runtime state, driven every frame by the character's SplatRelight component
        // (which owns the user-facing parameters - add it via the Add Relighting button on
        // SkinnedSplatRenderer). Hidden rather than removed so code can still drive a renderer
        // directly; serialized values survive but SplatRelight overwrites them when present.
        [HideInInspector] public float m_RelightStrength;
        [HideInInspector] [ColorUsage(false, true)] public Color m_RelightTint = Color.white;
        [HideInInspector] public float m_RelightAmbient = 0.2f;
        [HideInInspector] public int m_RelightUpdateNthFrame = 1;
        // Driven by SplatRelight's debug switch; paints every splat with its own relight sample.
        [System.NonSerialized] public bool m_RelightDebugSplatView;

        // The screen-space lighting texture ProxyLightingPass renders each frame, pushed in by
        // SplatRelightingFeature just before the splat pass. Deliberately NOT a serialized
        // inspector field: it is a per-frame render target, not per-character authored data, so
        // exposing it would invite a stray texture being dragged in and baked into a prefab.
        [System.NonSerialized] public Texture m_RelightTexture;
        // Set alongside the texture: xy = 1/RT size in pixels (the RT is rendered at
        // SplatRelightingFeature.m_RenderScale, so it does NOT match the camera target),
        // z = 1/reference luminance for the brightness normalization, w = 1 when the texture
        // above is a real relight RT and 0 when nothing rendered this frame.
        [System.NonSerialized] public Vector4 m_RelightParams;

        [SerializeField, HideInInspector] internal GaussianCutout[] m_Cutouts;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        // Unified (cross-object) sorting, off by default: the render system owns ONE view buffer
        // for every splat object and hands each renderer a slice of it, so a single sorted draw can
        // interleave splats from different objects. While that is in effect m_GpuView is released
        // and this points at the shared buffer (which this renderer does not own), so the 48 bytes
        // a splat are relocated rather than duplicated. See SplatUnifiedSort.cs.
        [System.NonSerialized] GraphicsBuffer m_UnifiedView;
        // This renderer's first slot in the shared buffer, and the splat count that slice was
        // assigned for (so the system can notice a reload changing the count). -1 = no slice.
        [System.NonSerialized] internal int m_ViewBase = -1;
        [System.NonSerialized] internal int m_ViewLayoutCount;
        // Only allocated on the vertex-texture-fetch path (UseVertexTextureFetch). Holds the
        // per-splat view data (CSBuildViewTex output) so the render vertex shader can texelFetch
        // it instead of reading StructuredBuffers, which some GLES drivers forbid in that stage.
        internal RenderTexture m_GpuViewTex;

        // ---- UseNoComputePath resources (null on every other path) ----------------------------
        // The static per-splat textures the no-compute vertex shader texelFetches, and the CPU
        // depth sorter that produces its draw order. Built by whichever subclass has the decoded
        // arrays - only SkinnedSplatRenderer today, via AdoptNoComputeData - because SplatAsset
        // itself holds the packed GPU layout, which the CPU cannot read back cheaply.
        internal SplatNoComputeData m_NoComputeData;
        internal CpuSplatSorter m_CpuSorter;
        // Cross-object ordering (SplatNoComputeGlobalSort): this renderer's first slot in the
        // shared view-data textures, and the splat count that slice was assigned for - so the
        // system can notice a reload changing the count. -1 = no slice, which is also the state
        // the per-object path always sees. [NonSerialized] because it is a per-session layout
        // decision that must never bake into a scene or prefab.
        [System.NonSerialized] internal int m_NCViewBase = -1;
        [System.NonSerialized] internal int m_NCViewLayoutCount;

        /// <summary>
        /// Rebuild the no-compute source textures. Called from CreateResourcesForAsset, which is
        /// the ONLY correct moment: DisposeResourcesForAsset frees them along with every other
        /// per-asset resource, so anything that builds them before a Release/Create pair hands the
        /// renderer textures the next line destroys. Making this a hook rather than something each
        /// call site remembers to order correctly is deliberate - that ordering bug has been
        /// written twice already, in Setup and in RebuildAssetFromBin.
        ///
        /// Base handles static captures, from the .spz this component points at. A subclass with a
        /// different source overrides it (SkinnedSplatRenderer builds from the character bin).
        /// </summary>
        // Decoded capture arrays, kept from load. The packed sections cannot be re-read later:
        // SplatRenderAssetBuilder takes ownership of them and frees them as it consumes them, and
        // re-reading the file at hook time returned uninitialised memory (SplatSpzFile.Alloc uses
        // UninitializedMemory, so a short read is silent garbage rather than an error). Decoding
        // once while the good copy is in hand, and holding the result, is the same shape as
        // SkinnedSplatRenderer keeping m_Bin alive for exactly this reason.
        NativeArray<float3> m_CapPos;
        NativeArray<quaternion> m_CapRot;
        NativeArray<float3> m_CapScale;
        NativeArray<Color32> m_CapColor;
        NativeArray<float> m_CapOpacity;

        /// <summary>
        /// Decode a capture for the no-compute path. Called at load, BEFORE the packed sections are
        /// handed to SplatRenderAssetBuilder. No-op unless UseNoComputePath.
        /// </summary>
        void DecodeCaptureForNoCompute(SpzPackedData packed)
        {
            if (!UseNoComputePath) return;
            int n = packed.splatCount;
            if (n <= 0) return;

            ReleaseCaptureArrays();
            m_CapPos = new NativeArray<float3>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_CapRot = new NativeArray<quaternion>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_CapScale = new NativeArray<float3>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_CapColor = new NativeArray<Color32>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_CapOpacity = new NativeArray<float>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            SplatCaptureJobs.DecodeToArrays(packed, m_CapPos, m_CapRot, m_CapScale, m_CapColor, m_CapOpacity);
        }

        internal void ReleaseCaptureArrays()
        {
            if (m_CapPos.IsCreated) m_CapPos.Dispose();
            if (m_CapRot.IsCreated) m_CapRot.Dispose();
            if (m_CapScale.IsCreated) m_CapScale.Dispose();
            if (m_CapColor.IsCreated) m_CapColor.Dispose();
            if (m_CapOpacity.IsCreated) m_CapOpacity.Dispose();
        }

        /// <summary>
        /// Rebuild the no-compute source textures. Called from CreateResourcesForAsset, which is
        /// the ONLY correct moment: DisposeResourcesForAsset frees them along with every other
        /// per-asset resource, so anything that builds them before a Release/Create pair hands the
        /// renderer textures the next line destroys. Making this a hook rather than something each
        /// call site remembers to order correctly is deliberate - that ordering bug has been
        /// written twice already, in Setup and in RebuildAssetFromBin.
        ///
        /// Base builds from the capture arrays decoded at load. A subclass with a different source
        /// overrides it (SkinnedSplatRenderer builds from the character bin).
        /// </summary>
        internal virtual void CreateNoComputeResources()
        {
            if (!UseNoComputePath || !m_CapPos.IsCreated) return;
            // A capture carries no bones: the default (uncreated) arrays leave hasBones false, so
            // the shader's skinning branch stays off.
            AdoptNoComputeData(SplatNoComputeData.Build(
                m_CapPos, m_CapRot, m_CapScale, m_CapColor, m_CapOpacity, default, default));
        }

        float4[] m_NoComputeDepthRows;

        /// <summary>
        /// Per-bone depth rows for CpuSplatSorter: row 2 of (worldToCamera * localToWorld * M_b),
        /// so the sorter gets view-space depth from a single dot product per splat. This base
        /// implementation is the unskinned case - one row that every splat shares. Returns true
        /// when the rows are per-bone and must be indexed by dominantBone.
        /// </summary>
        internal virtual bool BuildNoComputeDepthRows(Camera cam, out float4[] rows)
        {
            m_NoComputeDepthRows ??= new float4[1];
            Matrix4x4 m = cam.worldToCameraMatrix * transform.localToWorldMatrix;
            m_NoComputeDepthRows[0] = new float4(m.m20, m.m21, m.m22, m.m23);
            rows = m_NoComputeDepthRows;
            return false;
        }

        /// <summary>
        /// Re-sorts (when <paramref name="doSort"/>) and binds everything
        /// RenderGaussianSplatsNoCompute.shader reads. The sort is the expensive half, hence the
        /// flag: the caller throttles it with the same moved/sortNthFrame signal the compute path
        /// uses, so a still character stops paying for it exactly as it does there.
        /// </summary>
        internal void BindNoCompute(MaterialPropertyBlock mpb, Camera cam, bool doSort)
        {
            if (m_NoComputeData == null || m_CpuSorter == null)
                return;

            if (doSort)
            {
                bool skinned = BuildNoComputeDepthRows(cam, out float4[] rows);
                m_CpuSorter.Sort(m_NoComputeData.positions, m_NoComputeData.dominantBone, rows, skinned);
            }
            BindNoComputeSource(mpb, cam);
            mpb.SetTexture(Props.OrderTex, m_CpuSorter.orderTexture);
        }

        /// <summary>
        /// Binds everything the no-compute PROJECTION needs - source textures, bone palette,
        /// matrices - and nothing about draw order. Split out from <see cref="BindNoCompute"/>
        /// because cross-object ordering projects and draws in separate passes with a single
        /// shared order between them, so it wants exactly this half and no order texture at all.
        /// </summary>
        internal void BindNoComputeSource(MaterialPropertyBlock mpb, Camera cam)
        {
            var d = m_NoComputeData;
            if (d == null)
                return;

            // The transform matrices. On the compute path these are set on splatUtilities inside
            // CalcViewData, which this path never runs - so without this block the vertex shader
            // projects through uninitialised matrices and every splat lands somewhere arbitrary.
            //
            // Derived exactly as CalcViewData derives them, including GL.GetGPUProjectionMatrix
            // with renderIntoTexture: true. Passed explicitly rather than relying on the
            // UNITY_MATRIX_* globals for the same reason stated there - they are not reliably
            // populated on OpenGL ES, which is the only platform this path runs on.
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = transform.localToWorldMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            mpb.SetMatrix(Props.MatrixVP, matProj * matView);
            mpb.SetMatrix(Props.MatrixP, matProj);
            mpb.SetMatrix(Props.MatrixMV, matView * matO2W);
            mpb.SetMatrix(Props.MatrixObjectToWorld, matO2W);

            // Occlusion reveal, same upload as the compute path's CalcViewData.
            mpb.SetVector(Props.GsRevealPos, SplatOcclusionReveal.PosStrength(m_OcclusionReveal));
            mpb.SetVector(Props.GsRevealCam, cam.transform.position);
            mpb.SetVector(Props.GsRevealShape, SplatOcclusionReveal.Shape);

            mpb.SetTexture(Props.SplatPosOpacityTex, d.posOpacityTex);
            mpb.SetTexture(Props.SplatRotTex, d.rotTex);
            mpb.SetTexture(Props.SplatScaleTex, d.scaleTex);
            mpb.SetTexture(Props.SplatColTex, d.colorTex);
            mpb.SetInteger(Props.SplatTexWidth, d.texWidth);

            // The projection's own scalars. The per-object path also sets these in the render loop
            // (along with a pile of compute-path uniforms this shader ignores), which is harmless;
            // they are set HERE because the pre-pass runs nowhere near that loop and the covariance
            // projection is wrong without the viewport size - silently, as a focal length error.
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            mpb.SetVector(Props.VecScreenParams,
                new Vector4(eyeW != 0 ? eyeW : cam.pixelWidth, eyeH != 0 ? eyeH : cam.pixelHeight, 0, 0));
            mpb.SetFloat(Props.SplatScale, splatScale);
            mpb.SetFloat(Props.SplatOpacityScale, opacityScale);
            mpb.SetInteger(Props.SplatCount, d.splatCount);

            // Skinning is bound only when the skinner has actually posed at least once. Until
            // then the bone textures may not exist, and the shader must draw the bind pose.
            bool skinning = m_NoComputeSkinningActive != 0 && m_NoComputeBonePoseTex != null &&
                            d.hasBones;
            mpb.SetInteger(Props.SplatNCSkinningActive, skinning ? 1 : 0);
            if (skinning)
            {
                mpb.SetTexture(Props.SplatBoneIdxTex, d.boneIdxTex);
                mpb.SetTexture(Props.SplatBoneWtTex, d.boneWtTex);
                mpb.SetTexture(Props.BonePoseTex, m_NoComputeBonePoseTex);
            }
        }

        /// <summary>
        /// Hands this renderer the baked no-compute data and spins up its CPU sorter. Takes
        /// ownership: both are disposed with the rest of the per-asset resources. No-op unless
        /// UseNoComputePath, so a caller can build unconditionally and let the path decide.
        /// </summary>
        internal void AdoptNoComputeData(SplatNoComputeData data)
        {
            if (!UseNoComputePath || data == null)
            {
                data?.Dispose();
                return;
            }
            m_NoComputeData?.Dispose();
            m_CpuSorter?.Dispose();
            m_NoComputeData = data;
            m_CpuSorter = new CpuSplatSorter(data.splatCount, data.texWidth);
        }
        internal GraphicsBuffer m_GpuIndexBuffer;
        // Screen-size-cull compaction (GlobalScreenSizeCull): a single-uint visible tally and the
        // indirect-draw args the sort compaction writes, so the splat draw runs over only the
        // visible prefix. Null/unused when the cull is disabled (the plain DrawProcedural path).
        GraphicsBuffer m_GpuVisibleCounter;
        internal GraphicsBuffer m_GpuDrawArgs; // read by GaussianSplatRenderSystem for the indirect draw

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;
        GpuBitonicSort m_BitonicSorter; // last-resort fallback, 32-bit keys only (see m_CountingSorter)
        // Preferred fallback when radix (wave ops) is unsupported: O(n) and 7 dispatches against
        // the bitonic network's 153. Requires 16-bit keys, so bitonic stays for the 32-bit case.
        GpuCountingSort m_CountingSorter;
        GpuCountingSort.Resources m_CountingRes;

        // Dummy buffers for skinning globals (must always be bound even when skinning is inactive)
        static GraphicsBuffer s_DummySkinPos;
        static GraphicsBuffer s_DummySkinRot;
        // Per-renderer skinning override (set by an external skinner). Instance, not static,
        // so multiple skinned characters with different splat buffers don't stomp each other.
        internal GraphicsBuffer m_SkinPosOverride;
        internal GraphicsBuffer m_SkinRotOverride;
        internal int m_SkinningActive;

        // UseNoComputePath equivalents. Deliberately NOT m_SkinningActive: that flag means "read
        // already-skinned positions from m_SkinPosOverride", and on this path no such buffer
        // exists - the vertex shader skins from the bind data itself. Stamped by the skinner, in
        // the other assembly, exactly like m_SkinningActive.
        internal int m_NoComputeSkinningActive;
        internal Texture m_NoComputeBonePoseTex;
        // Frame index on which the skinner last actually re-posed this character (animation LOD). The
        // static-skip in SortAndRenderSplats treats "posed this frame" as movement; on frames the
        // skinner throttled out, the splats didn't move, so the sort + CalcViewData are reused. Public
        // because SplatCharacterSkinner (a different assembly) stamps it, like m_SkinningActive.
        internal int m_LastPosedFrame = int.MinValue;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal static bool Released { get; private set; }
        internal int m_FrameCounter;
        SplatAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;
        bool m_SorterInvalidLogged;
        internal bool m_SkippedLogged;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");

            // UseNoComputePath: the source textures RenderGaussianSplatsNoCompute.shader reads.
            public static readonly int SplatPosOpacityTex = Shader.PropertyToID("_SplatPosOpacityTex");
            public static readonly int SplatRotTex = Shader.PropertyToID("_SplatRotTex");
            public static readonly int SplatScaleTex = Shader.PropertyToID("_SplatScaleTex");
            public static readonly int SplatColTex = Shader.PropertyToID("_SplatColTex");
            public static readonly int SplatBoneIdxTex = Shader.PropertyToID("_SplatBoneIdxTex");
            public static readonly int SplatBoneWtTex = Shader.PropertyToID("_SplatBoneWtTex");
            public static readonly int OrderTex = Shader.PropertyToID("_OrderTex");
            public static readonly int BonePoseTex = Shader.PropertyToID("_BonePoseTex");
            public static readonly int SplatTexWidth = Shader.PropertyToID("_SplatTexWidth");
            public static readonly int SplatNCSkinningActive = Shader.PropertyToID("_SplatNCSkinningActive");
            // UseNoComputePath cross-object ordering: the shared view-data textures the pre-pass
            // (pass 2) writes and the merged draw (pass 1) reads, plus the pre-pass's own slice
            // parameters. See SplatNoComputeGlobalSort.cs.
            public static readonly int GsViewPosTex = Shader.PropertyToID("_GsViewPosTex");
            public static readonly int GsViewAxisTex = Shader.PropertyToID("_GsViewAxisTex");
            public static readonly int GsViewColTex = Shader.PropertyToID("_GsViewColTex");
            public static readonly int GsViewTexWidth = Shader.PropertyToID("_GsViewTexWidth");
            public static readonly int GsDrawCount = Shader.PropertyToID("_GsDrawCount");
            public static readonly int GsDrawSlotBase = Shader.PropertyToID("_GsDrawSlotBase");
            public static readonly int GsViewRect = Shader.PropertyToID("_GsViewRect");
            public static readonly int GsViewBase = Shader.PropertyToID("_GsViewBase");
            public static readonly int GsViewFlipY = Shader.PropertyToID("_GsViewFlipY");
            // Lengths of the two buffers the splat vertex shader indexes. Must be supplied: the
            // shader used to query them with GetDimensions, which on Metal lowers to an auxiliary
            // "spvBufferSizeConstants" buffer Unity never binds - the query returned garbage, the
            // bounds clamp did nothing, and the draw faulted with a GPU address fault.
            public static readonly int OrderBufferCount = Shader.PropertyToID("_OrderBufferCount");
            public static readonly int SplatViewDataCount = Shader.PropertyToID("_SplatViewDataCount");
            // Unified (cross-object) sorting. _ViewBase is this renderer's offset into the shared
            // view buffer (0 in the normal mode); the _Uni* set drives CSUnifiedBuildKeys.
            public static readonly int ViewBase = Shader.PropertyToID("_ViewBase");
            public static readonly int UniRegions = Shader.PropertyToID("_UniRegions");
            public static readonly int UniRegionCount = Shader.PropertyToID("_UniRegionCount");
            public static readonly int UniCount = Shader.PropertyToID("_UniCount");
            public static readonly int UniViewCount = Shader.PropertyToID("_UniViewCount");
            public static readonly int UniKeyBits = Shader.PropertyToID("_UniKeyBits");
            public static readonly int UniDepthParams = Shader.PropertyToID("_UniDepthParams");
            public static readonly int UniViewDataTex = Shader.PropertyToID("_UniViewDataTex");
            public static readonly int UniViewTexWidth = Shader.PropertyToID("_UniViewTexWidth");
            public static readonly int SplatViewDataTex = Shader.PropertyToID("_SplatViewDataTex");
            public static readonly int SplatViewTexWidth = Shader.PropertyToID("_SplatViewTexWidth");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatScreenSizeCull = Shader.PropertyToID("_SplatScreenSizeCull");
            public static readonly int CullToBack = Shader.PropertyToID("_CullToBack");
            public static readonly int VisibleCounter = Shader.PropertyToID("_VisibleCounter");
            public static readonly int DrawArgs = Shader.PropertyToID("_DrawArgs");
            public static readonly int DrawIndexCount = Shader.PropertyToID("_DrawIndexCount");
            public static readonly int DrawMaxInstances = Shader.PropertyToID("_DrawMaxInstances");
            public static readonly int SplatCountFull = Shader.PropertyToID("_SplatCountFull");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int GaussianSplatRTParams = Shader.PropertyToID("_GaussianSplatRTParams");
            public static readonly int SplatManualDepthTest = Shader.PropertyToID("_SplatManualDepthTest");
            public static readonly int SplatDepthTexParams = Shader.PropertyToID("_SplatDepthTexParams");
            public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            public static readonly int GsRevealPos = Shader.PropertyToID("_GsRevealPos");
            public static readonly int GsRevealCam = Shader.PropertyToID("_GsRevealCam");
            public static readonly int GsRevealShape = Shader.PropertyToID("_GsRevealShape");
            public static readonly int GsTintColor = Shader.PropertyToID("_GsTintColor");
            public static readonly int GsTintStrength = Shader.PropertyToID("_GsTintStrength");
            public static readonly int GsFresnelColor = Shader.PropertyToID("_GsFresnelColor");
            public static readonly int GsFresnelStrength = Shader.PropertyToID("_GsFresnelStrength");
            public static readonly int GsFresnelPower = Shader.PropertyToID("_GsFresnelPower");
            public static readonly int GsFresnelThreshold = Shader.PropertyToID("_GsFresnelThreshold");
            public static readonly int GsRelightTex = Shader.PropertyToID("_GsRelightTex");
            public static readonly int GsRelightParams = Shader.PropertyToID("_GsRelightParams");
            public static readonly int GsRelightStrength = Shader.PropertyToID("_GsRelightStrength");
            public static readonly int GsRelightTint = Shader.PropertyToID("_GsRelightTint");
            public static readonly int GsRelightAmbient = Shader.PropertyToID("_GsRelightAmbient");
            public static readonly int GsRelightFlipY = Shader.PropertyToID("_GsRelightFlipY");
            public static readonly int GsRelightDebug = Shader.PropertyToID("_GsRelightDebug");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SortKeysInvert = Shader.PropertyToID("_SortKeysInvert");
            public static readonly int SortDepthParams = Shader.PropertyToID("_SortDepthParams");
            public static readonly int Sort16BitKeys = Shader.PropertyToID("_Sort16BitKeys");
            public static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
            public static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int SortMatrixMV = Shader.PropertyToID("_SortMatrixMV");
            public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
            public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
            public static readonly int SplatSkinPos = Shader.PropertyToID("_SplatSkinPos");
            public static readonly int SplatSkinRot = Shader.PropertyToID("_SplatSkinRot");
            public static readonly int SplatSkinningActive = Shader.PropertyToID("_SplatSkinningActive");
        }

        [field: NonSerialized] internal bool editModified { get; private set; }
        [field: NonSerialized] internal uint editSelectedSplats { get; private set; }
        [field: NonSerialized] internal uint editDeletedSplats { get; private set; }
        [field: NonSerialized] internal uint editCutSplats { get; private set; }
        [field: NonSerialized] internal Bounds editSelectedBounds { get; private set; }

        internal SplatAsset asset => m_Asset;
        internal int splatCount => m_SplatCount;

        // Distance LOD: the number of splats to actually process this frame - the top-K importance
        // prefix. No driver ships with the package today (distance LOD returns as separate baked
        // LOD files); this stays as the mechanism a host can drive. Set each frame by the
        // LOD driver (SplatCharacterSkinner); <=0 or >= splatCount means "process everything".
        // Honored on both sorter paths: the radix sorter sorts exactly the prefix, the bitonic/GLES
        // fallback sorts the smallest power-of-two region covering it (see SortPoints / CanLod).
        // Per-character LOD state, driven per frame by SplatCharacterSkinner (play) or SkinnedSplatRenderer
        // (edit). [NonSerialized]: this is runtime-only and must NEVER bake into a scene/prefab. Written
        // only through SetLod/ClearLod so the count and the active flag can never disagree.
        [System.NonSerialized] int m_ActiveSplatCount;
        [System.NonSerialized] bool m_LodActive;
        // True once the sort-key buffer was reseeded over only a [0..ProcCount) prefix (LOD), so it is no
        // longer a valid full [0..splatCount) permutation and must be rebuilt before the next full sort.
        [System.NonSerialized] bool m_SortKeysPartial;
        // False until CalcViewData has written every entry of m_GpuView at least once. A fresh
        // GraphicsBuffer holds undefined memory, so the first dispatch must cover the full splat
        // count rather than the LOD prefix (see CalcViewData).
        [System.NonSerialized] bool m_ViewInitialized;

        // Static-scene sort skip (see s_SkipStaticSort): the camera pose, this renderer's transform, and
        // the processed splat count as of the last sort. Runtime-only; used to detect "nothing moved".
        [System.NonSerialized] internal Matrix4x4 m_LastSortCamera;
        [System.NonSerialized] internal Matrix4x4 m_LastSortModel;
        [System.NonSerialized] internal int m_LastSortProcCount;
        [System.NonSerialized] internal bool m_HasSortedOnce;

        /// <summary>
        /// Force the next frame to re-run the depth sort and CalcViewData for this renderer.
        /// </summary>
        /// <remarks>
        /// The static-scene skip reuses the previous sort AND the previous CalcViewData output when
        /// neither the camera, this character's transform, its LOD count, nor active skinning has
        /// changed. That is correct for motion, but it also means anything else feeding CalcViewData -
        /// the tint / base color, opacity, the fresnel parameters - changes nothing on screen until
        /// something happens to move. Most visible while authoring: dragging the color wheel on a
        /// static character in the Scene view appeared to do nothing at all.
        ///
        /// Call this after changing any value ModifySplatColor reads.
        /// </remarks>
        internal void InvalidateViewData() => m_HasSortedOnce = false;

        /// <summary>
        /// Override the splat pass blend factors (UnityEngine.Rendering.BlendMode values).
        /// One/OneMinusSrcAlpha is the normal premultiplied "over"; One/One makes each covered
        /// fragment ADD, which is what the overdraw heatmap uses to count layers per pixel.
        /// </summary>
        /// <remarks>
        /// Has to be set on the material, not via Shader.SetGlobal*: the factors are declared as
        /// shader Properties, so the material always carries its own value and a global is ignored.
        /// </remarks>
        internal void SetSplatBlend(int srcBlend, int dstBlend)
        {
            if (m_MatSplats == null) return;
            m_MatSplats.SetFloat("_SrcBlend", srcBlend);
            m_MatSplats.SetFloat("_DstBlend", dstBlend);
        }
        // Sort-LOD: stable per-renderer phase (seeded from the instance id) so throttled re-sorts are
        // staggered across a crowd instead of all landing on the same frame. -1 = not yet seeded.
        [System.NonSerialized] internal int m_SortPhase = -1;

        // Frustum-cull visibility stamps, written by GaussianSplatRenderSystem.GatherSplatsForCamera:
        // the last frame any camera ran the cull test on this renderer, and the last frame it passed
        // (was inside at least one camera's frustum). -1 = never. Drives IsRecentlyVisible below.
        [System.NonSerialized] internal int m_LastCullTestFrame = -1;
        [System.NonSerialized] internal int m_LastVisibleFrame = -1;
        // Camera-space depth of this renderer's origin, precomputed per gather for the draw-order
        // sort (see GaussianSplatRenderSystem.ActiveSplatComparer).
        [System.NonSerialized] internal float m_CamSortDepth;

        // True unless the frustum cull recently tested this renderer and no camera saw it. Errs
        // toward "visible": before the first cull test, and when no camera has rendered lately
        // (so stale stamps can't freeze a character), it returns true. Used by drivers (e.g.
        // SplatCharacterSkinner) to pause per-frame work for characters nobody can see.
        internal bool IsRecentlyVisible(int withinFrames = 2)
        {
            if (m_LastCullTestFrame < 0)
                return true;
            int frame = Time.frameCount;
            if (frame - m_LastCullTestFrame > withinFrames)
                return true;
            return m_LastVisibleFrame >= 0 && frame - m_LastVisibleFrame <= withinFrames;
        }

        // World-space AABB for the frustum cull: the asset's rest bounds expanded by
        // m_CullBoundsPad, transformed by this renderer's matrix (axis-aligned re-fit via the
        // absolute rotation/scale terms). Skinned characters deform near the rig, which the
        // padding covers - see the m_FrustumCull tooltip.
        internal bool TryGetWorldCullBounds(out Bounds bounds)
        {
            var asset = m_Asset;
            if (asset == null)
            {
                bounds = default;
                return false;
            }
            Vector3 bmin = asset.boundsMin, bmax = asset.boundsMax;
            Vector3 center = (bmin + bmax) * 0.5f;
            Vector3 ext = (bmax - bmin) * (0.5f * (1f + m_CullBoundsPad));
            // Yaw-agnostic in the horizontal plane: skinned characters render pre-yawed 90° from
            // the asset's rest orientation (bin faces +X, rig faces +Z), which would swap the x/z
            // extents. Taking the max keeps the box conservative for any yaw.
            ext.x = ext.z = Mathf.Max(ext.x, ext.z);
            Matrix4x4 m = transform.localToWorldMatrix;
            Vector3 wc = m.MultiplyPoint3x4(center);
            Vector3 we = new Vector3(
                Mathf.Abs(m.m00) * ext.x + Mathf.Abs(m.m01) * ext.y + Mathf.Abs(m.m02) * ext.z,
                Mathf.Abs(m.m10) * ext.x + Mathf.Abs(m.m11) * ext.y + Mathf.Abs(m.m12) * ext.z,
                Mathf.Abs(m.m20) * ext.x + Mathf.Abs(m.m21) * ext.y + Mathf.Abs(m.m22) * ext.z);
            bounds = new Bounds(wc, we * 2f);
            return true;
        }

        // Conservative camera-space depth span of this renderer's (padded) bounds. Same construction
        // as the 16-bit sort key range in SortPoints, in the one space every splat object shares, so
        // unified sorting can take the union over the visible objects and quantize its global key
        // over that. Camera space looks down -z, so zMin is the FAR edge.
        internal bool TryGetCamDepthRange(Matrix4x4 worldToCam, out float zMin, out float zMax)
        {
            zMin = zMax = 0f;
            var asset = m_Asset;
            if (asset == null)
                return false;
            Matrix4x4 mv = worldToCam * transform.localToWorldMatrix;
            Vector3 bmin = asset.boundsMin, bmax = asset.boundsMax;
            Vector3 center = (bmin + bmax) * 0.5f;
            Vector3 ext = (bmax - bmin) * (0.5f * (1f + m_CullBoundsPad));
            // Yaw-agnostic in x/z, as everywhere else: a skinned character renders pre-yawed 90
            // degrees from its asset's rest orientation.
            ext.x = ext.z = Mathf.Max(ext.x, ext.z);
            float zc = mv.m20 * center.x + mv.m21 * center.y + mv.m22 * center.z + mv.m23;
            float ze = Mathf.Abs(mv.m20) * ext.x + Mathf.Abs(mv.m21) * ext.y + Mathf.Abs(mv.m22) * ext.z;
            zMin = zc - ze;
            zMax = zc + ze;
            return true;
        }

        // Whether this renderer can actually apply LOD. Radix path: sorts exactly the top-K prefix.
        // Bitonic/GLES fallback: sorts the smallest power-of-two region >= K with the tail sentinel-
        // filled (see SortPoints), so LOD works there too - and matters most there, since those are
        // the weakest devices. Drivers MUST gate their processing/skinning count on this so skin
        // output and draw count stay in lockstep.
        internal bool CanLod => (m_Sorter != null && m_Sorter.Valid && m_SorterArgs.resources.altBuffer != null)
                              || (m_CountingSorter != null && m_CountingSorter.Valid)
                              || (m_BitonicSorter != null && m_BitonicSorter.Valid);
        // Set the LOD state atomically. active == false means full detail (count ignored).
        internal void SetLod(int activeCount, bool active) { m_LodActive = active; m_ActiveSplatCount = active ? activeCount : m_SplatCount; }
        // Reset to full detail. Call when a LOD driver stops (teardown, rest pose, component disabled) so
        // the renderer doesn't stay frozen at a stale top-K prefix.
        internal void ClearLod() { m_LodActive = false; m_ActiveSplatCount = m_SplatCount; }

        internal int ActiveSplatCount => (m_ActiveSplatCount > 0 && m_ActiveSplatCount < m_SplatCount) ? m_ActiveSplatCount : m_SplatCount;
        internal bool LodDynamic => m_LodActive && CanLod;
        // The count every per-splat pass (sort, view compute, draw) must agree on this frame.
        internal int ProcCount => LodDynamic ? ActiveSplatCount : m_SplatCount;

        // Read-only introspection for debug UIs / verification: whether distance LOD is currently
        // reducing this renderer, and how many splats are actually processed this frame.
        internal bool IsLodActive => LodDynamic && ProcCount < m_SplatCount;
        internal int ProcessedSplatCount => ProcCount;

        // Exposed for skinning system
        internal GraphicsBuffer gpuPosData => m_GpuPosData;
        internal GraphicsBuffer gpuOtherData => m_GpuOtherData;
        internal GraphicsBuffer gpuChunkData => m_GpuChunks;
        internal int gpuSplatFormat => (int)((uint)m_Asset.posFormat |
            ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16));
        internal int gpuChunkCount => m_GpuChunksValid ? m_GpuChunks.count : 0;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
            BuildViewTex,
            // Screen-size-cull compaction. Positional: must match the #pragma kernel order in
            // SplatUtilities.compute (right after CSBuildViewTex, before the by-name bitonic kernels).
            ClearVisibleCounter,
            WriteDrawArgs,
            // Bitonic-LOD sentinel fill (also positional, right after CSWriteDrawArgs).
            FillSortDistances,
        }

        internal bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == SplatAsset.kCurrentVersion &&
            m_Asset.HasData;
        // "Are this object's render resources built?" - which resources those are depends on the
        // path. The compute path's three packed buffers are deliberately never created on the
        // no-compute path (GLES 3.0 has no storage buffers), so testing for them there reports a
        // failure that is actually the design.
        internal bool HasValidRenderSetup => UseNoComputePath
            ? m_NoComputeData != null && m_CpuSorter != null && m_GpuIndexBuffer != null
            : m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        // 40 bytes of data + 8 pad = 48; must equal sizeof(SplatViewData) in GaussianSplatting.hlsl.
        // GLES std430 rounds that struct's stride to a multiple of 16, so the buffer must be 48 too
        // or the vertex shader reads every splat past the first misaligned (ANGLE/OpenGL ES only).
        internal const int kGpuViewDataSize = 48;

        // Where this renderer's projected splats live, and at what offset. Identical to m_GpuView at
        // offset 0 unless unified sorting is on (see m_UnifiedView).
        internal GraphicsBuffer ViewBuffer => m_UnifiedView ?? m_GpuView;
        internal bool UsesUnifiedView => m_UnifiedView != null;

        /// <summary>
        /// Point this renderer's view data at a slice of the render system's shared buffer, or
        /// (with a null buffer) back at its own. Called only by <see cref="SplatUnifiedSort"/>.
        /// </summary>
        /// <remarks>
        /// Switching either way invalidates everything projected so far: the data is in the wrong
        /// place, and a freshly allocated destination holds undefined memory, so the next
        /// CalcViewData must cover the full splat count rather than a LOD prefix.
        /// </remarks>
        internal void SetUnifiedView(GraphicsBuffer shared, int baseIndex, int layoutCount)
        {
            if (shared != null)
            {
                // The private buffer (and the vertex-texture-fetch texture built from it) is dead
                // weight now: the system's shared pair replaces both.
                DisposeBuffer(ref m_GpuView);
                ReleaseViewTexture();
                m_UnifiedView = shared;
                m_ViewBase = baseIndex;
                m_ViewLayoutCount = layoutCount;
            }
            else
            {
                m_UnifiedView = null;
                m_ViewBase = -1;
                m_ViewLayoutCount = 0;
                if (m_GpuView == null && m_Asset != null && m_Asset.splatCount > 0)
                {
                    m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);
                    CreateViewTexture();
                }
            }
            m_ViewInitialized = false;
            InvalidateViewData();   // the reused sort order is also stale (unified mode does not sort per object)
        }

        // Cube indices; most often only the first quad is used. Target.Index is plain geometry -
        // no compute, no storage buffer - so both render paths share it.
        void CreateQuadIndexBuffer()
        {
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });
        }

        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;

            m_SplatCount = asset.splatCount;
            ClearLod();                 // full detail until a LOD driver lowers it per frame
            m_SortKeysPartial = false;  // InitSortBuffers reseeds a full [0..N) identity below
            m_HasSortedOnce = false;    // reseeded keys are unsorted; force a sort before any static-skip

            if (UseNoComputePath)
            {
                // Everything below this point is a Raw or Structured GraphicsBuffer, or a
                // random-write RenderTexture. GLES 3.0 has no shader storage buffers and no
                // compute, so Unity throws outright on each one:
                //   "Attempting to create a graphics buffer that requires compute shader support"
                // None of them has a consumer here anyway - the packed asset layout is replaced by
                // SplatNoComputeData's textures, the view buffer by in-shader projection, and the
                // sort buffers by CpuSplatSorter's order texture.
                //
                // The index buffer is the exception: Target.Index is plain geometry, it needs no
                // compute, and the draw still indexes through it.
                CreateQuadIndexBuffer();
                CreateNoComputeResources();
                return;
            }

            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.PosDataSize / 4), 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(asset.GetPosData<uint>());
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.OtherDataSize / 4), 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(asset.GetOtherData<uint>());
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.SHDataSize / 4), 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(asset.GetSHData<uint>());
            var (texWidth, texHeight) = SplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = SplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.GetColorData<byte>(), 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<SplatChunkInfo>()),
                    UnsafeUtility.SizeOf<SplatChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(asset.chunkData.GetData<SplatChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<SplatChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);
            // A fresh GraphicsBuffer's contents are UNDEFINED, so the first CalcViewData must cover
            // every entry rather than just the LOD prefix (see m_ViewInitialized in CalcViewData).
            m_ViewInitialized = false;
            CreateViewTexture();
            CreateQuadIndexBuffer();

            InitSortBuffers(splatCount);

            // Screen-size-cull compaction resources. The args are seeded to draw the full splat
            // count so the very first frames (before the first sort compaction runs) draw everything.
            m_GpuVisibleCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GaussianVisibleCounter" };
            // Structured too, so CSWriteDrawArgs can bind it as an RWStructuredBuffer<uint> UAV to
            // fill it, while IndirectArguments lets DrawProceduralIndirect consume it.
            m_GpuDrawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, 4) { name = "GaussianSplatDrawArgs" };
            m_GpuDrawArgs.SetData(new uint[] { 6, (uint)m_SplatCount, 0, 0, 0 });

            // Ensure skinning globals are always bound (dummy when no skinning component is active)
            InitSkinningDummyBuffers();
        }

        // The packed view-data texture for the vertex-texture-fetch path. RGBA32F, 3 texels per
        // splat (see CSBuildViewTex). enableRandomWrite so the compute kernel can imageStore into
        // it; sampled (point) in the render vertex shader. Only allocated when that path is used.
        void CreateViewTexture()
        {
            // UInt, not SFloat: CSBuildViewTex packs fp16 pairs and raw bit patterns into these
            // texels, and an SFloat target may flush NaN/Inf bit patterns. UInt passes them through
            // untouched, which is what allows 2 texels per splat instead of 3.
            const GraphicsFormat fmt = GraphicsFormat.R32G32B32A32_UInt;
            LogRenderPathDiagnostics(fmt);
            if (!UseVertexTextureFetch)
                return;
            ReleaseViewTexture(); // re-entrancy guard (e.g. re-enable) so we don't leak the RT

            const int kTexelsPerSplat = 2;   // must match CSBuildViewTex and the shader's VTF branch
            int texels = m_Asset.splatCount * kTexelsPerSplat;
            int height = (texels + kViewTexWidth - 1) / kViewTexWidth;
            var desc = new RenderTextureDescriptor(kViewTexWidth, Mathf.Max(height, 1), fmt, 0)
            {
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            m_GpuViewTex = new RenderTexture(desc)
            {
                name = "GaussianViewTex",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!m_GpuViewTex.Create())
                Debug.LogError($"SplatRenderer '{name}': failed to create {kViewTexWidth}x{height} {fmt} view texture " +
                               $"(randomWrite supported={SystemInfo.IsFormatSupported(fmt, FormatUsage.LoadStore)}); splats will not render", this);
        }

        // One-shot snapshot (per session) of which render path was chosen and the capability
        // values behind that choice, plus whether the chosen view-texture format is actually
        // usable for compute store + vertex sampling. Logged as a warning so it survives
        // "errors+warnings only" logcat filters on a remote tester's device.
        static void LogRenderPathDiagnostics(GraphicsFormat viewTexFormat)
        {
            if (s_RenderPathLogged)
                return;
            s_RenderPathLogged = true;
            if (!s_LogDiagnostics) return;
            Debug.Log($"[SplatRender DIAG] api={SystemInfo.graphicsDeviceType} " +
                      $"gpu={SystemInfo.graphicsDeviceName} " +
                      $"driver={SystemInfo.graphicsDeviceVersion} " +
                      $"maxComputeBufferInputsVertex={SystemInfo.maxComputeBufferInputsVertex} " +
                      $"vertexTextureFetch={UseVertexTextureFetch} " +
                      $"(forced={s_ForceVertexTextureFetch}) " +
                      $"viewTexFormat={viewTexFormat} " +
                      $"loadStore={SystemInfo.IsFormatSupported(viewTexFormat, FormatUsage.LoadStore)} " +
                      $"sample={SystemInfo.IsFormatSupported(viewTexFormat, FormatUsage.Sample)}");
        }

        static void InitSkinningDummyBuffers()
        {
            if (s_DummySkinPos == null)
                // stride 16 (float4, .w unused) - must match _SplatSkinPos in GaussianSplatting.hlsl
                s_DummySkinPos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16) { name = "DummySkinPos" };
            if (s_DummySkinRot == null)
                s_DummySkinRot = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16) { name = "DummySkinRot" };
            Shader.SetGlobalInteger("_SplatSkinningActive", 0);
            Shader.SetGlobalBuffer("_SplatSkinPos", s_DummySkinPos);
            Shader.SetGlobalBuffer("_SplatSkinRot", s_DummySkinRot);
        }

        internal static void ResetRenderStatics()
        {
            DisposeBuffer(ref s_DummySkinPos);
            DisposeBuffer(ref s_DummySkinRot);
            Shader.SetGlobalInteger("_SplatSkinningActive", 0);
            Shader.SetGlobalBuffer("_SplatSkinPos", (GraphicsBuffer)null);
            Shader.SetGlobalBuffer("_SplatSkinRot", (GraphicsBuffer)null);

            GlobalScreenSizeCull = 0.0f;
            SplatRenderScale = 1.0f;
            SplatDepthOcclusion = true;
            s_Sort16BitKeys = true;
            s_ForceGLESPath = false;
            s_ForceVertexTextureFetch = false;
            s_VertexTextureFetch = -1;
            s_ForceNoComputePath = false;
            s_NoComputePath = -1;
            s_RenderPathLogged = false;
            s_SortPathLogged = false;
            DebugFreezeCompute = false;
            s_ProbedUtilities = null;       // drop the reference and re-probe on the next enable
            s_ProbedUtilitiesUsable = false;
        }

        /// <summary>
        /// Release every native/GPU resource this renderer type owns and reset its statics, so the
        /// assembly can be safely unloaded. Call on scene unload, on returning to a menu, or before
        /// a hot assembly update.
        /// </summary>
        internal static void ReleaseAll()
        {
            Released = true;
            GaussianSplatRenderSystem.ReleaseAll();
            ResetRenderStatics();
        }

        // ---------------------------------------------------------------------------------------
        // Deprecated names, kept so existing integrations keep compiling. Delete once callers have
        // moved. (The Viggle.SplatEngine deprecation surface lives in Runtime/SplatObsoleteApi.cs;
        // these are here because GaussianSplatting is a separate assembly.)
        // ---------------------------------------------------------------------------------------

        [System.Obsolete("Renamed to SplatRenderer.ReleaseAll().")]
        internal static void ReleaseForHotReload() => ReleaseAll();

        [System.Obsolete("Renamed to SplatRenderer.ResetRenderStatics().")]
        internal static void ReleaseStaticsForHotReload() => ResetRenderStatics();

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();
            m_SorterArgs.resources.Dispose();

            EnsureSorterAndRegister();

            // The bitonic fallback sorts in place over a power-of-two sized buffer; pad the
            // buffers and fill the key tail with 0xFFFFFFFF so unused entries sort to the back
            // (CSCalcDistances only ever writes the first `count` keys, so the tail persists).
            bool noRadix = m_Sorter == null || !m_Sorter.Valid;
            bool useCounting = noRadix && m_CountingSorter != null && m_CountingSorter.Valid && s_Sort16BitKeys;
            // Only the bitonic network needs a power-of-two region; the counting sort works on the
            // exact count, which halves these buffers whenever splatCount sits just over a power of
            // two (67101 -> 67101 instead of 131072).
            bool useBitonic = noRadix && !useCounting && m_BitonicSorter != null && m_BitonicSorter.Valid;
            int bufferCount = useBitonic ? GpuBitonicSort.PaddedCount(count) : count;
            m_CountingRes.Dispose();
            if (useCounting)
                m_CountingRes = GpuCountingSort.Resources.Load(count);
            if (!s_SortPathLogged)
            {
                s_SortPathLogged = true;
                // Which sort path is active tells us whether the padded-tail OOB (guarded by the clamp
                // in RenderGaussianSplats.shader) is even reachable: bitonic pads the order buffer past
                // splatCount, radix sizes it exactly. Logged as a warning so it survives "warnings only"
                // logcat filters on a remote tester's device.
                if (s_LogDiagnostics) Debug.Log($"[SplatSort DIAG] path={(useBitonic ? "bitonic(padded)" : useCounting ? "counting(exact)" : "radix(exact)")} " +
                                 $"splatCount={count} bufferCount={bufferCount}");
            }

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCount, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCount, 4) { name = "GaussianSplatSortIndices" };
            if (useBitonic)
            {
                var sentinel = new uint[bufferCount];
                for (int i = 0; i < bufferCount; ++i)
                    sentinel[i] = 0xFFFFFFFF;
                m_GpuSortDistances.SetData(sentinel);
            }

            // init keys buffer to splat indices (whole buffer, so the padded tail holds valid indices too)
            splatUtilities.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            splatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            splatUtilities.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
        }

        // On the no-compute path the compute half of this test is not just unnecessary, it is
        // guaranteed to fail - that is what selected the path. Require the no-compute shader pair
        // instead. The debug shaders are still checked because these are serialized asset
        // REFERENCES, which exist on every platform; whether their programs are supported by the
        // GPU is a separate matter, and they are only ever bound by debug views.
        bool resourcesAreSetUp => UseNoComputePath
            ? shaderSplatsNoCompute != null && shaderCompositeNoCompute != null
            : shaderSplats != null && shaderComposite != null && shaderDebugPoints != null &&
              shaderDebugBoxes != null && splatUtilities != null &&
              SystemInfo.supportsComputeShaders && ComputeKernelsUsable(splatUtilities);

        // Probe result, keyed on the compute shader instance: resourcesAreSetUp is read every frame
        // and the probe is a native call. Statics reset on domain reload, so fixing the shader and
        // re-entering play mode re-probes.
        static ComputeShader s_ProbedUtilities;
        static bool s_ProbedUtilitiesUsable;

        // A non-null ComputeShader says nothing about whether it can run. Unity keeps the kernel
        // names from the #pragma kernel lines even when the shader fails to compile, so the asset
        // loads and reports its full kernel count while every kernel is unusable. Dispatching then
        // throws IndexOutOfRangeException ("Invalid kernelIndex (0) passed, must be non-negative
        // less than 29") out of InitSortBuffers, again out of SortPoints, on every frame, plus a
        // Render Graph execution error - which buries the one console line naming the actual shader
        // error. Probe once so resourcesAreSetUp reports it through the existing diagnostic instead.
        static bool ComputeKernelsUsable(ComputeShader cs)
        {
            if (ReferenceEquals(cs, s_ProbedUtilities))
                return s_ProbedUtilitiesUsable;
            s_ProbedUtilities = cs;
            s_ProbedUtilitiesUsable = false;
            try
            {
                // The three kernels every render path needs. Probed with GetKernelThreadGroupSizes
                // because that is the call that actually throws on an uncompiled kernel; IsSupported
                // answers a different question (device capability for a kernel that did compile), so
                // it is checked too but cannot stand alone.
                cs.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint sx, out _, out _);
                cs.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint dx, out _, out _);
                cs.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint vx, out _, out _);
                s_ProbedUtilitiesUsable = sx > 0 && dx > 0 && vx > 0 &&
                                          cs.IsSupported((int)KernelIndices.SetIndices) &&
                                          cs.IsSupported((int)KernelIndices.CalcDistances) &&
                                          cs.IsSupported((int)KernelIndices.CalcViewData);
            }
            catch (Exception)
            {
                s_ProbedUtilitiesUsable = false;
            }
            return s_ProbedUtilitiesUsable;
        }

        // Relight-texture row order override: -1 = derive from the projection matrix, 0 = never
        // mirror uv.y, 1 = always mirror. For platforms where the auto formula mismatches how
        // the render graph stores imported textures (diagnose with Relight Debug Splat View -
        // orange means inverted).
        public static int s_RelightFlipYOverride = -1;

        // Debug aid: force the GLES fallback path (bitonic sort, inverted keys, over-blend)
        // on any platform, to reproduce device behavior in the editor.
        internal static bool s_ForceGLESPath = false;

        // Debug aid: force the vertex-texture-fetch render path on any platform (e.g. to
        // reproduce/validate it in the editor where vertex SSBOs normally work).
        internal static bool s_ForceVertexTextureFetch = false;

        // Debug aid: force the no-compute render path on a platform that does have compute, to
        // validate the WebGL2 path in the editor. Must be set before any renderer is enabled -
        // the path decides which GPU resources get created, and that happens once per asset.
        internal static bool s_ForceNoComputePath = false;
        static int s_NoComputePath = -1; // -1 = not yet probed, 0 = compute available, 1 = no compute

        // True on platforms with no compute shaders at all (WebGL2 / GLES 3.0). There the entire
        // compute half of this renderer is unavailable - view-data projection (CSCalcViewData),
        // the depth sort (SplatUnifiedSort) and GPU skinning (SplatSkinning.compute) - so the
        // renderer instead feeds RenderGaussianSplatsNoCompute.shader from CpuSplatSorter plus
        // the baked SplatNoComputeData textures, and projects in the vertex stage.
        //
        // Distinct from the supportsComputeShaders test in resourcesAreSetUp, which is a guard
        // that refuses to draw. This one selects an alternative that does.
        internal static bool UseNoComputePath
        {
            get
            {
                if (s_ForceNoComputePath)
                    return true;
                if (s_NoComputePath < 0)
                    s_NoComputePath = SystemInfo.supportsComputeShaders ? 0 : 1;
                return s_NoComputePath == 1;
            }
        }

        // Keyword that switches RenderGaussianSplats.shader to read view data from a texture
        // instead of StructuredBuffers in the vertex stage. Must match the shader's #pragma.
        internal const string kVertexTextureFetchKeyword = "SPLAT_VERTEX_TEXTURE_FETCH";

        // Public surface for effects (e.g. the outline sample) that render their own splat-based
        // material and must mirror the renderer's vertex-stage data path. Enable this keyword on
        // such a material when it is true, exactly like the splat material does.
        internal static string VertexTextureFetchKeyword => kVertexTextureFetchKeyword;
        internal static bool RequiresVertexTextureFetch => UseVertexTextureFetch;

        // RenderGaussianSplats.shader needs 3 StructuredBuffers in the vertex stage
        // (_OrderBuffer, _SplatViewData, _SplatSelectedBits). Mali and some other mobile GLES
        // drivers report GL_MAX_VERTEX_SHADER_STORAGE_BLOCKS == 0, so the buffer variant fails
        // to link and nothing renders. maxComputeBufferInputsVertex exposes that limit.
        const int kRenderVertexBufferCount = 3;
        static int s_VertexTextureFetch = -1; // -1 = not yet probed, 0 = no, 1 = yes

        // True when the vertex stage cannot bind the StructuredBuffers the splat shader needs,
        // so we must feed it view data via a texture instead. Probed once from SystemInfo.
        internal static bool UseVertexTextureFetch
        {
            get
            {
                // The no-compute path implies this one: GLES 3.0 exposes no shader storage
                // buffers in any stage, so its vertex shader must read textures regardless of
                // what maxComputeBufferInputsVertex reports.
                if (s_ForceVertexTextureFetch || UseNoComputePath)
                    return true;
                if (s_VertexTextureFetch < 0)
                    s_VertexTextureFetch = SystemInfo.maxComputeBufferInputsVertex < kRenderVertexBufferCount ? 1 : 0;
                return s_VertexTextureFetch == 1;
            }
        }

        internal const int kViewTexWidth = 2048; // power-of-two row width for the packed view-data texture
        static bool s_RenderPathLogged;
        static bool s_SortPathLogged;
        static bool s_SilhouetteUnifiedLogged;

        // Debug passthrough: lets other assemblies (e.g. SkinnedSplatRenderer) toggle the
        // freeze-compute-after-warmup test from a player build, since GaussianSplatRenderSystem
        // is internal. See GaussianSplatRenderSystem.s_FreezeComputeAfterWarmup.
        internal static bool DebugFreezeCompute
        {
            get => GaussianSplatRenderSystem.s_FreezeComputeAfterWarmup;
            set => GaussianSplatRenderSystem.s_FreezeComputeAfterWarmup = value;
        }

        // Render back-to-front with premultiplied "over" blending (One OneMinusSrcAlpha,
        // hardcoded in RenderGaussianSplats.shader) and inverted sort keys (far→near).
        // The dst-alpha front-to-back "under" blend mis-accumulates on OpenGL ES/ANGLE,
        // so use the same back-to-front path everywhere (it's order-correct on all backends).
        internal static bool useBackToFrontBlend => true;

        internal void EnsureMaterials()
        {
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                if (UseNoComputePath)
                {
                    // The no-compute pair carries no keywords: it has one variant by design, so
                    // there is nothing to enable and nothing a driver can fail to link. The debug
                    // materials are deliberately left null - both their shaders declare
                    // "#pragma require compute" and read StructuredBuffers, so they cannot load
                    // here; the debug views that use them are compute-path/editor features.
                    m_MatSplats = new Material(shaderSplatsNoCompute) {name = "GaussianSplatsNoCompute"};
                    m_MatComposite = new Material(shaderCompositeNoCompute) {name = "GaussianClearDstAlphaNoCompute"};
                }
                else
                {
                    m_MatSplats = new Material(shaderSplats) {name = "GaussianSplats"};
                    if (UseVertexTextureFetch)
                        m_MatSplats.EnableKeyword(kVertexTextureFetchKeyword);
                    m_MatComposite = new Material(shaderComposite) {name = "GaussianClearDstAlpha"};
                    m_MatDebugPoints = new Material(shaderDebugPoints) {name = "GaussianDebugPoints"};
                    m_MatDebugBoxes = new Material(shaderDebugBoxes) {name = "GaussianDebugBoxes"};
                }
            }
        }

        // Draws this character's splats with a caller-supplied material into the currently-bound
        // render target, reusing the per-splat view data already computed for this camera by the
        // main splat pass (so call it after the splats have rendered this frame). The material's
        // vertex stage must read the same view data as RenderGaussianSplats.shader - see the
        // GaussianSplatOutlineMask sample shader. Used by effects (e.g. the outline sample) to
        // render a silhouette/mask without depending on the renderer's internals.
        //
        // Bind any material-specific uniforms (e.g. an outline color) on `mpb` before calling.
        // Returns false if this renderer is not ready to draw.
        internal bool RenderSilhouette(CommandBuffer cmb, Camera cam, Material maskMat, MaterialPropertyBlock mpb)
        {
            if (maskMat == null || mpb == null || !HasValidRenderSetup || m_GpuIndexBuffer == null)
                return false;
            // Effect materials mirror RenderGaussianSplats.shader's vertex-stage data path, and on
            // the no-compute path there is no such data to hand them - no view buffer, no order
            // buffer, and a caller's shader will not have the no-compute variant's texture inputs.
            // Refuse, like the unified-sorting case below, so the effect skips this renderer.
            if (UseNoComputePath)
                return false;
            // Unified sorting relocates the view data into a buffer shared by every splat object,
            // and the per-object order buffer that indexes it is not maintained there - so a
            // per-object silhouette pass has nothing consistent to read. Refuse rather than draw
            // garbage; the caller (an effect) skips this renderer.
            if (m_UnifiedView != null)
            {
                if (!s_SilhouetteUnifiedLogged)
                {
                    s_SilhouetteUnifiedLogged = true;
                    Debug.LogWarning("SplatRenderer: per-object silhouette/outline effects are not " +
                                     "available while unified splat sorting is enabled on the URP renderer.", this);
                }
                return false;
            }
            if (m_GpuView == null)
                return false;

            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            var screenPar = new Vector4(eyeW != 0 ? eyeW : cam.pixelWidth, eyeH != 0 ? eyeH : cam.pixelHeight, 0, 0);

            // Mirror SortAndRenderSplats' bindings: SSBOs go on the material instance too, since
            // per-draw MaterialPropertyBlock buffer bindings are unreliable on OpenGL ES.
            if (UseVertexTextureFetch && m_GpuViewTex != null)
            {
                mpb.SetTexture(Props.SplatViewDataTex, m_GpuViewTex);
                mpb.SetInteger(Props.SplatViewTexWidth, m_GpuViewTex.width);
                maskMat.SetTexture(Props.SplatViewDataTex, m_GpuViewTex);
                maskMat.SetInteger(Props.SplatViewTexWidth, m_GpuViewTex.width);
            }
            mpb.SetBuffer(Props.SplatViewData, m_GpuView);
            mpb.SetBuffer(Props.OrderBuffer, m_GpuSortKeys);
            mpb.SetVector(Props.VecScreenParams, screenPar);
            maskMat.SetBuffer(Props.SplatViewData, m_GpuView);
            maskMat.SetBuffer(Props.OrderBuffer, m_GpuSortKeys);
            // Same bounds-clamp lengths the main draw supplies; the mask shader indexes the same
            // two buffers and carried the same broken GetDimensions guard.
            mpb.SetInteger(Props.OrderBufferCount, m_GpuSortKeys.count);
            mpb.SetInteger(Props.SplatViewDataCount, m_GpuView.count);
            maskMat.SetInteger(Props.OrderBufferCount, m_GpuSortKeys.count);
            maskMat.SetInteger(Props.SplatViewDataCount, m_GpuView.count);
            maskMat.SetVector(Props.VecScreenParams, screenPar);

            // ProcCount, NOT splatCount: this must match what the main pass drew and what
            // CalcViewData actually computed this frame. Drawing the full count while the renderer
            // is LODded to a top-K prefix made the mask read view entries that this frame's passes
            // never wrote, so the silhouette was built from stale (and, before the buffer was
            // guaranteed initialized, undefined) projections - garbage quads in the outline.
            cmb.DrawProcedural(m_GpuIndexBuffer, transform.localToWorldMatrix, maskMat, 0,
                MeshTopology.Triangles, 6, ProcCount, mpb);
            return true;
        }

        internal void EnsureSorterAndRegister()
        {
            // Every sorter here is a compute shader, and CpuSplatSorter has already replaced all
            // three on this path. Constructing them anyway just probes kernels that cannot exist
            // and logs "kernels not found or not supported" once per renderer.
            if (m_Sorter == null && resourcesAreSetUp && !UseNoComputePath)
            {
                // Sort kernels live in their own compute shader (wave-op requirements are
                // file-scoped; see DeviceRadixSort.compute). Fall back to splatUtilities
                // for setups serialized before the split - GpuSorting logs if kernels are absent.
                m_Sorter = new GpuSorting(s_ForceGLESPath ? null : deviceRadixSort != null ? deviceRadixSort : splatUtilities);
                if (!m_Sorter.Valid)
                {
                    // No wave intrinsics (e.g. OpenGL ES). Prefer the counting sort; keep bitonic
                    // for the 32-bit-key case, where a bounded bin space does not exist.
                    m_CountingSorter = new GpuCountingSort(splatUtilities);
                    m_BitonicSorter = new GpuBitonicSort(splatUtilities);
                    if (m_CountingSorter.Valid && s_Sort16BitKeys)
                        if (s_LogDiagnostics) Debug.Log($"SplatRenderer '{name}': radix sort unavailable, using counting sort", this);
                    else if (m_BitonicSorter.Valid)
                        if (s_LogDiagnostics) Debug.Log($"SplatRenderer '{name}': radix sort unavailable, using bitonic sort fallback", this);
                }
            }

            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        // ------------------------------------------------------------------------------------
        // Unity messages. All `internal virtual` so SkinnedSplatRenderer can chain them: a Unity
        // message is NOT virtual by nature, so a derived type declaring its own OnEnable would
        // silently stop this one from ever running. Internal rather than protected keeps them off
        // the public surface; the friend assembly (see AssemblyInfo) can still override them.
        // ------------------------------------------------------------------------------------

        internal virtual void OnEnable()
        {
            if (ShouldRender) TrySetup();
        }

        internal virtual void OnDisable() => Teardown();

        internal virtual void Update()
        {
            // Reconcile edit-mode rendering with the project setting: covers toggling
            // renderInEditor and play/edit transitions.
            if (ShouldRender && !m_SetupDone) { TrySetup(); return; }
            if (!ShouldRender && m_SetupDone) { Teardown(); return; }

            // The file field was swapped from the inspector or from code. Reloading here rather
            // than in OnValidate is deliberate: OnValidate cannot create GameObjects or components,
            // which a subclass's Setup does. Both are null on a character, so this is inert there.
            if (m_SetupDone && m_LoadedFrom != m_SplatFile)
            {
                Teardown();
                if (m_SplatFile != null && ShouldRender) TrySetup();
                return;
            }

            // The packed asset itself was swapped (assigned directly, or rebuilt by a subclass).
            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                }
                else if (m_Asset != null)
                {
                    Debug.LogError($"{nameof(SplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }
        }

        /// <summary>
        /// Builds the GPU resources for the asset currently assigned. This is what enabling the
        /// component used to do; a subclass calls it directly after wiring up an asset of its own,
        /// because toggling `enabled` to force a rebuild would now re-enter this component's own
        /// OnEnable and Setup.
        /// </summary>
        internal void CreateRenderResources()
        {
            m_FrameCounter = 0;
            m_HasSortedOnce = false;  // fresh/rebuilt buffers must be sorted at least once (static-skip)
            m_LastCullTestFrame = -1; // forget visibility from a previous enable (IsRecentlyVisible)
            m_LastVisibleFrame = -1;
            if (!resourcesAreSetUp)
            {
                // No asset means we're not expected to render yet (e.g. OnEnable fired from
                // AddComponent, before the caller assigns resources) - stay quiet then.
                if (m_Asset != null)
                {
                    // Distinguish a missing asset reference from one that loaded but whose kernels
                    // did not compile: the fixes are completely different, and only the second one
                    // leaves a shader error further up the console.
                    bool csAssigned = splatUtilities != null;
                    bool csUsable = csAssigned && ComputeKernelsUsable(splatUtilities);
                    Debug.LogError($"SplatRenderer '{name}': cannot render. " +
                               $"shaderSplats={(shaderSplats ? "ok" : "MISSING")} shaderComposite={(shaderComposite ? "ok" : "MISSING")} " +
                               $"shaderDebugPoints={(shaderDebugPoints ? "ok" : "MISSING")} shaderDebugBoxes={(shaderDebugBoxes ? "ok" : "MISSING")} " +
                               $"csSplatUtilities={(!csAssigned ? "MISSING" : csUsable ? "ok" : "KERNELS UNUSABLE")} " +
                               $"supportsComputeShaders={SystemInfo.supportsComputeShaders} " +
                               $"({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName})" +
                               (csAssigned && !csUsable
                                   ? ". SplatUtilities.compute loaded but none of its kernels compiled for this " +
                                     "platform - look further up the console for the shader compile error, which is " +
                                     "the actual problem. Reimporting the shader reports it again."
                                   : ""), this);
                }
                return;
            }

            EnsureMaterials();
            EnsureSorterAndRegister();

            CreateResourcesForAsset();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = splatUtilities;
            int kernelIndex = (int) kernel;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, ViewBuffer);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            // Full buffer length for the index clamps. Separate from _SplatCount, which callers
            // overwrite per dispatch with the LOD prefix (see the uniform's note in the shader).
            cmb.SetComputeIntParam(cs, Props.SplatCountFull, m_GpuView != null ? m_GpuView.count : m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);

            // Skinning override: global buffers don't propagate via CommandBuffer, bind explicitly
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSkinPos, m_SkinPosOverride ?? s_DummySkinPos);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSkinRot, m_SkinRotOverride ?? s_DummySkinRot);
            cmb.SetComputeIntParam(cs, Props.SplatSkinningActive, m_SkinningActive);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            // Skinning override
            mat.SetBuffer(Props.SplatSkinPos, m_SkinPosOverride ?? s_DummySkinPos);
            mat.SetBuffer(Props.SplatSkinRot, m_SkinRotOverride ?? s_DummySkinRot);
            mat.SetInteger(Props.SplatSkinningActive, m_SkinningActive);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void ReleaseViewTexture()
        {
            if (m_GpuViewTex == null)
                return;
            m_GpuViewTex.Release();
            DestroyImmediate(m_GpuViewTex);
            m_GpuViewTex = null;
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuView);
            // Not owned here: the render system allocated it and hands out slices (unified sorting).
            m_UnifiedView = null;
            m_ViewBase = -1;
            m_ViewLayoutCount = 0;
            ReleaseViewTexture();
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuVisibleCounter);
            DisposeBuffer(ref m_GpuDrawArgs);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            // No-compute path resources. Null on every other path, and Dispose is only reached
            // through these null checks, so this costs nothing when compute is available.
            m_NoComputeData?.Dispose();
            m_NoComputeData = null;
            m_CpuSorter?.Dispose();
            m_CpuSorter = null;
            // Give up the slice of the shared view textures too. Not cosmetic: a renderer that
            // came back with the SAME splat count would otherwise still look assigned, and reuse a
            // base that the relayout its disappearance triggered has since handed to someone else.
            m_NCViewBase = -1;
            m_NCViewLayoutCount = 0;

            m_SorterArgs.resources.Dispose();
            m_CountingRes.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        /// <summary>
        /// Releases the GPU resources built by <see cref="CreateRenderResources"/> and unregisters
        /// from the render system. The other half of the old enabled-toggle.
        /// </summary>
        internal void ReleaseRenderResources()
        {
            DisposeResourcesForAsset();
            GaussianSplatRenderSystem.TryUnregisterSplat(this);
            m_Registered = false;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            // calculate view dependent data for each splat
            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);

            // Pass camera matrices explicitly: the UNITY_MATRIX_VP/P built-in globals are not
            // reliably populated for compute dispatches on all backends (notably OpenGL ES).
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(splatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(splatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(splatUtilities, Props.SplatScale, splatScale);
            cmb.SetComputeFloatParam(splatUtilities, Props.SplatOpacityScale, opacityScale);
            cmb.SetComputeFloatParam(splatUtilities, Props.SplatScreenSizeCull, GlobalScreenSizeCull);
            cmb.SetComputeIntParam(splatUtilities, Props.SHOrder, SHOrderEffective);
            cmb.SetComputeIntParam(splatUtilities, Props.SHOnly, shOnly ? 1 : 0);
            // Per-splat color hook uniforms (ModifySplatColor in GaussianSplatColorModifier.hlsl).
            // Strength is a constant 1: the shader does lerp(c, c*tint, strength), which at 1 is
            // exactly c * tint, so a white baseColor is already the identity and a separate
            // strength knob would only be a second way to spell the same multiply.
            cmb.SetComputeVectorParam(splatUtilities, Props.GsTintColor, baseColor.linear);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsTintStrength, 1f);
            cmb.SetComputeVectorParam(splatUtilities, Props.GsFresnelColor, m_FresnelColor.linear);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsFresnelStrength, m_FresnelStrength);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsFresnelPower, m_FresnelPower);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsFresnelThreshold, m_FresnelThreshold);
            // Occlusion reveal (GaussianSplatOcclusionReveal.hlsl): global focus/shape,
            // gated per renderer - a non-participating renderer uploads strength 0.
            cmb.SetComputeVectorParam(splatUtilities, Props.GsRevealPos, SplatOcclusionReveal.PosStrength(m_OcclusionReveal));
            cmb.SetComputeVectorParam(splatUtilities, Props.GsRevealCam, camPos);
            cmb.SetComputeVectorParam(splatUtilities, Props.GsRevealShape, SplatOcclusionReveal.Shape);
            // Relight (see SplatRelightingFeature). The texture is bound unconditionally: an
            // unbound Texture2D in a compute kernel reads driver garbage rather than zero on
            // several backends, and the strength check that skips the sample lives in the shader,
            // which cannot un-bind it. Black reads as "no light", which the w == 0 flag in
            // _GsRelightParams makes the hook ignore anyway.
            var relightTex = m_RelightTexture != null ? m_RelightTexture : (Texture)Texture2D.blackTexture;
            cmb.SetComputeTextureParam(splatUtilities, (int)KernelIndices.CalcViewData, Props.GsRelightTex, relightTex);
            cmb.SetComputeVectorParam(splatUtilities, Props.GsRelightParams, m_RelightTexture != null ? m_RelightParams : Vector4.zero);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsRelightStrength, m_RelightStrength);
            cmb.SetComputeVectorParam(splatUtilities, Props.GsRelightTint, m_RelightTint.linear);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsRelightAmbient, m_RelightAmbient);
            // Whether the relight-texture row order is mirrored relative to the NDC the splats
            // compute. Measured, not derived: on Metal (m11 < 0) the render graph stores the
            // imported relight texture with the OPPOSITE row order to the NDC this kernel
            // computes, so the mirror is needed exactly when the projection is flipped - the
            // reverse of the covariance-axes intuition. s_RelightFlipYOverride pins the answer
            // on platforms where the rule is wrong (diagnose with SplatRelight's SplatSample
            // debug view: orange means inverted).
            float relightFlip = s_RelightFlipYOverride >= 0
                ? s_RelightFlipYOverride
                : (matProj.m11 < 0 ? 1f : 0f);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsRelightFlipY, relightFlip);
            cmb.SetComputeFloatParam(splatUtilities, Props.GsRelightDebug, m_RelightDebugSplatView ? 1f : 0f);

            // Distance LOD: only compute view data for the top-K processed splats. Overrides the
            // _SplatCount that SetAssetDataOnCS set to the full count, and sizes the dispatch to match.
            int viewCount = ProcCount;
            // First dispatch after the buffer was (re)created must cover the FULL splat count, not
            // just the LOD prefix: a fresh GraphicsBuffer holds undefined memory, and anything that
            // reads an entry this pass never wrote - the LOD tail [ProcCount..splatCount), an effect
            // drawing its own instance count (RenderSilhouette), the static-scene view skip - would
            // otherwise interpret driver garbage as clip positions and ellipse axes. That is NaN or
            // huge values, i.e. the garbage full-screen quads in the client's 花屏 report. One extra
            // full-count dispatch per asset load; after that the LOD prefix is enough because every
            // entry now holds a real (if stale) projection, and stale-but-valid merely means a splat
            // is a frame late rather than a corrupt primitive.
            if (!m_ViewInitialized)
            {
                viewCount = m_SplatCount;
                m_ViewInitialized = true;
            }
            cmb.SetComputeIntParam(splatUtilities, Props.SplatCount, viewCount);
            // 0 in the normal per-object mode: the kernel writes _SplatViewData[_ViewBase + idx],
            // which is then exactly the write it has always done. Under unified sorting this is
            // where this renderer's slice of the shared buffer starts. Always set, never left to
            // whatever a previous dispatch happened to leave in the uniform.
            cmb.SetComputeIntParam(splatUtilities, Props.ViewBase, m_UnifiedView != null ? m_ViewBase : 0);
            splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(splatUtilities, (int)KernelIndices.CalcViewData, (viewCount + (int)gsX - 1)/(int)gsX, 1, 1);

            // Vertex-texture-fetch path: pack the just-computed view data (in draw order, via the
            // sort keys) into m_GpuViewTex so the render vertex shader can sample it without
            // StructuredBuffers. Runs right after CalcViewData and after this frame's sort, so
            // both inputs are current. Unity inserts the compute->graphics barrier before the draw.
            if (UseVertexTextureFetch && m_GpuViewTex != null)
            {
                int bvt = (int)KernelIndices.BuildViewTex;
                cmb.SetComputeBufferParam(splatUtilities, bvt, Props.SplatSortKeys, m_GpuSortKeys);
                cmb.SetComputeBufferParam(splatUtilities, bvt, Props.SplatViewData, ViewBuffer);
                cmb.SetComputeTextureParam(splatUtilities, bvt, Props.SplatViewDataTex, m_GpuViewTex);
                cmb.SetComputeIntParam(splatUtilities, Props.SplatCount, viewCount); // LOD: pack only the processed prefix
                cmb.SetComputeIntParam(splatUtilities, Props.SplatViewTexWidth, m_GpuViewTex.width);
                splatUtilities.GetKernelThreadGroupSizes(bvt, out uint bvtX, out _, out _);
                cmb.DispatchCompute(splatUtilities, bvt, (viewCount + (int)bvtX - 1)/(int)bvtX, 1, 1);
            }
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // Distance LOD: process only the top-K importance splats this frame (K = ProcCount).
            bool lod = LodDynamic;
            int procCount = ProcCount;
            // Which sorter runs this frame, and (bitonic only) the power-of-two region a LOD sort
            // covers: the smallest power of two >= K, capped at the padded buffer. The tail
            // [K..bitonicSortCount) is sentinel-filled below so the prefix sorts correctly.
            bool useRadix = m_Sorter != null && m_Sorter.Valid && m_SorterArgs.resources.altBuffer != null;
            bool useCounting = !useRadix && m_CountingSorter != null && m_CountingSorter.Valid
                               && s_Sort16BitKeys && m_CountingRes.IsCreated;
            bool useBitonic = !useRadix && !useCounting && m_BitonicSorter != null && m_BitonicSorter.Valid;
            int bitonicSortCount = useBitonic
                ? Mathf.Min(GpuBitonicSort.PaddedCount(procCount), m_GpuSortKeys.count)
                : 0;

            // Screen-size-cull compaction: when enabled, CSCalcDistances parks culled splats at the
            // back of the sort and tallies the survivors, so the draw can go indirect over just the
            // visible prefix. Clear the tally first; publish it into the draw args after the sort.
            // Off under unified sorting: the draw there is one merged draw over every object, whose
            // instance count cannot come from any single object's tally. The screen-size cull itself
            // still runs in CSCalcViewData (so the fill-rate saving is kept); only the vertex-side
            // compaction is lost, and the culled splats are collapsed by the vertex shader as usual.
            bool compact = GlobalScreenSizeCull > 0f && m_GpuDrawArgs != null && m_GpuVisibleCounter != null
                           && !UsesUnifiedView;

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            // Rebuild the identity index prefix so the sort operates on the right splats. Under LOD we
            // reseed the region the sort will run over - the top-K importance splats (stored
            // importance-descending); on the bitonic path that region is the power-of-two
            // bitonicSortCount (its [K..pow2) tail gets sentinel distances below, so those slots sort
            // to the back and are never drawn). Reseeding happens each LOD frame (last frame's sort
            // permuted the prefix). Whatever lies beyond the reseeded region is stale, so the buffer
            // is no longer a valid full [0..N) permutation; m_SortKeysPartial records that. When we
            // return to a full-count sort (LOD turned off) we MUST reseed [0..N) first - otherwise
            // the full sort runs over a buffer with duplicate/missing indices and draws holes + double
            // splats (which never self-heals). Non-LOD steady state reseeds nothing (temporal coherence).
            int lodSortCount = useBitonic ? bitonicSortCount : procCount;
            int reindexCount = lod ? lodSortCount : (m_SortKeysPartial ? m_SplatCount : 0);
            if (reindexCount > 0)
            {
                cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
                cmd.SetComputeIntParam(splatUtilities, Props.SplatCount, reindexCount);
                splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint siX, out _, out _);
                cmd.DispatchCompute(splatUtilities, (int)KernelIndices.SetIndices, (reindexCount + (int)siX - 1)/(int)siX, 1, 1);
                m_SortKeysPartial = lod && reindexCount < m_SplatCount;
            }
            if (compact)
            {
                cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.ClearVisibleCounter, Props.VisibleCounter, m_GpuVisibleCounter);
                cmd.DispatchCompute(splatUtilities, (int)KernelIndices.ClearVisibleCounter, 1, 1, 1);
            }
            // Bound every frame (not just when compacting) so the kernel's declared buffers always
            // have a binding; the shader only reads them when _CullToBack != 0.
            cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CalcDistances, Props.SplatViewData, ViewBuffer);
            if (m_GpuVisibleCounter != null)
                cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CalcDistances, Props.VisibleCounter, m_GpuVisibleCounter);
            cmd.SetComputeIntParam(splatUtilities, Props.CullToBack, compact ? 1 : 0);
            cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(splatUtilities, Props.SortKeysInvert, useBackToFrontBlend ? 1 : 0);
            // 16-bit key quantization (see s_Sort16BitKeys): camera-space depth range of this
            // renderer's bounds, conservative the same way as the frustum cull (yaw-agnostic in
            // x/z for skinned characters; out-of-range depths saturate harmlessly).
            cmd.SetComputeIntParam(splatUtilities, Props.Sort16BitKeys, s_Sort16BitKeys ? 1 : 0);
            if (s_Sort16BitKeys)
            {
                Matrix4x4 mv = worldToCamMatrix * matrix;
                Vector3 bmin = m_Asset.boundsMin, bmax = m_Asset.boundsMax;
                Vector3 center = (bmin + bmax) * 0.5f;
                Vector3 ext = (bmax - bmin) * (0.5f * (1f + m_CullBoundsPad));
                ext.x = ext.z = Mathf.Max(ext.x, ext.z);
                float zc = mv.m20 * center.x + mv.m21 * center.y + mv.m22 * center.z + mv.m23;
                float ze = Mathf.Abs(mv.m20) * ext.x + Mathf.Abs(mv.m21) * ext.y + Mathf.Abs(mv.m22) * ext.z;
                float invRange = 1f / Mathf.Max(2f * ze, 1e-6f);
                cmd.SetComputeVectorParam(splatUtilities, Props.SortDepthParams,
                    new Vector4(zc - ze, invRange, 0, 0));
            }
            cmd.SetComputeIntParam(splatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            // Own uniform, NOT _MatrixMV: this depth-flipped matrix must not bleed into the
            // covariance pass's _MatrixMV on OpenGL ES/ANGLE (see _SortMatrixMV in the shader).
            cmd.SetComputeMatrixParam(splatUtilities, Props.SortMatrixMV, worldToCamMatrix * matrix);
            // Under LOD only the first procCount keys are meaningful; the bounds check and dispatch
            // both use it. Non-LOD keeps the full (possibly padded) buffer count, unchanged.
            cmd.SetComputeIntParam(splatUtilities, Props.SplatCount, lod ? procCount : m_SplatCount);
            // Full per-splat buffer length for CSCalcDistances' index clamp. Set here as well as in
            // SetAssetDataOnCS because the sort path does not go through it, and _SplatCount above
            // is the LOD prefix rather than the buffer length.
            cmd.SetComputeIntParam(splatUtilities, Props.SplatCountFull, m_GpuView != null ? m_GpuView.count : m_SplatCount);
            cmd.SetComputeIntParam(splatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            // Skinning override for CalcDistances (LoadSplatPos checks _SplatSkinningActive)
            cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSkinPos, m_SkinPosOverride ?? s_DummySkinPos);
            cmd.SetComputeIntParam(splatUtilities, Props.SplatSkinningActive, m_SkinningActive);
            int distCount = lod ? procCount : m_GpuSortDistances.count;
            splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(splatUtilities, (int)KernelIndices.CalcDistances, (distCount + (int)gsX - 1)/(int)gsX, 1, 1);

            // Bitonic LOD: sentinel-fill the distance tail [procCount..bitonicSortCount) so the
            // power-of-two prefix sort parks those slots past the drawn prefix.
            if (lod && useBitonic && bitonicSortCount > procCount)
            {
                cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.FillSortDistances, Props.SplatSortDistances, m_GpuSortDistances);
                cmd.SetComputeIntParam(splatUtilities, Props.SplatCount, procCount);
                cmd.SetComputeIntParam(splatUtilities, Props.BufferSize, bitonicSortCount);
                splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.FillSortDistances, out uint fsX, out _, out _);
                int fillCount = bitonicSortCount - procCount;
                cmd.DispatchCompute(splatUtilities, (int)KernelIndices.FillSortDistances, (fillCount + (int)fsX - 1)/(int)fsX, 1, 1);
            }

            // sort the splats (only the top-K prefix under LOD; resources are sized for the full count)
            EnsureSorterAndRegister();
            if (useRadix)
            {
                var sorterArgs = m_SorterArgs;
                if (lod)
                    sorterArgs.count = (uint)procCount;
                sorterArgs.keyBits = s_Sort16BitKeys ? 16u : 32u;
                m_Sorter.Dispatch(cmd, sorterArgs);
            }
            else if (useCounting)
            {
                // Exact prefix under LOD - no power-of-two rounding, so no sentinel tail to fill.
                m_CountingSorter.Dispatch(cmd, m_GpuSortDistances, m_GpuSortKeys,
                                          lod ? procCount : m_SplatCount, m_CountingRes);
            }
            else if (useBitonic)
            {
                if (lod)
                    m_BitonicSorter.Dispatch(cmd, m_GpuSortDistances, m_GpuSortKeys, bitonicSortCount);
                else
                    m_BitonicSorter.Dispatch(cmd, m_GpuSortDistances, m_GpuSortKeys);
            }
            else if (!m_SorterInvalidLogged)
            {
                m_SorterInvalidLogged = true;
                Debug.LogError($"SplatRenderer '{name}': GPU sorter unavailable (sorter={(m_Sorter == null ? "null" : m_Sorter.Valid ? "valid" : "INVALID")}, " +
                               $"resources={(m_SorterArgs.resources.altBuffer != null ? "ok" : "MISSING")}); skipping splat sorting", this);
            }

            // Publish the visible tally into the indirect-draw args (after the sort has parked the
            // culled splats at the back, so instanceCount covers exactly the visible prefix).
            if (compact)
            {
                cmd.SetComputeIntParam(splatUtilities, Props.DrawIndexCount, 6);
                // Hard cap for the GPU-produced instanceCount: the vertex shader indexes
                // _OrderBuffer by SV_InstanceID, so the draw can never legitimately ask for more
                // instances than that buffer holds. Without this the count is whatever the on-device
                // tally produced, and nothing on the CPU can bound it.
                cmd.SetComputeIntParam(splatUtilities, Props.DrawMaxInstances, m_GpuSortKeys.count);
                cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.WriteDrawArgs, Props.VisibleCounter, m_GpuVisibleCounter);
                cmd.SetComputeBufferParam(splatUtilities, (int)KernelIndices.WriteDrawArgs, Props.DrawArgs, m_GpuDrawArgs);
                cmd.DispatchCompute(splatUtilities, (int)KernelIndices.WriteDrawArgs, 1, 1, 1);
            }
            cmd.EndSample(s_ProfSort);
        }

        internal void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            splatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            splatUtilities.SetInt(Props.BufferSize, buf.count);
            splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            splatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            splatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            splatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            splatUtilities.SetInt(Props.BufferSize, dst.count);
            splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            splatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        internal void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            splatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            splatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(splatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            splatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(splatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        internal void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        internal void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        internal void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        internal void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(splatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(splatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(splatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(splatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        internal void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(splatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        internal void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(splatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(splatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        internal void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(splatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(splatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        internal void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        internal void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(splatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        internal void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        internal void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(splatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        internal bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(splatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(splatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(splatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(splatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        internal void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > SplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.PosDataSize / asset.splatCount);
            int otherStride = (int)(asset.OtherDataSize / asset.splatCount);
            int shStride = (int) (asset.SHDataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = SplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = SplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        internal void EditCopySplatsInto(SplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        internal void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(splatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(splatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(splatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(splatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(splatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(splatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(splatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(splatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(splatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            splatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(splatUtilities, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        internal GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}
