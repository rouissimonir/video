// SPDX-License-Identifier: MIT
// Reads a Niantic/Scaniverse .spz capture (https://github.com/nianticlabs/spz) at RUNTIME.
// The capture-side counterpart of SplatCharacterBin, and it exists for the same reason: the file
// a host ships is the compressed source, and the GPU layout is built from it on load.
//
// This used to be an editor-only reader driving an import-time packer. It moved down here when
// the capture path stopped converting at import: the old importer turned a 27MB .spz into a
// 432MB project asset, most of it spherical harmonics the file does not contain.
//
// All this class does is gunzip and hand back the packed sections. It deliberately does not
// decode: decoding into a per-splat struct is what made the old path expensive, and the packing
// jobs re-decode straight out of these bytes instead. See SplatCaptureJobs.
//
// GZipStream is used rather than a hand-rolled inflate because it is in the BCL, works under
// IL2CPP, and is the same decompressor the reference implementation uses.

using System;
using System.IO;
using System.IO.Compression;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Viggle.SplatEngine.Jobs;

namespace Viggle.SplatEngine
{
    internal static class SplatSpzFile
    {
        // "NGSP", little-endian, the first four bytes of the DECOMPRESSED stream.
        const uint kMagic = 0x5053474e;
        const int kVersion = 2;
        const int kHeaderSize = 16;

        // The reference implementation's own ceiling. A file claiming more than this is corrupt
        // rather than large, and believing it would allocate gigabytes before failing.
        const int kMaxSplats = 10_000_000;

        /// Coefficients per splat at each SH degree. Degree 0 is a capture with no view-dependent
        /// shading at all, which is the common case for object scans and is why it is worth
        /// special-casing everywhere downstream.
        internal static int CoeffsForDegree(int degree) => degree switch
        {
            0 => 0,
            1 => 3,
            2 => 8,
            3 => 15,
            _ => 0
        };

        /// <summary>
        /// Reads the 16-byte header without decoding the body. False when these bytes are not an
        /// .spz capture this reader understands - never throws, because both callers (the importer
        /// and the runtime asset wrapper) have to report rather than explode.
        /// </summary>
        internal static bool PeekHeader(byte[] bytes, out int splatCount, out int shDegree)
        {
            splatCount = 0;
            shDegree = 0;
            try
            {
                if (bytes == null || bytes.Length < 18)   // gzip container is at least this long
                    return false;
                using var gz = new GZipStream(new MemoryStream(bytes, false), CompressionMode.Decompress);
                return ReadHeader(gz, out splatCount, out shDegree, out _);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gunzips <paramref name="bytes"/> into the packed sections the packing jobs consume.
        /// Throws <see cref="IOException"/> on a malformed file; the caller decides how loud that
        /// should be. Dispose the result with <see cref="Dispose"/>.
        /// </summary>
        internal static SpzPackedData Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new IOException("SPZ read error: no data.");

            using var gz = new GZipStream(new MemoryStream(bytes, false), CompressionMode.Decompress);
            if (!ReadHeader(gz, out int n, out int shDegree, out int fractBits))
                throw new IOException("SPZ read error: this is not a version 2 .spz capture.");
            if (n < 1 || n > kMaxSplats)
                throw new IOException($"SPZ read error: out of range splat count {n}.");
            if (fractBits < 0 || fractBits > 24)
                throw new IOException($"SPZ read error: out of range fractional bits {fractBits}.");

            int shCoeffs = CoeffsForDegree(shDegree);
            var d = new SpzPackedData
            {
                splatCount = n,
                shCoeffs = shCoeffs,
                fractScale = 1.0f / (1 << fractBits),
            };

            try
            {
                // Allocated and filled in the file's own section order, so the gzip stream is read
                // once, forwards, with no seeking and no staging copy of the whole payload.
                d.pos = Alloc(n * 9);
                d.alpha = Alloc(n);
                d.color = Alloc(n * 3);
                d.scale = Alloc(n * 3);
                d.rot = Alloc(n * 3);
                // Never zero-length, even at degree 0: a job field has to be a real container, and
                // a one-byte allocation is cheaper than reasoning about whether an empty one is.
                d.sh = Alloc(Math.Max(n * 3 * shCoeffs, 1));

                ReadFully(gz, d.pos, n * 9);
                ReadFully(gz, d.alpha, n);
                ReadFully(gz, d.color, n * 3);
                ReadFully(gz, d.scale, n * 3);
                ReadFully(gz, d.rot, n * 3);
                ReadFully(gz, d.sh, n * 3 * shCoeffs);
                return d;
            }
            catch
            {
                Dispose(ref d);
                throw;
            }
        }

        internal static void Dispose(ref SpzPackedData d)
        {
            if (d.pos.IsCreated) d.pos.Dispose();
            if (d.alpha.IsCreated) d.alpha.Dispose();
            if (d.color.IsCreated) d.color.Dispose();
            if (d.scale.IsCreated) d.scale.Dispose();
            if (d.rot.IsCreated) d.rot.Dispose();
            if (d.sh.IsCreated) d.sh.Dispose();
            d = default;
        }

        /// <summary>
        /// Stride-sampled splat centres and colours, for a project-window thumbnail. Reads and
        /// disposes the whole capture, so it is a gunzip's worth of work - fine inside an import,
        /// which is where the only caller lives. Returns false rather than throwing.
        /// </summary>
        internal static bool SamplePoints(byte[] bytes, int maxSamples,
                                          out Vector3[] positions, out Color32[] colors,
                                          out Vector3 boundsMin, out Vector3 boundsMax)
        {
            positions = null;
            colors = null;
            boundsMin = default;
            boundsMax = default;
            SpzPackedData d = default;
            try
            {
                d = Read(bytes);
                int stride = Mathf.Max(1, (d.splatCount + maxSamples - 1) / maxSamples);
                int capacity = (d.splatCount + stride - 1) / stride;
                positions = new Vector3[capacity];
                colors = new Color32[capacity];
                int written = SplatCaptureJobs.SamplePoints(d, stride, positions, colors);
                if (written == 0)
                    return false;
                if (written != capacity)
                {
                    Array.Resize(ref positions, written);
                    Array.Resize(ref colors, written);
                }
                // Bounds of the SAMPLE, not of the capture: the preview frames what it draws, and
                // computing the true bounds would mean a second pass over every splat for an icon.
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int i = 0; i < written; i++)
                {
                    min = Vector3.Min(min, positions[i]);
                    max = Vector3.Max(max, positions[i]);
                }
                boundsMin = min;
                boundsMax = max;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Dispose(ref d);
            }
        }

