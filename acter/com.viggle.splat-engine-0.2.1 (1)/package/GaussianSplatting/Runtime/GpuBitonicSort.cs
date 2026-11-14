// SPDX-License-Identifier: MIT
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Bitonic merge sort fallback for platforms without wave intrinsics (e.g. OpenGL ES),
    // where the DeviceRadixSort kernels are unsupported. Sorts (uint key, uint payload)
    // pairs ascending, in place — no support buffers needed. Requires the buffers to be
    // allocated at a power-of-two size (minimum kMinElems), with the key tail padded
    // with 0xFFFFFFFF so unused entries sort to the back. Kernels live in
    // SplatUtilities.compute.
    internal class GpuBitonicSort
    {
        const int kGroupSize = 128;             // must match BITONIC_GROUP in SplatUtilities.compute (GLES 3.1 workgroup floor)
        public const int kMinElems = kGroupSize * 2; // elements per group; also the minimum buffer size

        static readonly int s_PropCount = Shader.PropertyToID("_BitonicCount");
        static readonly int s_PropSize = Shader.PropertyToID("_BitonicSize");
        static readonly int s_PropStride = Shader.PropertyToID("_BitonicStride");
        static readonly int s_PropKeys = Shader.PropertyToID("_SplatSortDistances");
        static readonly int s_PropValues = Shader.PropertyToID("_SplatSortKeys");

        readonly ComputeShader m_CS;
        readonly int m_KernelSortLocal = -1;
        readonly int m_KernelMergeGlobal = -1;
        readonly int m_KernelMergeLocal = -1;
        readonly bool m_Valid;

        public bool Valid => m_Valid;

        public GpuBitonicSort(ComputeShader cs)
        {
            m_CS = cs;
            if (cs)
            {
                if (cs.HasKernel("CSBitonicSortLocal")) m_KernelSortLocal = cs.FindKernel("CSBitonicSortLocal");
                if (cs.HasKernel("CSBitonicMergeGlobal")) m_KernelMergeGlobal = cs.FindKernel("CSBitonicMergeGlobal");
                if (cs.HasKernel("CSBitonicMergeLocal")) m_KernelMergeLocal = cs.FindKernel("CSBitonicMergeLocal");
            }

            m_Valid = m_KernelSortLocal >= 0 && m_KernelMergeGlobal >= 0 && m_KernelMergeLocal >= 0;
            if (m_Valid &&
                (!cs.IsSupported(m_KernelSortLocal) || !cs.IsSupported(m_KernelMergeGlobal) || !cs.IsSupported(m_KernelMergeLocal)))
                m_Valid = false;
            if (!m_Valid)
                Debug.LogError($"GpuBitonicSort: kernels not found or not supported in {(cs ? cs.name : "<null>")} " +
                               $"({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName})");
        }

        // Buffer size that the sort needs for a given splat count.
        public static int PaddedCount(int count) => Mathf.Max(kMinElems, Mathf.NextPowerOfTwo(count));

        // keys/values must both have PaddedCount-sized capacity; sorts the whole buffer.
        public void Dispatch(CommandBuffer cmd, GraphicsBuffer keys, GraphicsBuffer values)
            => Dispatch(cmd, keys, values, keys.count);

        // Sorts only the first `count` elements (count must be a power of two >= kMinElems, e.g.
        // PaddedCount(K) for a LOD prefix of K with the [K..count) key tail sentinel-filled).
        // Elements beyond `count` are untouched. Cuts the O(n log^2 n) pass count when a LOD
        // driver draws only a prefix - the main sort saving available on the GLES fallback.
        public void Dispatch(CommandBuffer cmd, GraphicsBuffer keys, GraphicsBuffer values, int count)
        {
            int n = Mathf.Clamp(count, kMinElems, keys.count);
            Debug.Assert(m_Valid && n >= kMinElems && Mathf.IsPowerOfTwo(n) && values.count == keys.count);

            cmd.SetComputeIntParam(m_CS, s_PropCount, n);
            cmd.SetComputeBufferParam(m_CS, m_KernelSortLocal, s_PropKeys, keys);
            cmd.SetComputeBufferParam(m_CS, m_KernelSortLocal, s_PropValues, values);
            cmd.SetComputeBufferParam(m_CS, m_KernelMergeGlobal, s_PropKeys, keys);
            cmd.SetComputeBufferParam(m_CS, m_KernelMergeGlobal, s_PropValues, values);
            cmd.SetComputeBufferParam(m_CS, m_KernelMergeLocal, s_PropKeys, keys);
            cmd.SetComputeBufferParam(m_CS, m_KernelMergeLocal, s_PropValues, values);

            // All passes via the global kernel: one thread per compare-exchange, no shared
            // memory, no barriers. The shared-memory local kernels are faster but mis-execute
            // through ANGLE on some Android GLES drivers (keys sort, payload pairing breaks
            // within blocks), which scrambles local splat ordering.
            int globalGroups = (n / 2 + kGroupSize - 1) / kGroupSize;
            for (int size = 2; size <= n; size <<= 1)
            {
                cmd.SetComputeIntParam(m_CS, s_PropSize, size);
                for (int stride = size >> 1; stride >= 1; stride >>= 1)
                {
                    cmd.SetComputeIntParam(m_CS, s_PropStride, stride);
                    cmd.DispatchCompute(m_CS, m_KernelMergeGlobal, globalGroups, 1, 1);
                }
            }
        }
    }
}
