// SPDX-License-Identifier: MIT

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
    /// <summary>
    /// Optional cross-object splat sorting: one depth order spanning every visible splat object,
    /// drawn as a single draw, so a character standing inside an environment capture interleaves
    /// with it where they overlap in depth instead of being drawn wholly in front of or behind it.
    /// Off unless a <see cref="SplatRendererFeature"/> asks for it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is possible.</b> CSCalcViewData already projects each splat all the way to
    /// clip space (<c>_MatrixVP * _MatrixObjectToWorld * pos</c>) and hands the ellipse axes to the
    /// vertex shader in pixels. So <c>SplatViewData</c> is screen space with the object transform
    /// already baked in, and the splat vertex shader reads nothing else - no object matrix, no
    /// per-object uniform. Entries produced by different objects are therefore directly comparable
    /// and directly drawable together.</para>
    ///
    /// <para><b>What it costs.</b> One pass over every visible splat to build the keys, plus one
    /// sort of all of them, on every frame anything moves. There is no way around that: a single
    /// order over N splats has to be produced from N depths. The per-object sorts are skipped while
    /// this is on (they would be re-doing the same work at a granularity the merged draw cannot
    /// use), so for a single splat object it is close to a wash; the cost is real when a large
    /// static environment, which would otherwise never re-sort, has to be re-ordered because a
    /// character inside it moved.</para>
    ///
    /// <para><b>Why one sort rather than a k-way merge of the per-object orders.</b> An exact merge
    /// of k sorted lists needs, for each element, its rank among the other k-1 lists: that is
    /// O(N&#183;k&#183;log) with binary searches, which is worse than a sort as soon as there is a
    /// crowd rather than two objects. The only k-independent way to compute those ranks is a global
    /// histogram over the key space - which IS a counting sort. So the merge and the global sort are
    /// the same algorithm, and this uses the sorter the platform already has.</para>
    ///
    /// <para><b>Ordering fidelity.</b> With the radix sorter (wave intrinsics available) the key is
    /// the exact 32-bit camera-space depth, so the order is exact. Without it - OpenGL ES, where the
    /// counting sort is the fast path - the key is quantized to 16 bits, and quantized over the
    /// whole scene's depth range rather than one object's, so it is coarser than the per-object sort
    /// it replaces. The quantization is on inverse distance to spend that resolution where splats
    /// are large on screen.</para>
    /// </remarks>
    sealed class SplatUnifiedSort
    {
        // Must match struct UnifiedRegion in SplatUtilities.compute.
        struct Region
        {
            public uint outOffset;
            public uint viewBase;
        }

        internal static readonly ProfilerMarker s_ProfUnifiedSort =
            new(ProfilerCategory.Render, "GaussianSplat.UnifiedSort", MarkerFlags.SampleGPU);

        // ------------------------------------------------------------------------------------
        // The switch. A renderer feature stamps the current frame; the render system reads it at
        // the top of SortAndRenderSplats and takes the unified branch or the normal one. A frame
        // stamp rather than a plain bool so that removing the feature, disabling it, or swapping
        // renderer assets turns this back off on its own, with no teardown callback to forget.
        // ------------------------------------------------------------------------------------
        static int s_RequestFrame = int.MinValue;
        internal static void Request() => s_RequestFrame = Time.frameCount;
        internal static void ClearRequest() => s_RequestFrame = int.MinValue;
        // Two frames of slack: the request is stamped when passes are enqueued and read when they
        // execute, and in the editor those can straddle a frame boundary.
        internal static bool Requested => s_RequestFrame != int.MinValue && Time.frameCount - s_RequestFrame <= 2;

        // ------------------------------------------------------------------------------------
        // Shared GPU resources. Allocated only while unified sorting is on.
        // ------------------------------------------------------------------------------------

        // Every splat object's projected splats, one slice each (SplatRenderer.m_ViewBase). The
        // renderers release their own view buffers while this exists, so this is a relocation of
        // that memory, not a second copy.
        GraphicsBuffer m_View;
        RenderTexture m_ViewTex;    // vertex-texture-fetch path only, mirrors SplatRenderer.m_GpuViewTex
        GraphicsBuffer m_Keys;      // sort keys, one per draw slot
        GraphicsBuffer m_Order;     // sort payload / draw order: indices into m_View
        GraphicsBuffer m_RegionBuf; // the region table CSUnifiedBuildKeys binary-searches
        int m_Capacity;             // slots m_View / m_Keys / m_Order are sized for

        GpuSorting m_Radix;
        GpuSorting.Args m_RadixArgs;
        GpuCountingSort m_Counting;
        GpuCountingSort.Resources m_CountingRes;
        ComputeShader m_Utilities;
        int m_KernelBuildKeys = -1;
        int m_KernelBuildViewTex = -1;
        bool m_SorterFailedLogged;
        // Sticky: this device has no sorter or no unified kernels, which will not change. Without
        // it, every frame would rebuild the sorter objects (and re-log) just to fail again.
        bool m_Disabled;

        // Layout bookkeeping: what the current slice assignment was made for.
        int m_LayoutRenderers;
        long m_LayoutSplats;

        readonly List<Region> m_Regions = new();
        Region[] m_RegionUpload = System.Array.Empty<Region>();
        readonly MaterialPropertyBlock m_Mpb = new();

        // Single-slot cache for "the order buffer already holds what this draw needs". Correct to
        // miss, never correct to hit wrongly, so the key is deliberately over-specific.
        bool m_HasOrder;
        int m_OrderCamera;
        int m_OrderTotal;
        int m_OrderSignature;

        // Shrink hysteresis: capacity only comes back down after the smaller working set has held
        // for a while, so streaming objects in and out cannot thrash the allocation.
        int m_OversizedFrames;
        const int kShrinkAfterFrames = 300;

        internal bool IsActive => m_View != null;

        // ------------------------------------------------------------------------------------
        // Activation
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Make sure the shared buffer exists and every registered renderer owns a slice of it.
        /// False means unified sorting cannot run at all (no usable sorter, nothing to draw, or a
        /// scene too large for the shared resources) and the caller must use the per-object path.
        /// </summary>
        internal bool TryActivate(Dictionary<SplatRenderer, MaterialPropertyBlock> splats)
        {
            if (m_Disabled)
                return false;
            if (!EnsureSorter(splats))
                return false;
            return EnsureLayout(splats);
        }

        /// <summary>
        /// Hand every renderer its own view buffer back and release the shared ones. Safe and
        /// required to call after any failed activation: a failure can happen after the shared
        /// buffer was already released, and a renderer left pointing at that is a dangling handle.
        /// The sorter itself is kept, so a transient failure does not rebuild it every frame.
        /// </summary>
        internal void Deactivate(Dictionary<SplatRenderer, MaterialPropertyBlock> splats)
        {
            if (splats != null)
            {
                foreach (var gs in splats.Keys)
                {
                    if (gs != null && gs.UsesUnifiedView)
                        gs.SetUnifiedView(null, -1, 0);
                }
            }
            ReleaseBuffers();
        }

        internal void Release()
        {
            ReleaseBuffers();
            m_RegionBuf?.Dispose(); m_RegionBuf = null;
            m_Radix = null;
            m_Counting = null;
            m_Utilities = null;
            m_KernelBuildKeys = -1;
            m_KernelBuildViewTex = -1;
            m_OversizedFrames = 0;
        }

        // Everything sized to the splat count, without touching which sorter was chosen. Separate
        // from Release because resizing goes through here: tearing down the sorter mid-resize would
        // leave Allocate choosing resources for a sorter it had just forgotten about.
        void ReleaseBuffers()
        {
            m_View?.Dispose(); m_View = null;
            m_Keys?.Dispose(); m_Keys = null;
            m_Order?.Dispose(); m_Order = null;
            if (m_ViewTex != null)
            {
                m_ViewTex.Release();
                Object.DestroyImmediate(m_ViewTex);
                m_ViewTex = null;
            }
            m_RadixArgs.resources.Dispose();
            m_CountingRes.Dispose();
            m_Capacity = 0;
            m_LayoutRenderers = 0;
            m_LayoutSplats = 0;
            m_HasOrder = false;
        }

        bool EnsureSorter(Dictionary<SplatRenderer, MaterialPropertyBlock> splats)
        {
            if (m_Utilities != null && m_KernelBuildKeys >= 0)
                return true;

            // The compute shaders are serialized on the components (they cannot be found by name at
            // runtime, and a player build strips whatever nothing references), so take them from the
            // first renderer that has them.
            ComputeShader utilities = null, radix = null;
            foreach (var gs in splats.Keys)
            {
                if (gs == null || gs.splatUtilities == null) continue;
                utilities = gs.splatUtilities;
                radix = gs.deviceRadixSort;
                break;
            }
            if (utilities == null)
                return false;   // nothing registered yet; try again once something has loaded
            if (!utilities.HasKernel("CSUnifiedBuildKeys"))
            {
                m_Disabled = true;
                return false;
            }

            m_Utilities = utilities;
            m_KernelBuildKeys = utilities.FindKernel("CSUnifiedBuildKeys");
            m_KernelBuildViewTex = utilities.HasKernel("CSUnifiedBuildViewTex")
                ? utilities.FindKernel("CSUnifiedBuildViewTex") : -1;
            // Without it the vertex stage would sample a texture nothing filled. Those devices are
            // exactly the ones that cannot bind buffers in the vertex stage either, so there is no
            // second path to fall back to within unified sorting - fall back out of it.
            if (SplatRenderer.UseVertexTextureFetch && m_KernelBuildViewTex < 0)
            {
                m_Utilities = null;
                m_KernelBuildKeys = -1;
                m_Disabled = true;
                return false;
            }

            // Same preference order the per-object sort uses: radix where the wave intrinsics exist,
            // counting sort otherwise. No bitonic fallback here - it needs power-of-two padding with
            // a sentinel tail, which is a lot of machinery for a path the counting sort already
            // covers on every device that lacks wave ops.
            m_Radix = new GpuSorting(SplatRenderer.s_ForceGLESPath ? null : radix != null ? radix : utilities);
            if (!m_Radix.Valid)
            {
                m_Radix = null;
                m_Counting = new GpuCountingSort(utilities);
                if (!m_Counting.Valid)
                {
                    m_Counting = null;
                    if (!m_SorterFailedLogged)
                    {
                        m_SorterFailedLogged = true;
                        Debug.LogWarning("Unified splat sorting: no usable GPU sorter on this device " +
                                         $"({SystemInfo.graphicsDeviceType}); falling back to per-object sorting.");
                    }
                    m_Utilities = null;
                    m_KernelBuildKeys = -1;
                    m_Disabled = true;
                    return false;
                }
            }
            return true;
        }

        bool EnsureLayout(Dictionary<SplatRenderer, MaterialPropertyBlock> splats)
        {
            // Validate the current assignment against the registered set. Cheap (the set is small)
            // and self-correcting: a renderer that loaded, unloaded, or swapped its capture shows up
            // here as an unassigned slice or a changed count, with no registration hook to keep in
            // sync.
            int renderers = 0;
            long needed = 0;
            bool dirty = false;
            foreach (var gs in splats.Keys)
            {
                if (gs == null || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                ++renderers;
                needed += gs.splatCount;
                if (gs.m_ViewBase < 0 || !gs.UsesUnifiedView || gs.m_ViewLayoutCount != gs.splatCount)
                    dirty = true;
            }
            if (renderers == 0 || needed <= 0)
                return false;
            // 48 bytes a splat plus the sort buffers; well before an int overflows, the allocation
            // itself is hopeless. Refuse rather than wrap around into a tiny buffer.
            if (needed > 40_000_000L)
            {
                if (!m_SorterFailedLogged)
                {
                    m_SorterFailedLogged = true;
                    Debug.LogWarning($"Unified splat sorting: {needed:N0} splats is more than the shared " +
                                     "buffers can hold; falling back to per-object sorting.");
                }
                return false;
            }
            if (renderers != m_LayoutRenderers || needed != m_LayoutSplats)
                dirty = true;

            int need = (int)needed;
            // Grow with headroom so objects streaming in do not reallocate every frame; only give
            // the memory back once the smaller working set has held for a while, so streaming in and
            // out cannot thrash the allocation. Counted every frame rather than only on the frames
            // the layout already changed, or a set that shrank once would never trigger it.
            m_OversizedFrames = m_Capacity > 0 && need * 4 < m_Capacity ? m_OversizedFrames + 1 : 0;
            bool shrink = m_OversizedFrames > kShrinkAfterFrames;
            if (!dirty && !shrink && m_View != null)
                return true;

            if (m_View == null || need > m_Capacity || shrink)
            {
                m_OversizedFrames = 0;
                if (!Allocate(need + need / 4))
                    return false;
            }

            // Assign slices. Order does not matter as long as it is stable frame to frame, which it
            // is: the dictionary is only walked again when the set actually changed.
            int cursor = 0;
            foreach (var gs in splats.Keys)
            {
                if (gs == null || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                gs.SetUnifiedView(m_View, cursor, gs.splatCount);
                cursor += gs.splatCount;
            }
            m_LayoutRenderers = renderers;
            m_LayoutSplats = needed;
            m_HasOrder = false;   // every slice moved; nothing that indexes the old layout is valid
            return true;
        }

        bool Allocate(int capacity)
        {
            capacity = Mathf.Max(capacity, 1024);

            // The vertex-texture-fetch path needs a texture 2 texels a splat; on a device that far
            // from the vertex-SSBO path, a scene big enough to overflow the texture height is a
            // hard stop rather than something to render wrongly.
            int texHeight = 0;
            if (SplatRenderer.UseVertexTextureFetch)
            {
                texHeight = (capacity * 2 + SplatRenderer.kViewTexWidth - 1) / SplatRenderer.kViewTexWidth;
                if (texHeight > SystemInfo.maxTextureSize)
                {
                    if (!m_SorterFailedLogged)
                    {
                        m_SorterFailedLogged = true;
                        Debug.LogWarning($"Unified splat sorting: {capacity:N0} splats needs a {texHeight}px tall " +
                                         $"view texture, over this device's {SystemInfo.maxTextureSize}px limit; " +
                                         "falling back to per-object sorting.");
                    }
                    return false;
                }
            }

            ReleaseBuffers();
            m_Capacity = capacity;
            m_View = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, SplatRenderer.kGpuViewDataSize)
                { name = "GaussianUnifiedView" };
            m_Keys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4) { name = "GaussianUnifiedKeys" };
            m_Order = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4) { name = "GaussianUnifiedOrder" };
            // A fresh GraphicsBuffer holds undefined memory and the draw indexes m_View through
            // m_Order, so seed a valid permutation. Every path clamps on top of this, but a defined
            // starting state is what keeps a frame drawn before the first sort from being garbage.
            var seed = new uint[capacity];
            for (int i = 0; i < capacity; ++i) seed[i] = (uint)i;
            m_Order.SetData(seed);

            if (texHeight > 0)
            {
                var desc = new RenderTextureDescriptor(SplatRenderer.kViewTexWidth, texHeight,
                    GraphicsFormat.R32G32B32A32_UInt, 0)
                {
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false,
                    msaaSamples = 1,
                };
                m_ViewTex = new RenderTexture(desc)
                {
                    name = "GaussianUnifiedViewTex",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (!m_ViewTex.Create())
                {
                    Debug.LogError("Unified splat sorting: failed to create the shared view texture; " +
                                   "falling back to per-object sorting.");
                    ReleaseBuffers();
                    return false;
                }
            }

            if (m_Radix != null)
                m_RadixArgs.resources = GpuSorting.SupportResources.Load((uint)capacity);
            else
                m_CountingRes = GpuCountingSort.Resources.Load(capacity);
            return true;
        }

        // ------------------------------------------------------------------------------------
        // Per-frame work
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Order and draw one render-order group of splat objects as a single draw.
        /// </summary>
        /// <param name="active">The render system's active list, sorted by render order.</param>
        /// <param name="start">First index of this group (inclusive).</param>
        /// <param name="end">Last index of this group (exclusive).</param>
        /// <param name="viewRecomputed">Whether any renderer in the group re-projected this pass.</param>
        /// <param name="soleGroup">
        /// Whether this is the only group in the frame. The order buffer is shared, so a second
        /// group overwrites it: reuse across frames is only sound when nothing else touches it.
        /// </param>
        internal void DrawGroup(CommandBuffer cmb, Camera cam,
                                List<(SplatRenderer, MaterialPropertyBlock)> active,
                                int start, int end, bool viewRecomputed, bool soleGroup)
        {
            if (m_View == null)
                return;

            m_Regions.Clear();
            int total = 0;
            int signature = 17;
            SplatRenderer first = null;
            for (int i = start; i < end; ++i)
            {
                var gs = active[i].Item1;
                // Debug render modes draw through their own shaders, which read splat data and the
                // object matrix rather than view data - they cannot be merged, and the caller draws
                // them individually.
                if (gs.m_RenderMode != SplatRenderer.RenderMode.Splats)
                    continue;
                if (!gs.UsesUnifiedView || gs.m_ViewBase < 0)
                    continue;
                // No material means the projection was skipped for it too (same guard as the
                // per-object path), so its slice holds nothing this draw may read.
                if (gs.m_MatSplats == null)
                    continue;
                int count = gs.ProcCount;   // the LOD prefix, which is what CalcViewData wrote
                if (count <= 0)
                    continue;
                if (m_Regions.Count == 0)
                    first = gs;
                m_Regions.Add(new Region { outOffset = (uint)total, viewBase = (uint)gs.m_ViewBase });
                total += count;
                unchecked { signature = signature * 31 + gs.m_ViewBase; signature = signature * 31 + count; }
            }
            if (first == null || total <= 0)
                return;
            if (total > m_Capacity)
                return;   // layout is stale for one more frame; EnsureLayout grows it next call

            Material displayMat = first.m_MatSplats;
            if (displayMat == null || first.m_GpuIndexBuffer == null || first.gpuPosData == null)
                return;

            int camId = cam.GetInstanceID();
            bool reuse = soleGroup && m_HasOrder && !viewRecomputed &&
                         m_OrderCamera == camId && m_OrderTotal == total && m_OrderSignature == signature;
            if (!reuse)
            {
                BuildAndSort(cmb, cam, total);
                m_HasOrder = true;
                m_OrderCamera = camId;
                m_OrderTotal = total;
                m_OrderSignature = signature;
            }

            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            var screenPar = new Vector4(eyeW != 0 ? eyeW : cam.pixelWidth, eyeH != 0 ? eyeH : cam.pixelHeight, 0, 0);

            // Vertex-texture-fetch: pack the merged order into the shared texture, exactly as the
            // per-object path packs its own. The kernel reads _SplatSortKeys[p] and indexes
            // _SplatViewData with it, which is precisely what the merged order holds.
            if (m_ViewTex != null && m_KernelBuildViewTex >= 0 && !reuse)
            {
                int bvt = m_KernelBuildViewTex;
                cmb.SetComputeBufferParam(m_Utilities, bvt, SplatRenderer.Props.SplatSortKeys, m_Order);
                cmb.SetComputeBufferParam(m_Utilities, bvt, SplatRenderer.Props.SplatViewData, m_View);
                cmb.SetComputeTextureParam(m_Utilities, bvt, SplatRenderer.Props.UniViewDataTex, m_ViewTex);
                cmb.SetComputeIntParam(m_Utilities, SplatRenderer.Props.UniCount, total);
                cmb.SetComputeIntParam(m_Utilities, SplatRenderer.Props.UniViewCount, m_View.count);
                cmb.SetComputeIntParam(m_Utilities, SplatRenderer.Props.UniViewTexWidth, m_ViewTex.width);
                m_Utilities.GetKernelThreadGroupSizes(bvt, out uint bvtX, out _, out _);
                cmb.DispatchCompute(m_Utilities, bvt, (total + (int)bvtX - 1) / (int)bvtX, 1, 1);
            }

            m_Mpb.Clear();
            // The splat vertex shader reads exactly three things: the order buffer, the view data,
            // and (only when _SplatBitsValid) the selection bits - no splat positions, no object
            // matrix, no per-object uniform. That is the whole reason one draw can cover many
            // objects, and it is why this binds so much less than the per-object path does.
            //
            // Selection highlighting is per-object and editor-only; a merged draw has no per-object
            // bit buffer to consult, so it is off. The buffer is still bound (to something harmless)
            // because leaving a declared vertex-stage buffer unbound is its own class of bug.
            m_Mpb.SetBuffer(SplatRenderer.Props.SplatSelectedBits, first.gpuPosData);
            m_Mpb.SetInt(SplatRenderer.Props.SplatBitsValid, 0);
            displayMat.SetBuffer(SplatRenderer.Props.SplatSelectedBits, first.gpuPosData);
            displayMat.SetInt(SplatRenderer.Props.SplatBitsValid, 0);
            m_Mpb.SetBuffer(SplatRenderer.Props.SplatViewData, m_View);
            m_Mpb.SetBuffer(SplatRenderer.Props.OrderBuffer, m_Order);
            m_Mpb.SetInteger(SplatRenderer.Props.OrderBufferCount, total);
            m_Mpb.SetInteger(SplatRenderer.Props.SplatViewDataCount, m_View.count);
            m_Mpb.SetVector(SplatRenderer.Props.VecScreenParams, screenPar);
            // Mirrored onto the material instance as well: per-draw MaterialPropertyBlock SSBO
            // bindings are unreliable on OpenGL ES (see the per-object draw).
            displayMat.SetBuffer(SplatRenderer.Props.SplatViewData, m_View);
            displayMat.SetBuffer(SplatRenderer.Props.OrderBuffer, m_Order);
            displayMat.SetInteger(SplatRenderer.Props.OrderBufferCount, total);
            displayMat.SetInteger(SplatRenderer.Props.SplatViewDataCount, m_View.count);
            displayMat.SetVector(SplatRenderer.Props.VecScreenParams, screenPar);
            if (m_ViewTex != null)
            {
                m_Mpb.SetTexture(SplatRenderer.Props.SplatViewDataTex, m_ViewTex);
                m_Mpb.SetInteger(SplatRenderer.Props.SplatViewTexWidth, m_ViewTex.width);
                displayMat.SetTexture(SplatRenderer.Props.SplatViewDataTex, m_ViewTex);
                displayMat.SetInteger(SplatRenderer.Props.SplatViewTexWidth, m_ViewTex.width);
            }

            cmb.BeginSample(GaussianSplatRenderSystem.s_ProfDraw);
            // Identity, not any object's matrix: the vertex shader never reads the object transform
            // (it was applied on the compute side, before the splats were projected), and the
            // splats in this one draw come from many objects anyway.
            cmb.DrawProcedural(first.m_GpuIndexBuffer, Matrix4x4.identity, displayMat, 0,
                MeshTopology.Triangles, 6, total, m_Mpb);
            cmb.EndSample(GaussianSplatRenderSystem.s_ProfDraw);
        }

        void BuildAndSort(CommandBuffer cmb, Camera cam, int total)
        {
            cmb.BeginSample(s_ProfUnifiedSort);

            if (m_RegionUpload.Length < m_Regions.Count)
                m_RegionUpload = new Region[Mathf.NextPowerOfTwo(m_Regions.Count)];
            for (int i = 0; i < m_Regions.Count; ++i)
                m_RegionUpload[i] = m_Regions[i];
            if (m_RegionBuf == null || m_RegionBuf.count < m_RegionUpload.Length)
            {
                m_RegionBuf?.Dispose();
                m_RegionBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_RegionUpload.Length, 8)
                    { name = "GaussianUnifiedRegions" };
            }
            m_RegionBuf.SetData(m_RegionUpload, 0, 0, m_Regions.Count);

            // 32-bit keys are exact; the counting sort's bin space is 16 bits, so that path
            // quantizes (see the class remarks).
            bool exact = m_Radix != null;
            cmb.SetComputeBufferParam(m_Utilities, m_KernelBuildKeys, SplatRenderer.Props.UniRegions, m_RegionBuf);
            cmb.SetComputeBufferParam(m_Utilities, m_KernelBuildKeys, SplatRenderer.Props.SplatViewData, m_View);
            cmb.SetComputeBufferParam(m_Utilities, m_KernelBuildKeys, SplatRenderer.Props.SplatSortDistances, m_Keys);
            cmb.SetComputeBufferParam(m_Utilities, m_KernelBuildKeys, SplatRenderer.Props.SplatSortKeys, m_Order);
            cmb.SetComputeIntParam(m_Utilities, SplatRenderer.Props.UniRegionCount, m_Regions.Count);
            cmb.SetComputeIntParam(m_Utilities, SplatRenderer.Props.UniCount, total);
            cmb.SetComputeIntParam(m_Utilities, SplatRenderer.Props.UniViewCount, m_View.count);
            cmb.SetComputeIntParam(m_Utilities, SplatRenderer.Props.UniKeyBits, exact ? 32 : 16);
            cmb.SetComputeVectorParam(m_Utilities, SplatRenderer.Props.UniDepthParams, m_DepthParams);
            m_Utilities.GetKernelThreadGroupSizes(m_KernelBuildKeys, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_Utilities, m_KernelBuildKeys, (total + (int)gsX - 1) / (int)gsX, 1, 1);

            if (m_Radix != null)
            {
                var args = m_RadixArgs;
                args.count = (uint)total;
                args.inputKeys = m_Keys;
                args.inputValues = m_Order;
                args.keyBits = 32;
                m_Radix.Dispatch(cmb, args);
            }
            else
            {
                m_Counting.Dispatch(cmb, m_Keys, m_Order, total, m_CountingRes);
            }
            cmb.EndSample(s_ProfUnifiedSort);
        }

        // ------------------------------------------------------------------------------------
        // Key quantization range
        // ------------------------------------------------------------------------------------

        Vector4 m_DepthParams = new(0, 1, 0.01f, 0);

        /// <summary>
        /// Recompute the depth range the 16-bit key path quantizes over, from the visible objects'
        /// conservative camera-space bounds. Call once per pass, before drawing any group.
        /// </summary>
        internal void UpdateDepthRange(Camera cam, List<(SplatRenderer, MaterialPropertyBlock)> active)
        {
            Matrix4x4 worldToCam = cam.worldToCameraMatrix;
            float zMin = float.MaxValue, zMax = -float.MaxValue;
            for (int i = 0; i < active.Count; ++i)
            {
                if (!active[i].Item1.TryGetCamDepthRange(worldToCam, out float a, out float b))
                    continue;
                zMin = Mathf.Min(zMin, a);
                zMax = Mathf.Max(zMax, b);
            }
            // Camera space looks down -z, so the farthest point has the smallest z. Splats behind
            // the camera (z >= 0) are culled by CSCalcViewData anyway; clamping the distance keeps
            // the reciprocal finite and parks them at the near end, where they are drawn last and
            // then collapsed by the vertex shader.
            const float kMinDist = 0.01f;
            float dFar = Mathf.Max(-zMin, kMinDist);
            float dNear = Mathf.Max(-zMax, kMinDist);
            if (zMin > zMax || !(dFar > dNear))
            {
                // Nothing measurable (single flat object, or no valid bounds): any monotone mapping
                // will do, so keep the previous range rather than divide by zero.
                dFar = Mathf.Max(dNear * 2f, kMinDist * 2f);
            }
            float invFar = 1f / dFar;
            float invNear = 1f / dNear;
            m_DepthParams = new Vector4(invFar, 1f / Mathf.Max(invNear - invFar, 1e-9f), kMinDist, 0);
        }
    }
}
