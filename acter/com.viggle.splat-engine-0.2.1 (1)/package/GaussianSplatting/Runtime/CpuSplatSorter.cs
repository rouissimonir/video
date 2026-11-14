// SPDX-License-Identifier: MIT
// CPU depth sort for the no-compute render path (WebGL2 / GLES 3.0), replacing the GPU
// radix/bitonic/counting sorts in SplatUnifiedSort. Same approach as web splat viewers
// (PlayCanvas, antimatter15/splat): quantize view-space depth and counting-sort -
// O(n), stable, allocation-free after construction. For skinned characters the depth uses each
// splat's DOMINANT bone only (an approximation; splat sorting is already approximate and
// throttled via sortNthFrame).
//
// Per bone the caller precomputes a "depth row": row 2 of (worldToCamera * localToWorld *
// boneMatrix * bindYaw), so per splat the depth is one dot product with the raw bind
// position. Unity's worldToCamera looks down -Z, so more-negative depth = farther;
// ascending order = far-to-near = the back-to-front draw order the splat shader's
// premultiplied "over" blend requires.
//
// The result is uploaded to a point-sampled R32F texture (draw slot -> splat index) that
// RenderGaussianSplatsNoCompute.shader reads - GLES 3.0 has no vertex SSBOs. Deliberately
// plain C# (no Burst: unsupported on WebGL, the only platform this runs on in production).
//
// This is the PER-OBJECT sorter, which is what runs when cross-object ordering is off. With it
// on, CpuGlobalSplatSorter replaces it outright: one order over every visible splat, which is
// not something a set of per-object permutations can be merged into cheaply.

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class CpuSplatSorter : IDisposable
    {
        readonly int m_Count;
        readonly int m_Buckets;
        NativeArray<int> m_Keys;
        NativeArray<int> m_Hist;
        NativeArray<float> m_Order; // texWidth * texHeight floats; slot -> splat index

        public Texture2D orderTexture { get; private set; }

        /// <summary>
        /// Buckets to quantise <paramref name="count"/> splats into: 2^clamp(round(log2(n/4)), 10, 20),
        /// the web engine's budget (playcanvas gsplat-sort-worker.js) - roughly four splats per bucket.
        /// </summary>
        /// <remarks>
        /// Scaling with n rather than fixing it at 65536 is not a precision tweak, it is a cost fix.
        /// The histogram is cleared and prefix-summed once per sort regardless of splat count, so a
        /// fixed 65536 cost about 13x the sort itself for a 5k-splat object - and a tiled environment
        /// capture is dozens of such objects, every frame. Resolution does not suffer: a few thousand
        /// splats spread over ONE object's depth range are nowhere near dense enough for 1024 buckets
        /// to tie meaningfully.
        /// </remarks>
        public static int BucketCountFor(int count)
        {
            int bits = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(Mathf.Max(count, 1) / 4f, 2f)), 10, 20);
            return 1 << bits;
        }

        public CpuSplatSorter(int count, int texWidth)
        {
            m_Count = count;
            m_Buckets = BucketCountFor(count);
            int texHeight = Mathf.Max(1, (count + texWidth - 1) / texWidth);
            m_Keys = new NativeArray<int>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_Hist = new NativeArray<int>(m_Buckets, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_Order = new NativeArray<float>(texWidth * texHeight, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            // Identity order until the first Sort (renderer may draw a frame before the
            // skinner has published its palette).
            for (int i = 0; i < count; ++i)
                m_Order[i] = i;
            orderTexture = new Texture2D(texWidth, texHeight, TextureFormat.RFloat, false, true)
            {
                name = "SplatNCOrder",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            orderTexture.SetPixelData(m_Order, 0);
            orderTexture.Apply(false, false);
        }

        /// <summary>
        /// depthRows: one float4 per bone when skinned (indexed by dominantBone), else a
        /// single row at index 0. Uploads the new order texture when done.
        /// </summary>
        public void Sort(NativeArray<float3> positions, NativeArray<ushort> dominantBone,
            float4[] depthRows, bool skinned)
        {
            int n = m_Count;
            if (n == 0 || positions.Length < n)
                return;

            // Pass 1: depth per splat + range.
            // Reuse m_Order as scratch for raw depths before it is overwritten by the scatter.
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < n; ++i)
            {
                float4 row = depthRows[skinned ? dominantBone[i] : 0];
                float3 p = positions[i];
                float d = row.x * p.x + row.y * p.y + row.z * p.z + row.w;
                if (float.IsNaN(d)) d = 0;
                m_Order[i] = d;
                if (d < mn) mn = d;
                if (d > mx) mx = d;
            }

            // Pass 2: quantize + histogram. Ascending depth = far-to-near (view-space z is
            // negative in front of the camera, more negative = farther). Buckets are uniform over
            // the range, which is fine HERE and only here: one object's splats span one object's
            // depth, so there is no empty air for the resolution to be wasted on. The global
            // sorter, whose range is the whole scene, has to distribute them by density instead.
            for (int i = 0; i < m_Buckets; ++i)
                m_Hist[i] = 0;
            float scale = mx > mn ? (m_Buckets - 1) / (mx - mn) : 0f;
            for (int i = 0; i < n; ++i)
            {
                int k = (int)((m_Order[i] - mn) * scale);
                m_Keys[i] = k;
                m_Hist[k]++;
            }

            // Prefix sum -> bucket start offsets.
            int sum = 0;
            for (int i = 0; i < m_Buckets; ++i)
            {
                int c = m_Hist[i];
                m_Hist[i] = sum;
                sum += c;
            }

            // Scatter (stable): slot -> splat index, as float for the R32F texture.
            for (int i = 0; i < n; ++i)
            {
                int k = m_Keys[i];
                m_Order[m_Hist[k]] = i;
                m_Hist[k]++;
            }

            orderTexture.SetPixelData(m_Order, 0);
            orderTexture.Apply(false, false);
        }

        public void Dispose()
        {
            if (m_Keys.IsCreated) m_Keys.Dispose();
            if (m_Hist.IsCreated) m_Hist.Dispose();
            if (m_Order.IsCreated) m_Order.Dispose();
            if (orderTexture != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(orderTexture);
                else UnityEngine.Object.DestroyImmediate(orderTexture);
                orderTexture = null;
            }
        }
    }
}
