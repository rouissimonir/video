// SPDX-License-Identifier: MIT
// Cross-object splat ordering for the no-compute (WebGL2 / GLES 3.0) render path: ONE depth
// order spanning every visible splat object, drawn as ONE draw. The counterpart of
// SplatUnifiedSort, which does the same job with compute and cannot run here.
//
// WHY IT WAS NOT POSSIBLE, AND WHAT CHANGED. RenderGaussianSplatsNoCompute.shader used to do the
// entire job in the vertex stage - skinning, covariance, projection - reading the object's own
// source textures through a MaterialPropertyBlock. There was no shared post-projection buffer,
// so one draw could not span two objects, and the best that could be done was to cut the
// contested depth interval into a fixed number of bands and draw each object's contiguous run
// per band. Within a band, object order was list order. At Vigmon scale that is not a subtle
// error: an environment capture is 65 tiled objects, the "contested" interval collapses to
// essentially the whole visible depth range, and a band ends up metres deep - which reads as
// hard seams between neighbouring chunks, not as noise.
//
// What was missing was CSCalcViewData. Pass 2 of the shader supplies it: one fragment per splat,
// projecting into a SHARED view-data texture set at a per-object offset, running the exact same
// projection the vertex stage used to (same file, same function - see
// GaussianSplatNoComputeSource.hlsl). Its output names no object, so from there on this is the
// compute path's design with the compute taken out:
//
//   1. project - N small draws, one per object, one fragment per splat, into the shared textures;
//   2. order   - one CPU counting sort over every visible splat's view-space depth, which needs
//                no GPU readback because that depth is already one dot product per splat
//                (CpuGlobalSplatSorter);
//   3. draw    - one DrawProcedural over the global order texture, per render-order group.
//
// Draw calls go from several hundred to a handful, the per-object CPU sorts stop running
// entirely, and compositing is correct wherever two objects overlap rather than to within a band.
//
// COST, honestly stated. A global order has to be rebuilt whenever ANYTHING moves - the old
// per-object sorts let a static environment skip its sort while a character animated inside it,
// and that no longer applies. This is the same trade SplatUnifiedSort makes on the compute path
// and for the same reason: one order over N splats has to be produced from N depths. The static
// skip is still honoured at the level it still means something - an object that did not move is
// not re-projected, and if nothing moved at all the previous order is reused untouched.
using System;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;
using GaussianSplatting.Runtime;

