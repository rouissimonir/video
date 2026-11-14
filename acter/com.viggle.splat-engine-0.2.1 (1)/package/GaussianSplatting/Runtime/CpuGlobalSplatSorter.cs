// SPDX-License-Identifier: MIT
// ONE depth order over every visible splat, spanning objects, for the no-compute render path
// (WebGL2 / GLES 3.0). The counterpart of the GPU sorters SplatUnifiedSort drives on the compute
// path, and the reason a scene of many splat objects composites correctly rather than in
// arbitrary per-object order.
//
// It can be a CPU sort with no GPU readback because the depth of a splat is already one dot
// product: the caller precomputes, per bone, row 2 of (worldToCamera * localToWorld * boneMatrix),
// so a splat's view-space depth is that row dotted with its raw bind position (see
// SplatRenderer.BuildNoComputeDepthRows). Those depths are in the ONE space every object shares,
// so objects can be appended one after another and sorted together. Unity's worldToCamera looks
// down -Z, so ascending depth = far-to-near = the back-to-front order the "over" blend needs.
//
// ADAPTIVE, NON-UNIFORM BUCKETS, ported from the web engine's sort worker
// (playcanvas/src/scene/gsplat/gsplat-sort-worker.js). Two things change once one range spans the
// whole scene instead of one object:
//
//   - Bucket COUNT scales with the splat count, 2^clamp(round(log2(n/4)), 10, 20). A fixed 65536
//     was not just imprecise at the top end, it was ruinous at the bottom: clearing the histogram
//     cost more than sorting a few thousand splats, and the old per-object sorter paid that clear
//     once PER OBJECT - dozens of them per environment capture, every frame.
//   - Bucket WIDTH follows where splats actually are. Uniform buckets over a whole room spend
//     their resolution on empty air; a coarse density histogram over the objects' own depth
//     spans (built for free while the depths are computed) says where the mass is, and the
//     buckets are handed out in proportion. A character standing in a large capture therefore
//     still resolves to a fraction of a splat, which is exactly where uniform quantisation over
//     a scene-wide range would have failed.
//
// The result is uploaded to a point-sampled R32F texture (draw slot -> GLOBAL index into the
// shared view-data textures) that RenderGaussianSplatsNoCompute.shader's cross-object pass reads
// - GLES 3.0 has no vertex SSBOs. Deliberately plain C# (no Burst: unsupported on WebGL, the only
// platform this runs on in production).

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public sealed class CpuGlobalSplatSorter : IDisposable
    {
        // Density resolution: how many equal slices of the scene's depth range the bucket budget
        // is handed out over. 32 is the web engine's number and is not a tuning knob so much as a
        // floor - the point is only to notice "most of the splats are in this tenth of the range",
        // and the non-uniformity within a slice is then absorbed by the buckets themselves.
        const int kDensityBins = 32;

        // One appended object: where its splats landed in the depth array, where they live in the
        // shared view textures, and the depth span they actually occupy. The span is the density
        // histogram's input - exact, and free, because it falls out of the depth pass.
        struct Run
        {
            public int start;
            public int count;
            public int viewBase;
            public float depthMin;
            public float depthMax;
        }

        readonly List<Run> m_Runs = new();

        int m_Capacity;                 // splats the per-splat arrays are sized for
        int m_Count;                    // splats appended into the CURRENT group
        int m_OutCursor;                // order slots written so far, across every group
        float m_DepthMin, m_DepthMax;

        NativeArray<float> m_Depth;
        NativeArray<int> m_Key;
        NativeArray<float> m_Order;     // texWidth * texHeight floats; slot -> global view index
        NativeArray<int> m_Hist;
        // Bucket allocation per density bin, and the running total in front of each. Rebuilt every
        // sort; tiny, so they are plain arrays rather than native memory.
        readonly float[] m_BinMass = new float[kDensityBins];
        readonly int[] m_BinBuckets = new int[kDensityBins];
        readonly int[] m_BinBase = new int[kDensityBins];

        /// <summary>Draw slot -> global index into the shared view textures. Null until the first
        /// <see cref="SetCapacity"/>.</summary>
        public Texture2D orderTexture { get; private set; }

        /// <summary>How many order slots the last <see cref="Begin"/>..<see cref="Upload"/> filled.</summary>
        public int sortedCount { get; private set; }

        /// <summary>Splats the arrays and the order texture are currently sized for.</summary>
        public int capacity => m_Capacity;

        /// <summary>
        /// Size every buffer for <paramref name="capacity"/> splats. Idempotent; only reallocates
        /// when the number actually changes, so the caller can drive it from a layout that grows
        /// with headroom and shrinks on hysteresis without checking first.
        /// </summary>
        public bool SetCapacity(int capacity, int texWidth)
        {
            capacity = Mathf.Max(capacity, 1);
            if (m_Capacity == capacity && orderTexture != null && orderTexture.width == texWidth)
                return true;

            Release();
            int texHeight = Mathf.Max(1, (capacity + texWidth - 1) / texWidth);
            if (texHeight > SystemInfo.maxTextureSize)
                return false;

            m_Capacity = capacity;
            m_Depth = new NativeArray<float>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_Key = new NativeArray<int>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            // Identity, not undefined: a draw recorded before the first sort (or one whose sort was
            // skipped because nothing moved) still reads every slot it covers.
            m_Order = new NativeArray<float>(texWidth * texHeight, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < m_Order.Length; ++i)
                m_Order[i] = i;
            orderTexture = new Texture2D(texWidth, texHeight, TextureFormat.RFloat, false, true)
            {
                name = "SplatNCGlobalOrder",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            orderTexture.SetPixelData(m_Order, 0);
            orderTexture.Apply(false, false);
            sortedCount = 0;
            return true;
        }

        /// <summary>
        /// Start a new frame's ordering. One frame may hold several render-order groups, which are
        /// ordered independently and laid end to end in the same order texture - see
        /// <see cref="EndGroup"/>.
        /// </summary>
        public void Begin()
        {
            m_OutCursor = 0;
            BeginGroup();
        }

        void BeginGroup()
        {
            m_Runs.Clear();
            m_Count = 0;
            m_DepthMin = float.MaxValue;
            m_DepthMax = float.MinValue;
        }

        /// <summary>
        /// Append one object's splats. <paramref name="depthRows"/> is one float4 per bone when
        /// skinned (indexed by <paramref name="dominantBone"/>), else a single row at index 0 -
        /// see <c>SplatRenderer.BuildNoComputeDepthRows</c>. <paramref name="viewBase"/> is the
        /// object's first slot in the shared view textures, which is what the order texture will
        /// end up holding.
        /// </summary>
        public void AddObject(int viewBase, NativeArray<float3> positions, NativeArray<ushort> dominantBone,
            float4[] depthRows, bool skinned, int count)
        {
            if (count <= 0 || depthRows == null || depthRows.Length == 0 || !positions.IsCreated)
                return;
            count = Mathf.Min(count, positions.Length);
            if (skinned && (!dominantBone.IsCreated || dominantBone.Length < count))
                skinned = false;    // no bone map to index the rows with; fall back to the shared row
            if (m_OutCursor + m_Count + count > m_Capacity)
                return;             // layout is stale for one more frame; the caller resizes next pass

            int start = m_Count;
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < count; ++i)
            {
                float4 row = depthRows[skinned ? dominantBone[i] : 0];
                float3 p = positions[i];
                float d = row.x * p.x + row.y * p.y + row.z * p.z + row.w;
                if (float.IsNaN(d)) d = 0;
                m_Depth[start + i] = d;
                if (d < mn) mn = d;
                if (d > mx) mx = d;
            }
            m_Count = start + count;
            if (mn < m_DepthMin) m_DepthMin = mn;
            if (mx > m_DepthMax) m_DepthMax = mx;
            m_Runs.Add(new Run { start = start, count = count, viewBase = viewBase, depthMin = mn, depthMax = mx });
        }

        /// <summary>
        /// Order everything appended since the last group boundary into the slots starting at
        /// <paramref name="slotBase"/>, and open the next group. Returns how many slots it filled.
        /// </summary>
        /// <remarks>
        /// Groups exist because renderOrder is a hard layering control: order 0 draws entirely
        /// beneath order 1, so their splats must not interleave. They share ONE order texture,
        /// laid end to end, and that is not an optimisation - the texture is uploaded from the CPU
        /// at command-RECORD time, so a second upload would rewrite what the first group's
        /// already-recorded draw is going to read. One upload, disjoint slot ranges, one draw each.
        /// </remarks>
        public int EndGroup(out int slotBase)
        {
            slotBase = m_OutCursor;
            int n = m_Count;
            if (n <= 0)
                return 0;

            int bucketCount = BuildBuckets(n);

            // Quantise + histogram. Ascending key = ascending depth = far-to-near (view-space z is
            // negative in front of the camera, more negative = farther).
            if (!m_Hist.IsCreated || m_Hist.Length < bucketCount)
            {
                if (m_Hist.IsCreated) m_Hist.Dispose();
                m_Hist = new NativeArray<int>(bucketCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            for (int i = 0; i < bucketCount; ++i)
                m_Hist[i] = 0;

            float range = m_DepthMax - m_DepthMin;
            float binScale = range > 0f ? kDensityBins / range : 0f;
            for (int i = 0; i < n; ++i)
            {
                // Position within the density grid: whole part picks the bin, fraction picks the
                // bucket inside the slice that bin was allotted.
                float f = (m_Depth[i] - m_DepthMin) * binScale;
                int bin = (int)f;
                if (bin < 0) bin = 0;
                else if (bin >= kDensityBins) bin = kDensityBins - 1;
                int k = m_BinBase[bin] + (int)(m_BinBuckets[bin] * (f - bin));
                if (k < 0) k = 0;
                else if (k >= bucketCount) k = bucketCount - 1;
                m_Key[i] = k;
                m_Hist[k]++;
            }

            // Prefix sum -> bucket start offsets.
            int sum = 0;
            for (int i = 0; i < bucketCount; ++i)
            {
                int c = m_Hist[i];
                m_Hist[i] = sum;
                sum += c;
            }

            // Scatter (stable): slot -> GLOBAL view index, as float for the R32F texture. Walking
            // the runs rather than a per-splat index array is what keeps this at two arrays: the
            // global index is (run base + offset within the run), and the runs are contiguous.
            for (int r = 0; r < m_Runs.Count; ++r)
            {
                Run run = m_Runs[r];
                for (int i = 0; i < run.count; ++i)
                {
                    int k = m_Key[run.start + i];
                    m_Order[slotBase + m_Hist[k]] = run.viewBase + i;
                    m_Hist[k]++;
                }
            }

            m_OutCursor = slotBase + n;
            BeginGroup();
            return n;
        }

        /// <summary>Publish every group's slots. Call once, after the last <see cref="EndGroup"/>.</summary>
        public int Upload()
        {
            if (m_OutCursor > 0)
            {
                orderTexture.SetPixelData(m_Order, 0);
                orderTexture.Apply(false, false);
            }
            return sortedCount = m_OutCursor;
        }

        /// <summary>
        /// Hand out the bucket budget across the density bins, and return the budget. After this
        /// m_BinBuckets[b] is how many buckets bin b owns and m_BinBase[b] is the total in front
        /// of it, which together turn a depth into a key in two lookups.
        /// </summary>
        int BuildBuckets(int n)
        {
            // 2^clamp(round(log2(n/4)), 10, 20), the web engine's budget: roughly four splats per
            // bucket, floored at 1024 so a tiny scene still separates and capped at ~1M so the
            // histogram never costs more than the sort.
            int bits = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(Mathf.Max(n, 1) / 4f, 2f)), 10, 20);
            // One spare bucket at the top: the key of a splat at the very end of a bin's slice
            // approaches, but never reaches, the next bin's base - except at the last bin, where
            // rounding can land exactly on the total.
            int bucketCount = (1 << bits) + 1;

            float range = m_DepthMax - m_DepthMin;
            if (!(range > 0f))
            {
                // Every splat at the same depth: any assignment will do, and dividing by the range
                // would not. One bin owns everything.
                for (int b = 0; b < kDensityBins; ++b) { m_BinBuckets[b] = 0; m_BinBase[b] = 0; }
                return bucketCount;
            }

            // Density: each object's splats spread evenly over the bins its own depth span covers.
            // Mass-conserving on purpose - an object spanning many bins should not outweigh a
            // compact one of the same splat count, it should be thinner.
            for (int b = 0; b < kDensityBins; ++b)
                m_BinMass[b] = 0f;
            float invRange = kDensityBins / range;
            for (int r = 0; r < m_Runs.Count; ++r)
            {
                Run run = m_Runs[r];
                int b0 = Mathf.Clamp((int)((run.depthMin - m_DepthMin) * invRange), 0, kDensityBins - 1);
                int b1 = Mathf.Clamp((int)((run.depthMax - m_DepthMin) * invRange), b0, kDensityBins - 1);
                float share = run.count / (float)(b1 - b0 + 1);
                for (int b = b0; b <= b1; ++b)
                    m_BinMass[b] += share;
            }

            float total = 0f;
            for (int b = 0; b < kDensityBins; ++b)
                total += m_BinMass[b];
            int cursor = 0;
            for (int b = 0; b < kDensityBins; ++b)
            {
                // Truncation only ever leaves buckets unspent, never overspends - which is why the
                // key clamp in Sort is a guard rather than load-bearing.
                m_BinBuckets[b] = total > 0f ? (int)(m_BinMass[b] / total * (bucketCount - 1)) : 0;
                m_BinBase[b] = cursor;
                cursor += m_BinBuckets[b];
            }
            return bucketCount;
        }

        void Release()
        {
            if (m_Depth.IsCreated) m_Depth.Dispose();
            if (m_Key.IsCreated) m_Key.Dispose();
            if (m_Order.IsCreated) m_Order.Dispose();
            if (orderTexture != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(orderTexture);
                else UnityEngine.Object.DestroyImmediate(orderTexture);
                orderTexture = null;
            }
            m_Capacity = 0;
            m_Count = 0;
            m_OutCursor = 0;
            sortedCount = 0;
            m_Runs.Clear();
        }

        public void Dispose()
        {
            Release();
            if (m_Hist.IsCreated) m_Hist.Dispose();
        }
    }
}
