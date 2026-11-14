// SPDX-License-Identifier: MIT
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Counting sort over the 16-bit depth keys, for platforms without wave intrinsics (OpenGL ES),
    // where DeviceRadixSort reports unsupported.
    //
    // Replaces GpuBitonicSort, which needs one dispatch per compare-exchange stage: k(k+1)/2 for
    // 2^k elements. A 67k-splat character pads to 131072 and costs 153 dispatches, per character,
    // per frame. That is dominated by dispatch and barrier overhead rather than GPU throughput,
    // which is why it does not improve on faster hardware, and why lowering splat quality barely
    // helps - the pass count is O(log^2 n), so dropping 20% of splats removes almost no passes.
    //
    // This is 7 dispatches of O(n) work, and needs no power-of-two padding, so the sort buffers
    // shrink to the exact splat count (131072 -> 67101 in that example).
    //
    // EXACT, not approximate: CSCalcDistances already quantizes depth to 16 bits, so 65536 bins
    // covers the whole key space. Ordering among equal keys differs from the bitonic result, but
    // both are arbitrary there and 16-bit quantization puts ties sub-millimetre apart.
    //
    // Requires SplatRenderer.s_Sort16BitKeys. With 32-bit keys there is no bounded bin
    // space and the caller keeps using the bitonic network.
    internal class GpuCountingSort
    {
        public const int kBins = 65536;      // must match COUNT_BINS in SplatUtilities.compute
        const int kBlocks = 256;             // must match COUNT_BLOCKS
        const int kGroupSize = 128;          // must match GROUP_SIZE

        static readonly int s_PropHist = Shader.PropertyToID("_CountHist");
        static readonly int s_PropBlockSum = Shader.PropertyToID("_CountBlockSum");
        static readonly int s_PropOut = Shader.PropertyToID("_CountOut");
        static readonly int s_PropNum = Shader.PropertyToID("_CountNum");
        static readonly int s_PropOutSize = Shader.PropertyToID("_CountOutSize");
        static readonly int s_PropKeys = Shader.PropertyToID("_SplatSortDistances");
        static readonly int s_PropValues = Shader.PropertyToID("_SplatSortKeys");

        readonly ComputeShader m_CS;
        readonly int m_KClear = -1, m_KHist = -1, m_KBlockSums = -1, m_KScan = -1,
                     m_KBinOffsets = -1, m_KScatter = -1, m_KCopyBack = -1;
        readonly bool m_Valid;

        public bool Valid => m_Valid;

        public GpuCountingSort(ComputeShader cs)
        {
            m_CS = cs;
            if (cs)
            {
                m_KClear = Find(cs, "CSCountClear");
                m_KHist = Find(cs, "CSCountHistogram");
                m_KBlockSums = Find(cs, "CSCountBlockSums");
                m_KScan = Find(cs, "CSCountScanBlockSums");
                m_KBinOffsets = Find(cs, "CSCountBinOffsets");
                m_KScatter = Find(cs, "CSCountScatter");
                m_KCopyBack = Find(cs, "CSCountCopyBack");
            }

            m_Valid = m_KClear >= 0 && m_KHist >= 0 && m_KBlockSums >= 0 && m_KScan >= 0 &&
                      m_KBinOffsets >= 0 && m_KScatter >= 0 && m_KCopyBack >= 0;
            if (m_Valid && !(cs.IsSupported(m_KClear) && cs.IsSupported(m_KHist) &&
                             cs.IsSupported(m_KBlockSums) && cs.IsSupported(m_KScan) &&
                             cs.IsSupported(m_KBinOffsets) && cs.IsSupported(m_KScatter) &&
                             cs.IsSupported(m_KCopyBack)))
                m_Valid = false;
            if (!m_Valid)
                Debug.LogWarning($"GpuCountingSort: kernels missing or unsupported in " +
                                 $"{(cs ? cs.name : "<null>")} ({SystemInfo.graphicsDeviceType}, " +
                                 $"{SystemInfo.graphicsDeviceName}); falling back to the bitonic sort");
        }

        static int Find(ComputeShader cs, string name) => cs.HasKernel(name) ? cs.FindKernel(name) : -1;

        // Scratch the sort needs. Sized per renderer rather than shared globally: sorts are
        // recorded into one command buffer and do execute in order, but this project has already
        // been bitten twice by state shared across dispatches behaving differently on ANGLE, and
        // 257KB of scratch is not worth re-litigating that.
        public struct Resources
        {
            public GraphicsBuffer hist;      // kBins
            public GraphicsBuffer blockSum;  // kBlocks
            public GraphicsBuffer outValues; // payload scatter target, splat count

            public static Resources Load(int count)
            {
                var t = GraphicsBuffer.Target.Structured;
                return new Resources
                {
                    hist = new GraphicsBuffer(t, kBins, 4) { name = "GaussianCountHist" },
                    blockSum = new GraphicsBuffer(t, kBlocks, 4) { name = "GaussianCountBlockSum" },
                    outValues = new GraphicsBuffer(t, Mathf.Max(1, count), 4) { name = "GaussianCountOut" },
                };
            }

            public void Dispose()
            {
                hist?.Dispose(); blockSum?.Dispose(); outValues?.Dispose();
                hist = blockSum = outValues = null;
            }

            public bool IsCreated => hist != null && blockSum != null && outValues != null;
        }

        // Sorts the first `count` (key, payload) pairs ascending by key, leaving the sorted payload
        // in `values`. `keys` is left as-is: CSCalcDistances regenerates it from `values` every
        // frame, so permuting it would be wasted work.
        public void Dispatch(CommandBuffer cmd, GraphicsBuffer keys, GraphicsBuffer values,
                             int count, in Resources res)
        {
            Debug.Assert(m_Valid && res.IsCreated);
            if (count <= 0)
                return;

            cmd.SetComputeIntParam(m_CS, s_PropNum, count);
            cmd.SetComputeIntParam(m_CS, s_PropOutSize, res.outValues.count);

            // Clear covers the bins, which dwarf the block sums, so it is sized off kBins.
            Bind(cmd, m_KClear, keys, values, res);
            cmd.DispatchCompute(m_CS, m_KClear, Groups(kBins), 1, 1);

            Bind(cmd, m_KHist, keys, values, res);
            cmd.DispatchCompute(m_CS, m_KHist, Groups(count), 1, 1);

            Bind(cmd, m_KBlockSums, keys, values, res);
            cmd.DispatchCompute(m_CS, m_KBlockSums, Groups(kBlocks), 1, 1);

            // Single-threaded by design: 256 serial iterations, no barriers to get wrong.
            Bind(cmd, m_KScan, keys, values, res);
            cmd.DispatchCompute(m_CS, m_KScan, 1, 1, 1);

            Bind(cmd, m_KBinOffsets, keys, values, res);
            cmd.DispatchCompute(m_CS, m_KBinOffsets, Groups(kBlocks), 1, 1);

            Bind(cmd, m_KScatter, keys, values, res);
            cmd.DispatchCompute(m_CS, m_KScatter, Groups(count), 1, 1);

            Bind(cmd, m_KCopyBack, keys, values, res);
            cmd.DispatchCompute(m_CS, m_KCopyBack, Groups(count), 1, 1);
        }

        static int Groups(int n) => (n + kGroupSize - 1) / kGroupSize;

        // Every kernel gets every buffer. Compute buffer bindings are per-kernel in Unity, and
        // binding them all uniformly costs nothing at dispatch time while removing a whole class of
        // "forgot to bind X on kernel Y" bug - which on this hardware surfaces as a page fault
        // rather than an error.
        void Bind(CommandBuffer cmd, int kernel, GraphicsBuffer keys, GraphicsBuffer values,
                  in Resources res)
        {
            cmd.SetComputeBufferParam(m_CS, kernel, s_PropKeys, keys);
            cmd.SetComputeBufferParam(m_CS, kernel, s_PropValues, values);
            cmd.SetComputeBufferParam(m_CS, kernel, s_PropHist, res.hist);
            cmd.SetComputeBufferParam(m_CS, kernel, s_PropBlockSum, res.blockSum);
            cmd.SetComputeBufferParam(m_CS, kernel, s_PropOut, res.outValues);
        }
    }
}
