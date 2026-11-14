// SPDX-License-Identifier: MIT
// Reads a Gaussian splat capture out of a binary little-endian .ply, at IMPORT time.
//
// This is the front half of the .ply path: the reader hands blocks of decoded float32 splats to
// SplatSpzEncoder, which quantises them into the packed .spz layout the importer actually stores.
// Nothing downstream of the importer ever sees a .ply, and that is the whole point - a capture
// arrives as one of two file types and leaves as one asset type with one runtime path.
//
// IT STREAMS, IT DOES NOT LOAD
//
// A .ply Gaussian capture is uncompressed float32 at up to 248 bytes a splat, so the 1.92M-splat
// capture this was written against is a 476MB file. Reading it whole - or decoding it into a
// per-splat struct array, which is what the deleted importer did - costs half a gigabyte of
// transient memory before a single byte is encoded. So the file is walked a block at a time and
// the block buffer is under two megabytes. See SplatSpzEncoder for the block layout.
//
// PROPERTY ORDER IS NOT FIXED, AND NEITHER IS THE PROPERTY SET
//
// Every splat trainer writes its own property order, and plenty write extra properties nobody
// downstream wants. So the header is parsed into byte offsets and the offsets drive the gather;
// nothing here assumes x,y,z come first or that f_dc_0 follows the normals. What IS assumed is the
// one thing the format guarantees: a binary little-endian vertex record is its properties back to
// back with no padding.
//
// SPHERICAL HARMONICS ARE STORED PLANAR IN A .ply AND INTERLEAVED IN AN .spz
//
// f_rest_* is channel-major - every coefficient of red, then every coefficient of green, then
// blue - while .spz stores three bytes per coefficient. The transpose is folded into the offset
// table, so the gather does it for free. The per-channel count comes from the file rather than
// from the degree, because they are only equal when the file carries exactly 9, 24 or 45 of them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Viggle.SplatEngine.Editor
{
    /// <summary>
    /// A binary little-endian .ply open for reading as a Gaussian splat capture. Dispose it.
    /// </summary>
    internal sealed class SplatPlyFile : IDisposable
    {
        // The .spz reader's own ceiling, mirrored here so a corrupt header is rejected before it
        // sizes an allocation rather than after.
        const int kMaxSplats = 10_000_000;

        // A splat capture header is about sixty property lines. Anything past these limits is not
        // a header we are going to like, and the probe has to stay cheap: a ScriptedImporter
        // claims .ply globally, so this runs on every foreign mesh in the host project too.
        const int kMaxHeaderLines = 1024;
        const int kMaxHeaderBytes = 64 * 1024;

        // Everything a Gaussian splat needs. Normals are read by nobody: a .ply carries nx,ny,nz
        // because the trainer's exporter writes them, they are zero in every capture in practice,
        // and no part of the .spz layout has anywhere to put them.
        static readonly string[] kRequired =
        {
            "x", "y", "z",
            "f_dc_0", "f_dc_1", "f_dc_2",
            "opacity",
            "scale_0", "scale_1", "scale_2",
            "rot_0", "rot_1", "rot_2", "rot_3"
        };

        FileStream m_Stream;
        byte[] m_Raw;            // one block of raw vertex records
        int[] m_Offsets;         // byte offset within a record for every float of the block layout
        int m_VertexStride;
        int m_Returned;

        /// <summary>How many splats the file declares.</summary>
        internal int splatCount { get; private set; }

        /// <summary>The SH degree the file actually carries: 0, 1, 2 or 3.</summary>
        internal int shDegree { get; private set; }

        /// <summary>Floats per splat in the blocks <see cref="ReadBlock"/> fills.</summary>
        internal int strideFloats => m_Offsets.Length;

        SplatPlyFile() { }

        /// <summary>
        /// Opens <paramref name="path"/> for reading. Throws <see cref="IOException"/> with a
        /// human-readable reason if it is not a Gaussian splat .ply - call
        /// <see cref="DescribeUnreadable"/> first if you would rather ask than catch.
        /// </summary>
        internal static SplatPlyFile Open(string path)
        {
            var file = new SplatPlyFile();
            try
            {
                file.m_Stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                file.Init();
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Null when this file is a Gaussian splat capture this reader can decode, otherwise a
        /// human-readable reason why not. Reads the header and nothing else, and never throws,
        /// because a ScriptedImporter claims .ply for the whole project and most .ply files in the
        /// world are somebody's photogrammetry export.
        /// </summary>
        internal static string DescribeUnreadable(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return "the file does not exist";
                using var probe = Open(path);
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        /// <summary>
        /// Decodes the next block of splats into <paramref name="dst"/>, which must hold at least
        /// <paramref name="maxSplats"/> * <see cref="strideFloats"/> floats. Returns how many
        /// splats were written, or 0 at the end of the capture.
        /// </summary>
        internal int ReadBlock(float[] dst, int maxSplats)
        {
            int n = Math.Min(maxSplats, splatCount - m_Returned);
            if (n <= 0)
                return 0;
            if (dst == null || dst.Length < (long)n * strideFloats)
                throw new ArgumentException("PLY read error: the destination block is too small.");

            int wanted = n * m_VertexStride;
            if (m_Raw == null || m_Raw.Length < wanted)
                m_Raw = new byte[wanted];
            ReadFully(m_Stream, m_Raw, wanted);

            int stride = strideFloats;
            for (int i = 0; i < n; i++)
            {
                int src = i * m_VertexStride;
                int dstBase = i * stride;
                for (int f = 0; f < stride; f++)
                    dst[dstBase + f] = BitConverter.ToSingle(m_Raw, src + m_Offsets[f]);
            }

            m_Returned += n;
            return n;
        }

        public void Dispose()
        {
            m_Stream?.Dispose();
            m_Stream = null;
            m_Raw = null;
        }

        // ---- header ------------------------------------------------------------------------

        struct Property
        {
            public string name;
            public int offset;
            public bool isFloat32;
        }

        void Init()
        {
            var props = ParseHeader(m_Stream, out int count, out m_VertexStride);

            var byName = new Dictionary<string, Property>(props.Count);
            foreach (var p in props)
                byName[p.name] = p;   // last wins; a duplicate property name is already malformed

            var missing = new List<string>();
            foreach (var req in kRequired)
                if (!byName.TryGetValue(req, out var p) || !p.isFloat32)
                    missing.Add(req);
            if (missing.Count > 0)
                throw new IOException("it has no Gaussian splat properties (missing float32 " +
                                      string.Join(", ", missing) + ")");

            if (count < 1)
                throw new IOException("it contains no splats");
            if (count > kMaxSplats)
                throw new IOException($"it claims {count:N0} splats, past the {kMaxSplats:N0} ceiling");

            long dataStart = m_Stream.Position;
            long needed = (long)count * m_VertexStride;
            if (dataStart + needed > m_Stream.Length)
                throw new IOException($"it is truncated: {count:N0} splats need " +
                                      $"{needed:N0} bytes of vertex data and only " +
                                      $"{m_Stream.Length - dataStart:N0} follow the header");

            // Spherical harmonics are optional, and how many there are is how the degree is known:
            // nothing in the header states it. Only a contiguous run from f_rest_0 counts.
            int restCount = 0;
            while (byName.TryGetValue("f_rest_" + restCount, out var rest) && rest.isFloat32)
                restCount++;
            int perChannel = restCount / 3;
            shDegree = perChannel >= 15 ? 3 : perChannel >= 8 ? 2 : perChannel >= 3 ? 1 : 0;

            splatCount = count;
            m_Offsets = BuildOffsets(byName, perChannel);
        }

        // The gather table: one source byte offset per float of the block layout, in block order.
        int[] BuildOffsets(Dictionary<string, Property> byName, int perChannel)
        {
            int coeffs = SplatSpzFile.CoeffsForDegree(shDegree);
            var offsets = new int[SplatSpzEncoder.StrideFloats(shDegree)];

            offsets[SplatSpzEncoder.kPos + 0] = byName["x"].offset;
            offsets[SplatSpzEncoder.kPos + 1] = byName["y"].offset;
            offsets[SplatSpzEncoder.kPos + 2] = byName["z"].offset;
            for (int c = 0; c < 3; c++)
            {
                offsets[SplatSpzEncoder.kColor + c] = byName["f_dc_" + c].offset;
                offsets[SplatSpzEncoder.kScale + c] = byName["scale_" + c].offset;
            }
            offsets[SplatSpzEncoder.kOpacity] = byName["opacity"].offset;
            for (int c = 0; c < 4; c++)
                offsets[SplatSpzEncoder.kRot + c] = byName["rot_" + c].offset;

            // Planar to interleaved. perChannel is the file's stride between channels, which is
            // only equal to coeffs when the file carries exactly the coefficients of its degree;
            // a file with more than its degree needs simply has the extras skipped.
            for (int k = 0; k < coeffs; k++)
                for (int c = 0; c < 3; c++)
                    offsets[SplatSpzEncoder.kSh + k * 3 + c] = byName["f_rest_" + (c * perChannel + k)].offset;

            return offsets;
        }

        // Leaves the stream positioned on the first vertex record. Throws with a message written to
        // be shown to a user who dropped the wrong file into a project.
        static List<Property> ParseHeader(FileStream fs, out int vertexCount, out int vertexStride)
        {
            vertexCount = 0;
            vertexStride = 0;
            var props = new List<Property>();

            if (ReadLine(fs) != "ply")
                throw new IOException("it is not a PLY file");

            bool binaryLittleEndian = false;
            bool inVertexElement = false;
            bool sawVertexElement = false;
            bool sawAnyElement = false;

            for (int line = 0; line < kMaxHeaderLines; line++)
            {
                if (fs.Position > kMaxHeaderBytes)
                    throw new IOException("its header is implausibly long");

                var tokens = ReadLine(fs).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                switch (tokens[0])
                {
                    case "end_header":
                        if (!binaryLittleEndian)
                            throw new IOException("it is not a binary little-endian PLY, which is " +
                                                  "the only layout this reader decodes");
                        if (!sawVertexElement)
                            throw new IOException("it has no vertex element");
                        return props;

                    case "format":
                        binaryLittleEndian = tokens.Length >= 2 && tokens[1] == "binary_little_endian";
                        break;

                    case "element":
                        inVertexElement = tokens.Length >= 2 && tokens[1] == "vertex";
                        if (inVertexElement)
                        {
                            if (sawVertexElement)
                                throw new IOException("it declares the vertex element twice");
                            // Element data follows the header in declaration order, and this reader
                            // starts reading vertex records the moment the header ends. An element
                            // in front of the vertices would put its own data there instead.
                            if (sawAnyElement)
                                throw new IOException("its vertex element is not the first one");
                            sawVertexElement = true;
                            if (tokens.Length < 3 || !int.TryParse(tokens[2], out vertexCount))
                                throw new IOException("its vertex element has no readable count");
                        }
                        sawAnyElement = true;
                        break;

                    case "property":
                        if (!inVertexElement)
                            break;   // some other element's property; it is not in our records
                        if (tokens.Length >= 2 && tokens[1] == "list")
                            throw new IOException("its vertex element has a variable-length " +
                                                  "property, so its records are not a fixed stride");
                        if (tokens.Length < 3)
                            throw new IOException("it has a malformed property line");
                        int size = TypeSize(tokens[1]);
                        if (size == 0)
                            throw new IOException($"it uses the unsupported property type '{tokens[1]}'");
                        props.Add(new Property
                        {
                            name = tokens[2],
                            offset = vertexStride,
                            isFloat32 = tokens[1] == "float" || tokens[1] == "float32"
                        });
                        vertexStride += size;
                        break;
                }
            }
            throw new IOException("its header has no end_header line");
        }

        static int TypeSize(string type) => type switch
        {
            "char" or "int8" or "uchar" or "uint8" => 1,
            "short" or "int16" or "ushort" or "uint16" => 2,
            "int" or "int32" or "uint" or "uint32" or "float" or "float32" => 4,
            "double" or "float64" => 8,
            _ => 0
        };

        // Header lines are ASCII and short, so this reads a byte at a time rather than buffering:
        // the stream has to be left positioned exactly on the first vertex record, and a buffered
        // reader would have swallowed part of it.
        static string ReadLine(FileStream fs)
        {
            var sb = new StringBuilder(64);
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1 || b == '\n')
                    break;
                if (b != '\r')   // tolerate CRLF, which Windows exporters write
                    sb.Append((char)b);
            }
            return sb.ToString();
        }

        static void ReadFully(Stream s, byte[] dst, int count)
        {
            int got = 0;
            while (got < count)
            {
                int r = s.Read(dst, got, count - got);
                if (r <= 0)
                    throw new IOException("PLY read error: the file is smaller than its header says.");
                got += r;
            }
        }
    }
}