namespace Viggle.SplatEngine
{
    sealed class SplatNoComputeGlobalSort : IDisposable
    {
        internal static readonly ProfilerMarker s_ProfProject =
            new(ProfilerCategory.Render, "GaussianSplat.NoComputeProject", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfSort =
            new(ProfilerCategory.Render, "GaussianSplat.NoComputeGlobalSort");

        // Pass indices in RenderGaussianSplatsNoCompute.shader. Pass 0 is the per-object draw this
        // path replaces.
        const int kPassMerged = 1;
        const int kPassViewData = 2;

        // Row width of every per-splat texture on this path, source and shared alike
        // (SplatNoComputeData.kTexWidth). Sharing one width is what lets a single index -> texel
        // helper serve all of them in the shader.
        const int kTexWidth = SplatNoComputeData.kTexWidth;

        // Shrink hysteresis, same shape and same reason as SplatUnifiedSort: capacity only comes
        // back down after the smaller working set has held for a while, so objects streaming in
        // and out cannot thrash a multi-megabyte reallocation.
        const int kShrinkAfterFrames = 300;

        // ------------------------------------------------------------------------------------
        // Device support. Three render targets and a float one among them - GLES 3.0 guarantees
        // four draw buffers, and EXT_color_buffer_float is near-universal on WebGL2, but "near"
        // is not "always" and the fallback (the per-object path) is a real one.
        // ------------------------------------------------------------------------------------
        static int s_Supported = -1;
        static bool s_UnsupportedLogged;

        internal static bool DeviceSupported
        {
            get
            {
                if (s_Supported < 0)
                {
                    // FormatUsage, not GraphicsFormatUsage: the package floor is Unity 2022.3,
                    // where the newer name does not exist. Same enum, same question.
                    bool ok = SystemInfo.supportedRenderTargetCount >= 3
                              && SystemInfo.IsFormatSupported(kPosFormat, FormatUsage.Render)
                              && SystemInfo.IsFormatSupported(kHalfFormat, FormatUsage.Render);
                    s_Supported = ok ? 1 : 0;
                    if (!ok && !s_UnsupportedLogged)
                    {
                        s_UnsupportedLogged = true;
                        Debug.LogWarning("Cross-object splat sorting on the no-compute path needs 3 render " +
                            $"targets (this device reports {SystemInfo.supportedRenderTargetCount}) and " +
                            "renderable float/half formats; falling back to per-object ordering.");
                    }
                }
                return s_Supported == 1;
            }
        }

        // Clip-space centres need the full float: at room scale a half-precision clip position
        // moves the splat by a visible fraction of a pixel after the perspective divide. The axes
        // (pixels, and only ever a few of them) and the colour do not - the compute path stores
        // that colour as 4xFP16 for the same reason. 16 + 8 + 8 = 32 bytes a splat, against the
        // compute path's 48.
        const GraphicsFormat kPosFormat = GraphicsFormat.R32G32B32A32_SFloat;
        const GraphicsFormat kHalfFormat = GraphicsFormat.R16G16B16A16_SFloat;
        internal const int kViewBytesPerSplat = 16 + 8 + 8;

        RenderTexture m_ViewPos, m_ViewAxis, m_ViewCol;
        RenderTargetIdentifier[] m_Mrt;
        int m_Capacity;             // splats the shared textures and the sorter are sized for
        int m_OversizedFrames;
        bool m_NeedsClear;          // freshly allocated targets, not yet written by any pre-pass

        readonly CpuGlobalSplatSorter m_Sorter = new();
        readonly MaterialPropertyBlock m_Mpb = new();
        readonly List<(SplatRenderer, MaterialPropertyBlock)> m_ToProject = new();

        // Layout bookkeeping: what the current slice assignment was made for.
        int m_LayoutRenderers;
        long m_LayoutSplats;

        // Single-slot cache for "the order texture already holds what this draw needs". Correct to
        // miss, never correct to hit wrongly, so the key is deliberately over-specific.
        bool m_HasOrder;
        int m_OrderCamera;
        int m_OrderTotal;
        int m_OrderSignature;

        bool m_AllocFailedLogged;

        internal bool IsActive => m_ViewPos != null;

        // ------------------------------------------------------------------------------------
        // Activation
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Make sure the shared view textures exist and every registered renderer owns a slice of
        /// them. False means this path cannot run at all and the caller must use the per-object
        /// one.
        /// </summary>
        internal bool TryActivate(Dictionary<SplatRenderer, MaterialPropertyBlock> splats)
        {
            if (!DeviceSupported)
                return false;

            // Validate the current assignment against the registered set. Cheap (the set is small)
            // and self-correcting: a renderer that loaded, unloaded, or swapped its capture shows
            // up here as an unassigned slice or a changed count, with no registration hook to keep
            // in sync.
            int renderers = 0;
            long needed = 0;
            bool dirty = false;
            foreach (var gs in splats.Keys)
            {
                if (!Eligible(gs))
                    continue;
                ++renderers;
                needed += gs.splatCount;
                if (gs.m_NCViewBase < 0 || gs.m_NCViewLayoutCount != gs.splatCount)
                    dirty = true;
            }
            if (renderers == 0 || needed <= 0)
                return false;
            // Well before an int overflows, the allocation itself is hopeless. Refuse rather than
            // wrap around into a tiny texture.
            if (needed > 40_000_000L)
            {
                LogAllocFailure($"{needed:N0} splats is more than the shared view textures can hold");
                return false;
            }
            if (renderers != m_LayoutRenderers || needed != m_LayoutSplats)
                dirty = true;

            int need = (int)needed;
            m_OversizedFrames = m_Capacity > 0 && need * 4 < m_Capacity ? m_OversizedFrames + 1 : 0;
            bool shrink = m_OversizedFrames > kShrinkAfterFrames;
            if (!dirty && !shrink && m_ViewPos != null)
                return true;

            if (m_ViewPos == null || need > m_Capacity || shrink)
            {
                m_OversizedFrames = 0;
                // Grow with headroom so objects streaming in do not reallocate every frame.
                if (!Allocate(need + need / 4))
                    return false;
            }

            // Assign slices. Order does not matter as long as it is stable frame to frame, which
            // it is: the dictionary is only walked again when the set actually changed.
            int cursor = 0;
            foreach (var gs in splats.Keys)
            {
                if (!Eligible(gs))
                    continue;
                gs.m_NCViewBase = cursor;
                gs.m_NCViewLayoutCount = gs.splatCount;
                // Every slice moved, so whatever was projected into the old one is meaningless.
                gs.m_HasSortedOnce = false;
                cursor += gs.splatCount;
            }
            m_LayoutRenderers = renderers;
            m_LayoutSplats = needed;
            m_HasOrder = false;   // nothing that indexes the old layout is valid
            return true;
        }

        static bool Eligible(SplatRenderer gs) =>
            gs != null && gs.HasValidAsset && gs.HasValidRenderSetup && gs.splatCount > 0;

        /// <summary>
        /// Release the shared textures and un-assign every renderer's slice. Safe to call at any
        /// time; a renderer left holding a base into a freed texture is a dangling index rather
        /// than a missing one, so the un-assignment is not optional.
        /// </summary>
        internal void Deactivate(Dictionary<SplatRenderer, MaterialPropertyBlock> splats)
        {
            if (splats != null)
            {
                foreach (var gs in splats.Keys)
                {
                    if (gs == null || gs.m_NCViewBase < 0)
                        continue;
                    gs.m_NCViewBase = -1;
                    gs.m_NCViewLayoutCount = 0;
                    gs.m_HasSortedOnce = false;   // its own per-object order was never maintained here
                }
            }
            ReleaseTextures();
        }

        public void Dispose()
        {
            ReleaseTextures();
            m_Sorter.Dispose();
        }

        void ReleaseTextures()
        {
            ReleaseTex(ref m_ViewPos);
            ReleaseTex(ref m_ViewAxis);
            ReleaseTex(ref m_ViewCol);
            m_Mrt = null;
            m_NeedsClear = false;
            m_Capacity = 0;
            m_LayoutRenderers = 0;
            m_LayoutSplats = 0;
            m_HasOrder = false;
        }

        static void ReleaseTex(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            rt = null;
        }

        bool Allocate(int capacity)
        {
            capacity = Mathf.Max(capacity, 1024);
            int texHeight = (capacity + kTexWidth - 1) / kTexWidth;
            if (texHeight > SystemInfo.maxTextureSize)
            {
                LogAllocFailure($"{capacity:N0} splats needs a {texHeight}px tall view texture, " +
                                $"over this device's {SystemInfo.maxTextureSize}px limit");
                return false;
            }

            ReleaseTextures();
            m_ViewPos = CreateTex("GaussianNCViewPos", kPosFormat, texHeight);
            m_ViewAxis = CreateTex("GaussianNCViewAxis", kHalfFormat, texHeight);
            m_ViewCol = CreateTex("GaussianNCViewCol", kHalfFormat, texHeight);
            if (m_ViewPos == null || m_ViewAxis == null || m_ViewCol == null)
            {
                LogAllocFailure($"could not create the {kTexWidth}x{texHeight} shared view textures");
                ReleaseTextures();
                return false;
            }
            m_Mrt = new RenderTargetIdentifier[]
            {
                new(m_ViewPos), new(m_ViewAxis), new(m_ViewCol)
            };
            // A fresh render target holds undefined memory, and the merged draw reads a slot's
            // clip w to decide whether the pre-pass culled it - zero is that "culled" value. Every
            // slot the order can reference IS rewritten this same pass (the layout change forced
            // every renderer to re-project), so this is belt and braces rather than load-bearing;
            // it is deferred to the command buffer because binding a render target outside one, in
            // the middle of an SRP pass, is not something to do for belt and braces.
            m_NeedsClear = true;
            if (!m_Sorter.SetCapacity(capacity, kTexWidth))
            {
                LogAllocFailure($"could not size the global order texture for {capacity:N0} splats");
                ReleaseTextures();
                return false;
            }
            m_Capacity = capacity;
            if (!s_AllocLogged)
            {
                s_AllocLogged = true;
                long bytes = (long)capacity * (kViewBytesPerSplat + 4); // + the R32F order texture
                Debug.Log($"[GaussianSplat] no-compute cross-object sorting active: {capacity:N0} splat slots, " +
                          $"{kTexWidth}x{texHeight} shared view textures, {bytes / (1024f * 1024f):0.0} MB " +
                          $"(MRT={SystemInfo.supportedRenderTargetCount}, ARGBFloat render=" +
                          $"{SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)}, " +
                          $"ARGBHalf render={SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)}, " +
                          $"maxTextureSize={SystemInfo.maxTextureSize}, uvTop={SystemInfo.graphicsUVStartsAtTop})");
            }
            return true;
        }

        // Once per session, not once per resize: the numbers that matter are the device's, and they
        // do not change. Static so a teardown/rebuild does not re-log them either.
        static bool s_AllocLogged;

        static RenderTexture CreateTex(string name, GraphicsFormat fmt, int height)
        {
            var desc = new RenderTextureDescriptor(kTexWidth, height, fmt, 0)
            {
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            var rt = new RenderTexture(desc)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!rt.Create())
            {
                UnityEngine.Object.DestroyImmediate(rt);
                return null;
            }
            return rt;
        }

        void LogAllocFailure(string what)
        {
            if (m_AllocFailedLogged)
                return;
            m_AllocFailedLogged = true;
            Debug.LogWarning($"Cross-object splat sorting on the no-compute path: {what}; " +
                             "falling back to per-object ordering.");
        }

        // ------------------------------------------------------------------------------------
        // Phase 1: projection into the shared view textures
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Re-project every object whose splats or camera moved. Detours the render target to the
        /// shared view textures and puts <paramref name="restore"/> back, which is why the caller
        /// has to state what it had bound - a CommandBuffer cannot be asked.
        /// </summary>
        /// <returns>Whether anything was re-projected, i.e. whether the order is now stale.</returns>
        internal bool ProjectVisible(CommandBuffer cmb, Camera cam,
            List<(SplatRenderer, MaterialPropertyBlock)> renderSplats, in SplatRenderTarget restore)
        {
            m_ToProject.Clear();
            int frame = Time.frameCount;
            Matrix4x4 camMat = cam.transform.localToWorldMatrix;

            foreach (var kvp in renderSplats)
            {
                var gs = kvp.Item1;
                // The same predicate the ORDER uses. It has to be: projecting something the order
                // will not reference is wasted work, and ordering something nothing projected is
                // a run of slots pointing at undefined texels.
                if (!Drawable(gs))
                    continue;

                bool freeze = GaussianSplatRenderSystem.s_FreezeComputeAfterWarmup &&
                              gs.m_FrameCounter > GaussianSplatRenderSystem.kFreezeAfterFrames;
                ++gs.m_FrameCounter;

                // Same static-scene skip signals as every other path: the camera pose, this
                // object's transform, its processed splat count, and whether the skinner re-posed
                // it this frame. The view data is CLIP space, so a camera move invalidates every
                // object, which is exactly what camObjMoved reports.
                var matrix = gs.transform.localToWorldMatrix;
                bool camObjMoved = camMat != gs.m_LastSortCamera
                                   || matrix != gs.m_LastSortModel
                                   || gs.ProcCount != gs.m_LastSortProcCount;
                bool posed = gs.m_LastPosedFrame == frame;
                bool moved = !gs.m_HasSortedOnce || camObjMoved || posed;
                if (freeze || !(moved || !GaussianSplatRenderSystem.s_SkipStaticView))
                    continue;

                gs.BindNoComputeSource(kvp.Item2, cam);
                m_ToProject.Add(kvp);
                gs.m_LastSortCamera = camMat;
                gs.m_LastSortModel = matrix;
                gs.m_LastSortProcCount = gs.ProcCount;
                gs.m_HasSortedOnce = true;
            }

            if (m_ToProject.Count == 0)
                return false;

            cmb.BeginSample(s_ProfProject);
            // m_Mrt[0] doubles as the depth target: these textures carry no depth buffer, and
            // SetRenderTarget wants one named anyway.
            cmb.SetRenderTarget(m_Mrt, m_Mrt[0]);
            if (m_NeedsClear)
            {
                cmb.ClearRenderTarget(RTClearFlags.Color, Color.clear, 1f, 0);
                m_NeedsClear = false;
            }
            float texHeight = m_ViewPos.height;
            // The interpolant the fragment stage derives its texel from runs bottom-up in NDC, so
            // which end of the target texel row 0 sits at is the one thing the shader cannot work
            // out for itself. See _GsViewFlipY.
            float flipY = SystemInfo.graphicsUVStartsAtTop ? 1f : 0f;
            foreach (var kvp in m_ToProject)
            {
                var gs = kvp.Item1;
                var mpb = kvp.Item2;
                int count = gs.ProcCount;
                int row0 = gs.m_NCViewBase / kTexWidth;
                int row1 = (gs.m_NCViewBase + count - 1) / kTexWidth + 1;
                mpb.SetVector(SplatRenderer.Props.GsViewRect,
                    new Vector4(row0, row1, kTexWidth, texHeight));
                mpb.SetInteger(SplatRenderer.Props.GsViewBase, gs.m_NCViewBase);
                mpb.SetFloat(SplatRenderer.Props.GsViewFlipY, flipY);
                // Two triangles over this object's rows, no index buffer and no transform: the
                // vertex shader emits clip positions directly.
                cmb.DrawProcedural(Matrix4x4.identity, gs.m_MatSplats, kPassViewData,
                    MeshTopology.Triangles, 6, 1, mpb);
            }
            restore.Bind(cmb);
            cmb.EndSample(s_ProfProject);
            m_ToProject.Clear();
            return true;
        }

        // ------------------------------------------------------------------------------------
        // Phases 2 and 3: one global order, one draw per render-order group
        // ------------------------------------------------------------------------------------

        // One render-order group's slice of the shared order texture, and the renderer whose
        // material and index buffer the merged draw borrows. Kept across frames so a reused order
        // can be redrawn without re-deriving the ranges.
        struct Group
        {
            public int slotBase;
            public int count;
            public SplatRenderer first;
        }

        readonly List<Group> m_Groups = new();

        /// <summary>
        /// Order every visible splat and draw it. renderOrder still groups - all of order 0 is
        /// ordered and drawn, then all of order 1 - so it remains a hard layering control rather
        /// than being silently ignored.
        /// </summary>
        /// <param name="viewRecomputed">Whether any renderer re-projected this pass.</param>
        internal void SortAndDraw(CommandBuffer cmb, Camera cam,
            List<(SplatRenderer, MaterialPropertyBlock)> active, bool viewRecomputed)
        {
            if (m_ViewPos == null || active.Count == 0)
                return;

            // Signature over the WHOLE frame, not per group: the order texture is one upload
            // covering every group, so reusing any part of it means reusing all of it.
            int total = 0;
            int signature = 17;
            for (int i = 0; i < active.Count; ++i)
            {
                var gs = active[i].Item1;
                if (!Drawable(gs))
                    continue;
                total += gs.ProcCount;
                unchecked
                {
                    signature = signature * 31 + gs.m_NCViewBase;
                    signature = signature * 31 + gs.ProcCount;
                    signature = signature * 31 + gs.renderOrder;
                }
            }
            if (total <= 0 || total > m_Capacity)
                return;

            int camId = cam.GetInstanceID();
            bool reuse = m_HasOrder && !viewRecomputed &&
                         m_OrderCamera == camId && m_OrderTotal == total && m_OrderSignature == signature;
            if (!reuse)
            {
                BuildOrder(cam, active);
                m_HasOrder = true;
                m_OrderCamera = camId;
                m_OrderTotal = total;
                m_OrderSignature = signature;
            }
            if (m_Groups.Count == 0)
                return;

            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            var screenPar = new Vector4(eyeW != 0 ? eyeW : cam.pixelWidth, eyeH != 0 ? eyeH : cam.pixelHeight, 0, 0);

            m_Mpb.Clear();
            // The merged vertex shader reads exactly four textures and two numbers - no splat
            // source data, no object matrix, no per-object uniform. That is the whole reason one
            // draw can cover many objects, and it is why this binds so much less than the
            // per-object path does.
            m_Mpb.SetTexture(SplatRenderer.Props.GsViewPosTex, m_ViewPos);
            m_Mpb.SetTexture(SplatRenderer.Props.GsViewAxisTex, m_ViewAxis);
            m_Mpb.SetTexture(SplatRenderer.Props.GsViewColTex, m_ViewCol);
            m_Mpb.SetTexture(SplatRenderer.Props.OrderTex, m_Sorter.orderTexture);
            m_Mpb.SetInteger(SplatRenderer.Props.GsViewTexWidth, kTexWidth);
            m_Mpb.SetVector(SplatRenderer.Props.VecScreenParams, screenPar);

            cmb.BeginSample(GaussianSplatRenderSystem.s_ProfDraw);
            foreach (var g in m_Groups)
            {
                if (g.count <= 0 || g.first == null || g.first.m_MatSplats == null)
                    continue;
                // RECORDED globals, not values on the property block. One block serves every
                // group, so a per-draw block value would depend on CommandBuffer snapshotting the
                // block at record time rather than holding a reference - true as far as it goes,
                // but the whole feature would fail silently if it ever were not. SetGlobalInteger
                // is itself a command, replayed in order with the draws, so the pairing is
                // guaranteed. Nothing sets these on a property block anywhere, which matters: a
                // block value would override the global.
                cmb.SetGlobalInteger(SplatRenderer.Props.GsDrawSlotBase, g.slotBase);
                cmb.SetGlobalInteger(SplatRenderer.Props.GsDrawCount, g.count);
                // Identity, not any object's matrix: the vertex shader never reads the object
                // transform (it was applied in the pre-pass, before the splats were projected),
                // and the splats in this one draw come from many objects anyway.
                cmb.DrawProcedural(g.first.m_GpuIndexBuffer, Matrix4x4.identity, g.first.m_MatSplats,
                    kPassMerged, MeshTopology.Triangles, 6, g.count, m_Mpb);
            }
            // The globals outlive this pass, so put them back for whatever draws next.
            cmb.SetGlobalInteger(SplatRenderer.Props.GsDrawSlotBase, 0);
            cmb.SetGlobalInteger(SplatRenderer.Props.GsDrawCount, 0);
            cmb.EndSample(GaussianSplatRenderSystem.s_ProfDraw);
        }

        // One counting sort per render-order group, all of them written into one order texture and
        // uploaded once. active is already sorted by render order, so the groups are contiguous runs.
        void BuildOrder(Camera cam, List<(SplatRenderer, MaterialPropertyBlock)> active)
        {
            s_ProfSort.Begin();
            m_Groups.Clear();
            m_Sorter.Begin();
            for (int i = 0; i < active.Count; )
            {
                int order = active[i].Item1.renderOrder;
                SplatRenderer first = null;
                int end = i;
                while (end < active.Count && active[end].Item1.renderOrder == order)
                {
                    var gs = active[end].Item1;
                    if (Drawable(gs))
                    {
                        first ??= gs;
                        var d = gs.m_NoComputeData;
                        bool skinned = gs.BuildNoComputeDepthRows(cam, out var rows);
                        m_Sorter.AddObject(gs.m_NCViewBase, d.positions, d.dominantBone, rows, skinned, gs.ProcCount);
                    }
                    ++end;
                }
                int count = m_Sorter.EndGroup(out int slotBase);
                if (count > 0 && first != null)
                    m_Groups.Add(new Group { slotBase = slotBase, count = count, first = first });
                i = end;
            }
            m_Sorter.Upload();
            s_ProfSort.End();
        }

        // Debug render modes draw through their own shaders, which read splat data and the object
        // matrix rather than view data - they cannot be merged. On this path those materials are
        // never created at all (their shaders require compute), so such a renderer simply does not
        // draw; excluding it here is what keeps it out of the ORDER as well, which would otherwise
        // leave a run of slots pointing at splats nothing projected.
        static bool Drawable(SplatRenderer gs) =>
            gs != null && gs.m_NCViewBase >= 0 && gs.m_MatSplats != null && gs.m_GpuIndexBuffer != null
            && gs.m_NoComputeData != null && gs.ProcCount > 0
            && gs.m_RenderMode == SplatRenderer.RenderMode.Splats;
    }

