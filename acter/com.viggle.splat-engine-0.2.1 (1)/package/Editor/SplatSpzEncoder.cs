// SPDX-License-Identifier: MIT
// Quantises float32 Gaussian splats into the packed Niantic/Scaniverse .spz layout, at IMPORT
// time. The exact inverse of SplatSpzFile plus the decode helpers in SplatCaptureJobs, and it has
// to stay that way: nothing at runtime validates the layout, so a section written in the wrong
// order or a constant off by a factor does not fail, it renders wrong.
//
// WHY AN ENCODER EXISTS AT ALL
//
// So that a .ply capture can ride the .spz path instead of getting one of its own. A .ply is
// uncompressed float32 at up to 248 bytes a splat - ten times the same .spz - so storing one
// verbatim to decode on load is not an option, and converting it into the packed GPU layout at
// import is the import-time pipeline that was deliberately deleted. Converting into .spz bytes
// instead leaves ONE asset type, ONE runtime path, and a .ply and an .spz of the same capture
// producing byte-identical SplatCaptureAsset contents.
//
// WHAT IT COSTS
//
// Positions survive: 24-bit fixed point at the fractional bits chosen below is finer than the
// Norm11 chunk quantisation the runtime packer applies afterwards, so the .spz stage is not the
// limiting term. Rotation and scale do not: 8 bits each, against float32 in the source. Colour and
// opacity were already 8-bit-ish by the time anything looked at them.
//
// THE SECTIONS ARE BUILT IN PARALLEL AND WRITTEN IN FILE ORDER
//
// A capture is added block by block rather than as one array, for the reason in SplatPlyFile: the
// source file is up to half a gigabyte and materialising it is what this design exists to avoid.
// The packed sections total 19 bytes a splat plus SH, which is affordable to hold; positions stay
// float until the very end because the fractional-bit count is a property of the whole capture and
// is not known until the last splat has been seen.

using System;
using System.IO;
using System.IO.Compression;
using Viggle.SplatEngine.Jobs;
using Unity.Mathematics;

namespace Viggle.SplatEngine.Editor
{
    internal sealed class SplatSpzEncoder
    {
        // "NGSP" little-endian, and the only version SplatSpzFile reads.
        const uint kMagic = 0x5053474e;
        const uint kVersion = 2;
        const int kHeaderSize = 16;

        // Positions are 24-bit SIGNED fixed point.
        const int kPosMin = -8388608;
        const int kPosMax = 8388607;

        /// <summary>Pass as <c>fractionalBits</c> to derive it from the capture's own extent.</summary>
        internal const int kAutoFractionalBits = -1;

        // The ceiling on that derivation. 16 fractional bits is a 15-micron quantum, already below
        // the step the runtime packer's Norm11 positions can resolve inside a chunk a few
        // centimetres across, so past this point the extra bits buy nothing the renderer can show.
        // They are not free: the position section is 9 bytes a splat and the high bytes of a
        // fixed-point value are what gzip lives on. Measured on the 1.92M-splat reference capture,
        // 12 bits compresses to 12.8MB, 16 to 14.9MB and 19 to 16.4MB.
        const int kMaxFractionalBits = 16;

        // The reference implementation's colour scale. It maps a raw SH DC coefficient into 0..1
        // before quantising, and the decoder divides it back out - see DecodeColor.
        const float kColorScale = 0.15f;

        // ---- the block layout callers fill ---------------------------------------------------
        //
        // One splat is StrideFloats(shDegree) floats, in the source file's own domain: no sigmoid
        // applied to opacity, no exp applied to scale, and the rotation in the order a .ply stores
        // it. Every transform belongs here, so there is exactly one place where the SPZ
        // conventions are written down.

        internal const int kPos = 0;        // 3: x, y, z, metres
        internal const int kColor = 3;      // 3: f_dc_0..2, raw SH DC coefficients
        internal const int kOpacity = 6;    // 1: raw logit, pre-sigmoid
        internal const int kScale = 7;      // 3: log scale, pre-exp
        internal const int kRot = 10;       // 4: rot_0..3, w FIRST, as a .ply stores it
        internal const int kSh = 14;        // 3 * coeffs: interleaved, three channels per coefficient
        const int kFixedFloats = 14;

        internal static int StrideFloats(int shDegree) =>
            kFixedFloats + 3 * SplatSpzFile.CoeffsForDegree(shDegree);

        readonly int m_SplatCount;
        readonly int m_ShDegree;
        readonly int m_ShCoeffs;
        readonly int m_FractionalBits;

        readonly float[] m_Pos;     // 3 per splat, still float: see the header
        readonly byte[] m_Alpha;    // 1
        readonly byte[] m_Color;    // 3
        readonly byte[] m_Scale;    // 3
        readonly byte[] m_Rot;      // 3
        readonly byte[] m_Sh;       // 3 * coeffs, zero-length at degree 0

