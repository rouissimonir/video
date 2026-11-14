// SPDX-License-Identifier: MIT
// Decodes a Niantic/Scaniverse .spz capture and packs it straight into the GPU layout the
// renderer reads. Runs at LOAD time, which is why it is here and not in an importer.
//
// WHY THIS LIVES IN THE JOBS ASSEMBLY
//
// Same two reasons as SplatSkinningJobs, and they are not negotiable: Burst compiles jobs ahead
// of time by scanning statically referenced assemblies, so a [BurstCompile] job in an assembly
// that is loaded dynamically is never compiled and its Execute falls back to interpretation; and
// scheduling a job permanently registers its Execute with the native layer, pinning the assembly
// it lives in. Read the header of SplatSkinningJobs.cs for the full story.
//
// The rule that follows is the same one: the calling assembly never names a job type and never
// calls Schedule. It calls Build below, which schedules and completes within the call, so no
// JobHandle and no job type crosses the boundary.
//
// Every job here declares its inputs as flat NativeArray fields rather than taking the tidy
// SpzPackedData bundle, deliberately: the job safety system's guarantees are attached to
// container fields it can see, and burying them one struct deep is not a readability win worth
// betting a player build on.
//
// NO INTERMEDIATE SPLAT ARRAY, ON PURPOSE
//
// The import-time packer this replaces decoded the whole file into a 248-byte-per-splat
// InputSplatData array first. At load time that is not affordable: the 1.92M-splat capture this
// was written against would want 476MB of transient memory before a single byte was packed. So
// nothing is materialised. Every pass - bounds, Morton keys, chunk bounds, encode - decodes the
// packed source again on the fly. Decoding is a handful of integer ops per splat and Burst
// vectorises it; the memory saved is worth far more than the arithmetic spent.
//
// SPZ format, version 2: a 16-byte header, then six sections in this order, each tightly packed
// with one record per splat - positions (3x 24-bit fixed point), alpha (u8), colour (3x u8),
// scale (3x u8), rotation (3x u8, w reconstructed), and 3 bytes per spherical-harmonic
// coefficient. The number of SH coefficients follows the file's degree: 0, 3, 8 or 15.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Viggle.SplatEngine.Jobs
{
    /// <summary>
    /// The packed sections of an .spz file, as read off the decompressed stream. Plain byte
    /// arrays rather than a decoded splat list: see the header.
    /// </summary>
    internal struct SpzPackedData
    {
        public NativeArray<byte> pos;      // 9 bytes/splat
        public NativeArray<byte> alpha;    // 1
        public NativeArray<byte> color;    // 3
        public NativeArray<byte> scale;    // 3
        public NativeArray<byte> rot;      // 3
        public NativeArray<byte> sh;       // 3 * shCoeffs, empty at degree 0
        public int splatCount;
        public int shCoeffs;               // 0, 3, 8 or 15 - what the file actually carries
        public float fractScale;           // 1 / (1 << fractionalBits)
    }

    /// <summary>
    /// The packed GPU blobs to fill. Every one of them is uint-addressed, which is not a detail:
    /// this assembly compiles without unsafe code, so writes go through typed indices rather than
    /// byte pointers. The formats the caller picks are all 4-byte aligned, so it costs nothing.
    /// </summary>
    internal struct SpzPackedOutput
    {
        public NativeArray<uint> pos;      // Norm11: 1 uint/splat
        public NativeArray<uint> other;    // rot 10.10.10.2 + scale Norm11: 2 uints/splat
        public NativeArray<uint> color;    // Norm8x4: 1 uint/texel, Morton-16x16 swizzled
        public NativeArray<uint> sh;       // Norm6: 8 uints/splat, or a stub for a degree-0 file
        public NativeArray<SplatChunkInfo> chunks;
    }

    internal static class SplatCaptureJobs
    {
        // Splats per chunk. Mirrors SplatAsset.kChunkSize and kChunkSize in GaussianSplatting.hlsl;
        // all three must agree or the shader dequantizes against the wrong chunk.
        const int kChunkSize = 256;

        // Colour texture width. Must match SplatAsset.kTextureWidth and kTexWidth in the shader.
        const int kTexWidth = 2048;

        // Morton bits per axis for the spatial reorder below. The import-time packer used 21,
        // which needs a 63-bit key; 10 bits is a 30-bit key that fits a uint, halves the sort's
        // memory traffic, and is still far finer than the reorder needs - on a 1024^3 grid a
        // capture puts vastly fewer than 256 splats in one cell, and cell size is all that
        // decides how tight a chunk's bounds come out.
        const float kMortonScale = (1 << 10) - 1;

        /// <summary>
        /// Decode <paramref name="src"/> into <paramref name="dst"/>. Schedules and completes
        /// within the call.
        /// </summary>
        /// <param name="boundsMin">Capture bounds, computed here because the chunk encoding needs
        /// them anyway and the .spz header does not carry them.</param>
        internal static void Build(SpzPackedData src, SpzPackedOutput dst,
                                   out float3 boundsMin, out float3 boundsMax)
        {
            int n = src.splatCount;

            // 1. Bounds. One pass over the position section; nothing else needs storing.
            var bounds = new NativeArray<float3>(2, Allocator.TempJob);
            new CalcBoundsJob { packedPos = src.pos, fractScale = src.fractScale, count = n, bounds = bounds }
                .Schedule().Complete();
            boundsMin = bounds[0];
            boundsMax = bounds[1];
            bounds.Dispose();

            // 2. Spatial reorder. Chunks quantize against their own bounds, so splats that share
            //    a chunk should be near each other; a capture's file order is arbitrary, and
            //    without this every chunk's bounds would be the whole capture and Norm11 would
            //    quantize positions against the full extent instead of a few centimetres of it.
            //    Only the permutation is produced - the source is never physically reordered.
            var order = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            BuildMortonOrder(src, boundsMin, boundsMax, order);

            // 3. Per-chunk min/max, in the same transformed space the shader interpolates in.
            new ChunkBoundsJob
            {
                packedPos = src.pos, packedScale = src.scale, packedAlpha = src.alpha,
                packedCol = src.color, packedSh = src.sh,
                fractScale = src.fractScale, shCoeffs = src.shCoeffs, splatCount = n,
                order = order, chunks = dst.chunks
            }.Schedule(dst.chunks.Length, 8).Complete();

            // 4. Encode. The three outputs are independent, so they run together and join once.
            var handles = new NativeArray<JobHandle>(3, Allocator.Temp);
            handles[0] = new EncodePosOtherJob
            {
                packedPos = src.pos, packedScale = src.scale, packedRot = src.rot,
                fractScale = src.fractScale,
                order = order, chunks = dst.chunks, outPos = dst.pos, outOther = dst.other
            }.Schedule(n, 8192);

            handles[1] = new EncodeColorJob
            {
                packedCol = src.color, packedAlpha = src.alpha,
                order = order, chunks = dst.chunks, outColor = dst.color
            }.Schedule(n, 8192);

            handles[2] = src.shCoeffs > 0
                ? new EncodeShJob
                {
                    packedSh = src.sh, shCoeffs = src.shCoeffs,
                    order = order, chunks = dst.chunks, outSh = dst.sh
                }.Schedule(n, 8192)
                : default;

            JobHandle.CompleteAll(handles);
            handles.Dispose();

            order.Dispose();
        }

        /// <summary>
        /// Stride-sampled splat centres and colours, for a project-window thumbnail. Deliberately
        /// not a job: it touches one splat in a hundred, so scheduling would cost more than the
        /// work. Fills as much of <paramref name="positions"/> as the stride reaches and returns
        /// how many entries it wrote.
        /// </summary>
        internal static int SamplePoints(SpzPackedData src, int stride,
                                         Vector3[] positions, Color32[] colors)
        {
            int w = 0;
            for (int i = 0; i < src.splatCount && w < positions.Length; i += stride, w++)
            {
                float3 p = DecodePos(src.pos, i, src.fractScale);
                positions[w] = new Vector3(p.x, p.y, p.z);
                float3 c = math.saturate(DecodeColor(src.color, i));
                colors[w] = new Color32(ToByte(c.x), ToByte(c.y), ToByte(c.z), src.alpha[i]);
            }
            return w;
        }

        /// <summary>
        /// Decodes a whole capture into plain per-splat arrays, for the no-compute render path
        /// (SplatNoComputeData). The packing jobs above decode straight into the GPU layout, which
        /// is the right thing everywhere compute exists; GLES 3.0 cannot read that layout at all,
        /// so it needs the decoded values instead.
        ///
        /// Not a job, matching SamplePoints: it runs once at load and the caller needs the result
        /// in the same call. Every decoder it uses is the one the packing jobs use, so the two
        /// paths cannot disagree about what a capture contains.
        ///
        /// Rotation is the UNPACKED quaternion, deliberately not DecodeRot's
        /// PackSmallest3Rotation output: that packing is a storage format for the GPU blob, and
        /// the no-compute shader wants the plain xyzw its covariance maths expects.
        /// </summary>
        internal static void DecodeToArrays(SpzPackedData src,
                                            NativeArray<float3> positions,
                                            NativeArray<quaternion> rotations,
                                            NativeArray<float3> scales,
                                            NativeArray<Color32> colors,
                                            NativeArray<float> opacities)
        {
            int n = src.splatCount;
            float maxAbs = 0.0f;
            for (int i = 0; i < n; i++)
            {
                float3 p = DecodePos(src.pos, i, src.fractScale);
                maxAbs = math.max(maxAbs, math.cmax(math.abs(p)));
                positions[i] = p;
                scales[i] = DecodeScale(src.scale, i);

                int b = i * 3;
                float3 xyz = new float3(src.rot[b], src.rot[b + 1], src.rot[b + 2]) * (1.0f / 127.5f) - 1.0f;
                float w = math.sqrt(math.max(0.0f, 1.0f - math.lengthsq(xyz)));
                rotations[i] = math.normalize(new quaternion(xyz.x, xyz.y, xyz.z, w));

                float3 c = math.saturate(DecodeColor(src.color, i));
                byte a = src.alpha[i];
                colors[i] = new Color32(ToByte(c.x), ToByte(c.y), ToByte(c.z), a);
                opacities[i] = a / 255.0f;
            }

            // A tripwire, not instrumentation: silent unless the decode is impossible.
            //
            // The 24-bit fixed-point format cannot express a magnitude above
            // 2^23 * fractScale, so anything beyond that is not a strange capture, it is a
            // broken reader - and this path is the one that had exactly that. Positions came
            // back as the low byte alone plus the sign extension, which lands negatives near
            // -2^24 * fractScale: twice the representable bound, so this would have caught it
            // on the first frame instead of it surfacing as an unexplained smear.
            //
            // Worth a permanent check because the failure is silent by nature. Nothing
            // downstream validates positions; wrong ones simply render wrong. Cheap, too: the
            // bound is folded into the decode loop above, not a second pass.
            //
            // Deliberately not routed through SplatLog - the Jobs assembly does not reference
            // the runtime one, and a correctness alarm should not be silenceable by a log level.
            if (n > 0)
            {
                float limit = 8388608.0f * src.fractScale;   // 2^23 * fractScale
                if (maxAbs > limit)
                    Debug.LogError($"[SplatDecode] decoded positions reach {maxAbs:F3}, beyond the " +
                                   $"{limit:F3} this file's 24-bit fixed point can express " +
                                   $"(fractScale=1/{(int)(1.0f / src.fractScale)}, {n:N0} splats). " +
                                   "The capture reader is wrong, not the capture - splats will " +
                                   "render clustered and smeared.");
            }
        }

        static byte ToByte(float v) => (byte)math.clamp((int)(v * 255.5f), 0, 255);

        static void BuildMortonOrder(SpzPackedData src, float3 boundsMin, float3 boundsMax,
                                     NativeArray<int> order)
        {
            int n = src.splatCount;
            var keys = new NativeArray<uint>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var keysTmp = new NativeArray<uint>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var orderTmp = new NativeArray<int>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            // A degenerate axis - a perfectly flat capture, or a single splat - would divide by
            // zero and hand the cast a NaN, whose conversion to uint is undefined. Floor the extent.
            float3 inv = 1.0f / math.max(boundsMax - boundsMin, 1e-6f);

            new MortonKeysJob
            {
                packedPos = src.pos, fractScale = src.fractScale,
                boundsMin = boundsMin, invBoundsSize = inv,
                keys = keys, order = order
            }.Schedule(n, 8192).Complete();

            new RadixSortJob { keys = keys, keysTmp = keysTmp, order = order, orderTmp = orderTmp }
                .Schedule().Complete();

            keys.Dispose();
            keysTmp.Dispose();
            orderTmp.Dispose();
        }

        // ---- decode helpers, shared by every pass -------------------------------------------
        //
        // These mirror the reference SPZ decoder (github.com/nianticlabs/spz). Changing any
        // constant here changes what every already-exported capture looks like.

        // Reassembles one 24-bit little-endian fixed-point coordinate.
        //
        // Written the long way ON PURPOSE. The previous form,
        //
        //     int fx = p[o] | (p[o + 1] << 8) | (p[o + 2] << 16);
        //     fx |= (fx & 0x800000) != 0 ? -16777216 : 0;
        //
        // is correct as C# and correct everywhere Burst compiles it - which is every caller in
        // this file except DecodeToArrays, the one that feeds the no-compute path. Read back off
        // the GPU in a WebGL2 player, the positions that path produced were
        //
        //     (p[o] - (bit23 ? 2^24 : 0)) / (1 << fractionalBits)
        //
        // i.e. the two shifted bytes had been dropped while the sign extension still fired: every
        // positive coordinate collapsed into a 1/16-unit band and every negative one landed at
        // about -4096. Explicit int casts and a subtract, with no compound-assign of a negative
        // constant, express the same arithmetic without relying on that fold.
        static float Fixed24(in NativeArray<byte> p, int o)
        {
            int b0 = p[o], b1 = p[o + 1], b2 = p[o + 2];
            int fx = b0 + (b1 << 8) + (b2 << 16);
            if ((b2 & 0x80) != 0) fx -= 0x1000000;   // sign-extend the 24-bit value
            return fx;
        }

        static float3 DecodePos(in NativeArray<byte> packedPos, int i, float fractScale)
        {
            int b = i * 9;
            return new float3(Fixed24(packedPos, b), Fixed24(packedPos, b + 3), Fixed24(packedPos, b + 6)) * fractScale;
        }

        static float3 DecodeScale(in NativeArray<byte> packedScale, int i)
        {
            int b = i * 3;
            float3 logScale = new float3(packedScale[b], packedScale[b + 1], packedScale[b + 2]) / 16.0f - 10.0f;
            return GaussianUtils.LinearScale(logScale);
        }

        static float4 DecodeRot(in NativeArray<byte> packedRot, int i)
        {
            int b = i * 3;
            float3 xyz = new float3(packedRot[b], packedRot[b + 1], packedRot[b + 2]) * (1.0f / 127.5f) - 1.0f;
            float w = math.sqrt(math.max(0.0f, 1.0f - math.lengthsq(xyz)));
            return GaussianUtils.PackSmallest3Rotation(math.normalize(new float4(xyz, w)));
        }

        static float3 DecodeColor(in NativeArray<byte> packedCol, int i)
        {
            int b = i * 3;
            float3 col = new float3(packedCol[b], packedCol[b + 1], packedCol[b + 2]) / 255.0f - 0.5f;
            return GaussianUtils.SH0ToColor(col / 0.15f);
        }

        // The k'th SH coefficient of splat i, or zero when the file's degree does not reach it.
        // The degree check is load-bearing: a degree-0 capture has a zero-length SH section, and
        // reading all fifteen coefficients regardless walked off the end of it.
        static float3 DecodeSH(in NativeArray<byte> packedSh, int i, int k, int shCoeffs)
        {
            if (k >= shCoeffs)
                return float3.zero;
            int b = (i * shCoeffs + k) * 3;
            return (new float3(packedSh[b], packedSh[b + 1], packedSh[b + 2]) - 128.0f) / 128.0f;
        }

        // The two transforms the chunk encoding happens in, undone by the shader on the way back
        // out (GaussianSplatting.hlsl squares the scale three times after the lerp, and runs
        // col.a through InvSquareCentered01). Quantizing in this space spends the bits where the
        // eye is.
        static float3 ChunkScale(float3 linearScale) => math.pow(linearScale, 1.0f / 8.0f);
        static float ChunkOpacity(float opacity) => GaussianUtils.SquareCentered01(opacity);

        // ---- encoders, matching VECTOR_FMT_* in GaussianSplatting.hlsl ------------------------

        static uint EncodeFloat3ToNorm11(float3 v) // 32 bits: 11.10.11
            => (uint)(v.x * 2047.5f) | ((uint)(v.y * 1023.5f) << 11) | ((uint)(v.z * 2047.5f) << 21);

        static uint EncodeFloat3ToNorm565(float3 v) // 16 bits: 5.6.5
            => (uint)(v.x * 31.5f) | ((uint)(v.y * 63.5f) << 5) | ((uint)(v.z * 31.5f) << 11);

        static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
            => (uint)(v.x * 1023.5f) | ((uint)(v.y * 1023.5f) << 10) |
               ((uint)(v.z * 1023.5f) << 20) | ((uint)(v.w * 3.5f) << 30);

        // Safe against a zero-width chunk axis: ChunkBoundsJob floors every range at 1e-5 before
        // it writes the bounds, so max is always greater than min by the time this runs.
        static float3 Normalize01(float3 v, float3 min, float3 max) => math.saturate((v - min) / (max - min));

        static int SplatIndexToTextureIndex(uint idx)
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            const uint tilesPerRow = kTexWidth / 16;
            idx >>= 8;
            uint x = (idx % tilesPerRow) * 16 + xy.x;
            uint y = (idx / tilesPerRow) * 16 + xy.y;
            return (int)(y * kTexWidth + x);
        }

        // ---- jobs ----------------------------------------------------------------------------

        [BurstCompile]
        struct CalcBoundsJob : IJob
        {
            [ReadOnly] public NativeArray<byte> packedPos;
            public float fractScale;
            public int count;
            public NativeArray<float3> bounds;   // [0] = min, [1] = max

            public void Execute()
            {
                float3 lo = float.PositiveInfinity;
                float3 hi = float.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    float3 p = DecodePos(packedPos, i, fractScale);
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }
                bounds[0] = lo;
                bounds[1] = hi;
            }
        }

        [BurstCompile]
        struct MortonKeysJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> packedPos;
            public float fractScale;
            public float3 boundsMin, invBoundsSize;
            public NativeArray<uint> keys;
            public NativeArray<int> order;

            public void Execute(int i)
            {
                float3 p = (DecodePos(packedPos, i, fractScale) - boundsMin) * invBoundsSize;
                uint3 cell = (uint3)math.clamp(p * kMortonScale, 0.0f, kMortonScale);
                keys[i] = (uint)GaussianUtils.MortonEncode3(cell);
                order[i] = i;
            }
        }

        // Four 8-bit LSD passes over a 30-bit key. A comparison sort of two million elements is
        // half a second of a load nobody asked for; this is four linear passes and lands in tens
        // of milliseconds. Single-threaded by design: the counting phase is a shared histogram,
        // and splitting it costs more than it saves at this size.
        [BurstCompile]
        struct RadixSortJob : IJob
        {
            public NativeArray<uint> keys, keysTmp;
            public NativeArray<int> order, orderTmp;

            public void Execute()
            {
                int n = keys.Length;
                var counts = new NativeArray<int>(256, Allocator.Temp);

                var kSrc = keys; var kDst = keysTmp;
                var oSrc = order; var oDst = orderTmp;

                for (int pass = 0; pass < 4; pass++)
                {
                    int shift = pass * 8;
                    for (int i = 0; i < 256; i++) counts[i] = 0;
                    for (int i = 0; i < n; i++) counts[(int)((kSrc[i] >> shift) & 0xFF)]++;
                    int sum = 0;
                    for (int i = 0; i < 256; i++) { int c = counts[i]; counts[i] = sum; sum += c; }
                    for (int i = 0; i < n; i++)
                    {
                        int d = counts[(int)((kSrc[i] >> shift) & 0xFF)]++;
                        kDst[d] = kSrc[i];
                        oDst[d] = oSrc[i];
                    }
                    // An even number of passes, so the result lands back in the caller's arrays.
                    (kSrc, kDst) = (kDst, kSrc);
                    (oSrc, oDst) = (oDst, oSrc);
                }
                counts.Dispose();
            }
        }

        // Per-chunk min/max of everything the shader dequantizes. A transcription of the
        // import-time CalcChunkDataJob, minus its in-place rewrite of the splat array: nothing is
        // mutated here, so the encode jobs below run straight off the same packed source.
        [BurstCompile]
        struct ChunkBoundsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> packedPos, packedScale, packedAlpha, packedCol, packedSh;
            public float fractScale;
            public int shCoeffs, splatCount;
            [ReadOnly] public NativeArray<int> order;
            public NativeArray<SplatChunkInfo> chunks;

            public void Execute(int chunkIdx)
            {
                float3 minPos = float.PositiveInfinity, maxPos = float.NegativeInfinity;
                float3 minScl = float.PositiveInfinity, maxScl = float.NegativeInfinity;
                float4 minCol = float.PositiveInfinity, maxCol = float.NegativeInfinity;
                float3 minSh = float.PositiveInfinity, maxSh = float.NegativeInfinity;

                int begin = math.min(chunkIdx * kChunkSize, splatCount);
                int end = math.min((chunkIdx + 1) * kChunkSize, splatCount);

                for (int i = begin; i < end; i++)
                {
                    int s = order[i];

                    float3 pos = DecodePos(packedPos, s, fractScale);
                    float3 scl = ChunkScale(DecodeScale(packedScale, s));
                    float4 col = new float4(DecodeColor(packedCol, s), ChunkOpacity(packedAlpha[s] / 255.0f));

                    minPos = math.min(minPos, pos); maxPos = math.max(maxPos, pos);
                    minScl = math.min(minScl, scl); maxScl = math.max(maxScl, scl);
                    minCol = math.min(minCol, col); maxCol = math.max(maxCol, col);

                    // Skipped wholesale at degree 0 rather than run over fifteen coefficients that
                    // are all zero by definition. It is the same answer - the range floor below
                    // turns a zero-width range into 1e-5 either way - for thirty fewer vector
                    // min/max per splat, which on a two-million-splat capture is the difference
                    // between this pass mattering and not.
                    if (shCoeffs > 0)
                    {
                        for (int k = 0; k < 15; k++)
                        {
                            float3 sh = DecodeSH(packedSh, s, k, shCoeffs);
                            minSh = math.min(minSh, sh); maxSh = math.max(maxSh, sh);
                        }
                    }
                }

                if (shCoeffs == 0)
                    minSh = maxSh = float3.zero;   // the loop above would have found exactly this

                // A chunk whose values are all identical would give a zero-width range, and a
                // divide by zero in the encoders below.
                maxPos = math.max(maxPos, minPos + 1.0e-5f);
                maxScl = math.max(maxScl, minScl + 1.0e-5f);
                maxCol = math.max(maxCol, minCol + 1.0e-5f);
                maxSh = math.max(maxSh, minSh + 1.0e-5f);

                SplatChunkInfo info = default;
                info.posX = new float2(minPos.x, maxPos.x);
                info.posY = new float2(minPos.y, maxPos.y);
                info.posZ = new float2(minPos.z, maxPos.z);
                info.sclX = math.f32tof16(minScl.x) | (math.f32tof16(maxScl.x) << 16);
                info.sclY = math.f32tof16(minScl.y) | (math.f32tof16(maxScl.y) << 16);
                info.sclZ = math.f32tof16(minScl.z) | (math.f32tof16(maxScl.z) << 16);
                info.colR = math.f32tof16(minCol.x) | (math.f32tof16(maxCol.x) << 16);
                info.colG = math.f32tof16(minCol.y) | (math.f32tof16(maxCol.y) << 16);
                info.colB = math.f32tof16(minCol.z) | (math.f32tof16(maxCol.z) << 16);
                info.colA = math.f32tof16(minCol.w) | (math.f32tof16(maxCol.w) << 16);
                info.shR = math.f32tof16(minSh.x) | (math.f32tof16(maxSh.x) << 16);
                info.shG = math.f32tof16(minSh.y) | (math.f32tof16(maxSh.y) << 16);
                info.shB = math.f32tof16(minSh.z) | (math.f32tof16(maxSh.z) << 16);
                chunks[chunkIdx] = info;
            }
        }

        [BurstCompile]
        struct EncodePosOtherJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> packedPos, packedScale, packedRot;
            public float fractScale;
            [ReadOnly] public NativeArray<int> order;
            [ReadOnly] public NativeArray<SplatChunkInfo> chunks;
            public NativeArray<uint> outPos;
            [NativeDisableParallelForRestriction] public NativeArray<uint> outOther;

            public void Execute(int i)
            {
                int s = order[i];
                var chunk = chunks[i / kChunkSize];

                float3 posMin = new float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
                float3 posMax = new float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
                outPos[i] = EncodeFloat3ToNorm11(Normalize01(DecodePos(packedPos, s, fractScale), posMin, posMax));

                float3 sclMin = new float3(math.f16tof32(chunk.sclX), math.f16tof32(chunk.sclY), math.f16tof32(chunk.sclZ));
                float3 sclMax = new float3(math.f16tof32(chunk.sclX >> 16), math.f16tof32(chunk.sclY >> 16), math.f16tof32(chunk.sclZ >> 16));
                outOther[i * 2 + 0] = EncodeQuatToNorm10(DecodeRot(packedRot, s));
                outOther[i * 2 + 1] = EncodeFloat3ToNorm11(Normalize01(ChunkScale(DecodeScale(packedScale, s)), sclMin, sclMax));
            }
        }

        [BurstCompile]
        struct EncodeColorJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> packedCol, packedAlpha;
            [ReadOnly] public NativeArray<int> order;
            [ReadOnly] public NativeArray<SplatChunkInfo> chunks;
            [NativeDisableParallelForRestriction] public NativeArray<uint> outColor;

            public void Execute(int i)
            {
                int s = order[i];
                var chunk = chunks[i / kChunkSize];
                float4 colMin = new float4(math.f16tof32(chunk.colR), math.f16tof32(chunk.colG),
                                           math.f16tof32(chunk.colB), math.f16tof32(chunk.colA));
                float4 colMax = new float4(math.f16tof32(chunk.colR >> 16), math.f16tof32(chunk.colG >> 16),
                                           math.f16tof32(chunk.colB >> 16), math.f16tof32(chunk.colA >> 16));

                float4 col = new float4(DecodeColor(packedCol, s), ChunkOpacity(packedAlpha[s] / 255.0f));
                col = math.saturate((col - colMin) / (colMax - colMin));

                uint enc = (uint)(col.x * 255.5f) | ((uint)(col.y * 255.5f) << 8) |
                           ((uint)(col.z * 255.5f) << 16) | ((uint)(col.w * 255.5f) << 24);
                outColor[SplatIndexToTextureIndex((uint)i)] = enc;
            }
        }

        // Norm6: fifteen 5.6.5 coefficients plus two bytes of padding, 32 bytes a splat. The
        // shader's SH stride is fixed per format, so a degree-1 or degree-2 capture still fills
        // all fifteen slots and the ones past its degree are simply zero. Only degree 0 escapes
        // paying for SH at all, and it escapes by never scheduling this job.
        [BurstCompile]
        struct EncodeShJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> packedSh;
            public int shCoeffs;
            [ReadOnly] public NativeArray<int> order;
            [ReadOnly] public NativeArray<SplatChunkInfo> chunks;
            [NativeDisableParallelForRestriction] public NativeArray<uint> outSh;

            public void Execute(int i)
            {
                int s = order[i];
                var chunk = chunks[i / kChunkSize];
                float3 shMin = new float3(math.f16tof32(chunk.shR), math.f16tof32(chunk.shG), math.f16tof32(chunk.shB));
                float3 shMax = new float3(math.f16tof32(chunk.shR >> 16), math.f16tof32(chunk.shG >> 16), math.f16tof32(chunk.shB >> 16));

                int b = i * 8;
                for (int w = 0; w < 8; w++)
                {
                    // Two 16-bit coefficients to a uint; the sixteenth slot is the struct's padding.
                    int k = w * 2;
                    uint lo = k < 15 ? EncodeFloat3ToNorm565(Normalize01(DecodeSH(packedSh, s, k, shCoeffs), shMin, shMax)) : 0u;
                    uint hi = k + 1 < 15 ? EncodeFloat3ToNorm565(Normalize01(DecodeSH(packedSh, s, k + 1, shCoeffs), shMin, shMax)) : 0u;
                    outSh[b + w] = lo | (hi << 16);
                }
            }
        }
    }
}