    /// <summary>
    /// The render target a caller had bound for the splat draws, so a pass that has to detour to
    /// its own targets can put it back.
    /// </summary>
    /// <remarks>
    /// A <see cref="CommandBuffer"/> cannot be asked what is currently bound, and the caller is
    /// the only one that knows: the built-in pipeline binds a temporary RT by name, URP binds a
    /// render-graph texture, HDRP binds its own. So each states it next to its own SetRenderTarget
    /// and the render system carries it. An undeclared target is not a small problem - it would
    /// leave the splat draws writing into the view-data textures - so the no-compute global path
    /// refuses to run without one rather than guessing.
    /// </remarks>
    readonly struct SplatRenderTarget
    {
        readonly RenderTargetIdentifier m_Color;
        readonly RenderTargetIdentifier m_Depth;
        readonly bool m_HasColor;
        readonly bool m_HasDepth;

        internal SplatRenderTarget(RenderTargetIdentifier color)
        {
            m_Color = color;
            m_Depth = default;
            m_HasColor = true;
            m_HasDepth = false;
        }

        internal SplatRenderTarget(RenderTargetIdentifier color, RenderTargetIdentifier depth)
        {
            m_Color = color;
            m_Depth = depth;
            m_HasColor = true;
            m_HasDepth = true;
        }

        internal bool IsValid => m_HasColor;

        internal void Bind(CommandBuffer cmb)
        {
            if (!m_HasColor)
                return;
            if (m_HasDepth)
                cmb.SetRenderTarget(m_Color, m_Depth);
            else
                cmb.SetRenderTarget(m_Color);
        }
    }
}