        int m_Written;

        /// <param name="splatCount">Exactly how many splats will be added before Finish.</param>
        /// <param name="shDegree">0, 1, 2 or 3 - the degree the SOURCE carries. A capture with no
        /// view-dependent shading must be written as degree 0 with no SH section at all, not as a
        /// higher degree padded with zeroes: the section length follows the degree, and the runtime
        /// clamps the shader's SH order to what the asset holds.</param>
        /// <param name="fractionalBits">Fixed-point fractional bits for positions, or
        /// <see cref="kAutoFractionalBits"/> to choose from the capture's extent.</param>
        internal SplatSpzEncoder(int splatCount, int shDegree, int fractionalBits = kAutoFractionalBits)
        {
            if (splatCount < 1)
                throw new ArgumentOutOfRangeException(nameof(splatCount));
            if (shDegree < 0 || shDegree > 3)
                throw new ArgumentOutOfRangeException(nameof(shDegree));
            if (fractionalBits != kAutoFractionalBits && (fractionalBits < 0 || fractionalBits > 24))
                throw new ArgumentOutOfRangeException(nameof(fractionalBits));

            m_SplatCount = splatCount;
            m_ShDegree = shDegree;
            m_ShCoeffs = SplatSpzFile.CoeffsForDegree(shDegree);
            m_FractionalBits = fractionalBits;

            m_Pos = new float[splatCount * 3];
            m_Alpha = new byte[splatCount];
            m_Color = new byte[splatCount * 3];
            m_Scale = new byte[splatCount * 3];
            m_Rot = new byte[splatCount * 3];
            m_Sh = new byte[splatCount * 3 * m_ShCoeffs];
        }

        /// <summary>
        /// Quantises <paramref name="count"/> splats laid out <see cref="StrideFloats"/> floats
        /// each. Call until the full splat count has been added.
        /// </summary>
        internal void AddBlock(float[] block, int count)
        {
            int stride = StrideFloats(m_ShDegree);
            if (count < 0 || block == null || block.Length < (long)count * stride)
                throw new ArgumentException("SPZ encode error: short block.");
            if (m_Written + count > m_SplatCount)
                throw new InvalidOperationException("SPZ encode error: more splats than declared.");

            for (int i = 0; i < count; i++)
            {
                int s = i * stride;
                int o = m_Written + i;

                m_Pos[o * 3 + 0] = block[s + kPos + 0];
                m_Pos[o * 3 + 1] = block[s + kPos + 1];
                m_Pos[o * 3 + 2] = block[s + kPos + 2];

                // Colour: the DC coefficient scaled into 0..1. DecodeColor undoes exactly this,
                // then runs SH0ToColor - which is why nothing here converts to a colour.
                for (int c = 0; c < 3; c++)
                    m_Color[o * 3 + c] = ToByte((block[s + kColor + c] * kColorScale + 0.5f) * 255.0f);

                // Opacity: the sigmoid the renderer would otherwise apply, baked in. The packed
                // byte IS the linear alpha - see the alpha read in EncodeColorJob.
                m_Alpha[o] = ToByte(GaussianUtils.Sigmoid(block[s + kOpacity]) * 255.0f);

                // Scale stays logarithmic and gets an 8-bit affine window of it, which is the one
                // place the format is genuinely coarse: 1/16th of a log unit, so about 6% per step.
                for (int c = 0; c < 3; c++)
                    m_Scale[o * 3 + c] = ToByte((block[s + kScale + c] + 10.0f) * 16.0f);

                // Rotation: only xyz is stored and w is reconstructed as the positive root, so the
                // quaternion has to be normalised and sign-flipped into the w >= 0 hemisphere
                // first. Both quaternions represent the same rotation, so the flip costs nothing.
                float4 q = new float4(block[s + kRot + 0], block[s + kRot + 1],
                                      block[s + kRot + 2], block[s + kRot + 3]);
                q = math.lengthsq(q) > 1e-12f
                    ? GaussianUtils.NormalizeSwizzleRotation(q)   // (w,x,y,z) in, (x,y,z,w) out
                    : new float4(0, 0, 0, 1);                     // degenerate: identity, not NaN
                if (q.w < 0.0f)
                    q = -q;
                m_Rot[o * 3 + 0] = ToByte((q.x + 1.0f) * 127.5f);
                m_Rot[o * 3 + 1] = ToByte((q.y + 1.0f) * 127.5f);
                m_Rot[o * 3 + 2] = ToByte((q.z + 1.0f) * 127.5f);

                for (int k = 0; k < m_ShCoeffs; k++)
                    for (int c = 0; c < 3; c++)
                        m_Sh[(o * m_ShCoeffs + k) * 3 + c] =
                            ToByte(block[s + kSh + k * 3 + c] * 128.0f + 128.0f);
            }

            m_Written += count;
        }