        static NativeArray<byte> Alloc(int length) =>
            new NativeArray<byte>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        static bool ReadHeader(Stream s, out int splatCount, out int shDegree, out int fractBits)
        {
            splatCount = 0;
            shDegree = 0;
            fractBits = 0;

            var h = new byte[kHeaderSize];
            int got = 0;
            while (got < kHeaderSize)
            {
                int r = s.Read(h, got, kHeaderSize - got);
                if (r <= 0)
                    return false;
                got += r;
            }

            uint magic = BitConverter.ToUInt32(h, 0);
            uint version = BitConverter.ToUInt32(h, 4);
            if (magic != kMagic || version != kVersion)
                return false;

            splatCount = (int)BitConverter.ToUInt32(h, 8);
            uint flags = BitConverter.ToUInt32(h, 12);
            shDegree = (int)(flags & 0xFF);
            fractBits = (int)((flags >> 8) & 0xFF);
            if (shDegree < 0 || shDegree > 3)
                return false;
            return true;
        }

        // A stream read that actually reads everything asked for. GZipStream is free to return a
        // short read at any point, and the reader this replaces treated a short read as a
        // truncated file - which turned "the decompressor handed back 64KB instead of 17MB" into
        // "your capture is corrupt".
        static unsafe void ReadFully(Stream s, NativeArray<byte> dst, int count)
        {
            if (count <= 0)
                return;
            const int kStaging = 1 << 16;
            var staging = new byte[kStaging];
            byte* d = (byte*)dst.GetUnsafePtr();
            int off = 0;
            while (off < count)
            {
                int want = Math.Min(kStaging, count - off);
                int got = s.Read(staging, 0, want);
                if (got <= 0)
                    throw new IOException("SPZ read error: the file is smaller than its header says.");
                fixed (byte* src = staging)
                    UnsafeUtility.MemCpy(d + off, src, got);
                off += got;
            }
        }
    }
}