        /// <summary>
        /// Gzips the packed sections into the bytes of an .spz file. Valid once the declared splat
        /// count has been added.
        /// </summary>
        /// <param name="fractionalBits">The fixed-point scale the positions were written at.</param>
        internal byte[] Finish(out int fractionalBits)
        {
            if (m_Written != m_SplatCount)
                throw new InvalidOperationException(
                    $"SPZ encode error: {m_Written} of {m_SplatCount} splats were added.");

            fractionalBits = m_FractionalBits == kAutoFractionalBits
                ? ChooseFractionalBits(MaxAbsCoord())
                : m_FractionalBits;

            var pos = new byte[m_SplatCount * 9];
            float scale = 1 << fractionalBits;
            for (int i = 0; i < m_SplatCount * 3; i++)
            {
                // Round half up, the same convention as ToByte, and the clamp is what keeps a stray
                // coordinate - an outlier splat a trainer left at infinity - from wrapping around
                // the 24-bit field and landing on the far side of the capture.
                int q = (int)math.clamp(math.floor(m_Pos[i] * scale + 0.5f), kPosMin, kPosMax);
                // Little-endian two's complement, which is what the 24-bit sign extension on the
                // reading side reconstructs: -1 has to come out as ff ff ff.
                pos[i * 3 + 0] = (byte)(q & 0xFF);
                pos[i * 3 + 1] = (byte)((q >> 8) & 0xFF);
                pos[i * 3 + 2] = (byte)((q >> 16) & 0xFF);
            }

            var header = new byte[kHeaderSize];
            Write32(header, 0, kMagic);
            Write32(header, 4, kVersion);
            Write32(header, 8, (uint)m_SplatCount);
            // Byte 0 is the SH degree, byte 1 the fractional bits, byte 2 the flag bits (the only
            // one defined is "antialiased", which a converted capture makes no claim about) and
            // byte 3 is reserved.
            Write32(header, 12, (uint)m_ShDegree | ((uint)fractionalBits << 8));

            // A capture compresses to roughly three quarters of its packed size, so this saves the
            // MemoryStream most of its doubling without reserving the whole payload twice over.
            long packed = kHeaderSize + (long)m_SplatCount * 19 + m_Sh.Length;
            using var ms = new MemoryStream((int)Math.Min(packed * 3 / 4, 256L * 1024 * 1024));
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                // Section order is the file's, and SplatSpzFile reads it forwards with no seeking.
                gz.Write(header, 0, header.Length);
                gz.Write(pos, 0, pos.Length);
                gz.Write(m_Alpha, 0, m_Alpha.Length);
                gz.Write(m_Color, 0, m_Color.Length);
                gz.Write(m_Scale, 0, m_Scale.Length);
                gz.Write(m_Rot, 0, m_Rot.Length);
                gz.Write(m_Sh, 0, m_Sh.Length);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// The most fractional bits a capture reaching <paramref name="maxAbsCoord"/> metres from
        /// the origin can carry without overflowing the 24-bit field, capped at what the renderer
        /// can still resolve. A capture is measured rather than assumed a size: the reference
        /// implementation's fixed 12 bits is a quarter-millimetre quantum, which is generous for a
        /// room and coarse for a handheld object scan.
        /// </summary>
        internal static int ChooseFractionalBits(float maxAbsCoord)
        {
            if (!(maxAbsCoord > 0.0f))   // also catches NaN
                return kMaxFractionalBits;
            return math.clamp((int)math.floor(math.log2(kPosMax / maxAbsCoord)),
                              0, kMaxFractionalBits);
        }

        float MaxAbsCoord()
        {
            float max = 0.0f;
            for (int i = 0; i < m_Pos.Length; i++)
            {
                float a = math.abs(m_Pos[i]);
                if (a > max)   // false for NaN, which is the wanted answer: it cannot set the scale
                    max = a;
            }
            return max;
        }

        // Round-to-nearest into 0..255. The clamp is not defensive tidiness - a .ply is float32 and
        // nothing in it is bounded, so a log scale past the format's window or a colour outside
        // gamut arrives here regularly.
        static byte ToByte(float v) => (byte)(int)math.clamp(math.floor(v + 0.5f), 0.0f, 255.0f);

        static void Write32(byte[] dst, int offset, uint v)
        {
            dst[offset + 0] = (byte)(v & 0xFF);
            dst[offset + 1] = (byte)((v >> 8) & 0xFF);
            dst[offset + 2] = (byte)((v >> 16) & 0xFF);
            dst[offset + 3] = (byte)((v >> 24) & 0xFF);
        }
    }
}
