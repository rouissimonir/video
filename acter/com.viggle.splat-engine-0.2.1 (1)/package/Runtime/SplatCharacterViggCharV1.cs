// SPDX-License-Identifier: MIT
// VIGGCHAR version 1 - the quantised block payload.
//
// Payload layout, offsets from the start of the FILE:
//   [16..20)             uint32 headerLength
//   [20..20+headerLength) UTF-8 JSON header, space-padded to a multiple of 4
//   [blockStart..)       the blocks, where blockStart = 20 + headerLength
//
// The header has exactly two keys, `numSplats` and `blocks`. `blocks` is the manifest: what it
// lists is what the file contains, and each entry carries everything needed to decode THAT block
// - its offset, length, dtype, components and count, plus its own dequantisation bounds or its own
// palette where it has them. Nothing cross-references a top-level key, and there is no file-wide
// precision mode.
//
// FOUR THINGS THAT BITE, all of which this reader is shaped around:
//
//  1. Every `offset` is relative to blockStart, NOT to the start of the file, and the blocks are
//     NOT back to back - each starts on a 4-byte boundary with 0x00 padding ahead of it. So every
//     block is located by seeking to blockStart + offset. Accumulating lengths is wrong from the
//     second block onward and wrong QUIETLY: the arrays come out the right shape, filled with data
//     shifted by a byte or three, and the character renders as garbage rather than failing.
//  2. Values are COMPONENT-MAJOR: all the X, then all the Y, then all the Z. This is the biggest
//     structural difference from VFCHARB1, which interleaves a record per splat.
//  3. `boneIndex.dtype` is u8 on some files and u16 on others, depending on whether that
//     character's palette fits in 256 entries. It is read from the entry, never assumed.
//  4. `boneIndex` values index that block's `palette`, which maps to real rig bones and is not the
//     identity - 391 entries for a 441-bone rig on the shipped example.
//
// There is no compression anywhere in this format. If a decode here ever seems to want a
// DeflateStream, something has been misread.
//
// WHAT COMES OUT. SplatCharacterBinData, the same arrays SplatCharacterBin.Parse produces from a
// VFCHARB1 file, so everything downstream - the renderer, the skinning, the Avatar - needs no
// knowledge of which container it came from. That means undoing three of this format's storage
// choices on the way out, because SplatCharacterBinData is defined in VFCHARB1's terms:
//   * `scale` is stored as a LOGARITHM and is handed back linear (exp), which is what
//     SplatRenderAssetBuilder writes into the asset.
//   * `opacity` is stored as a LOGIT and is handed back as an alpha in 0..1 (sigmoid).
//   * `color` is stored as the spherical-harmonic DC term, whose decode is
//     (q/255 - 0.5) / SH_C0 and whose inverse is exactly q/255 - so the stored byte IS the 0..1
//     colour, and it is copied through unchanged rather than round-tripped through SH_C0.
// Positions, rotations and joint rest translations get the same source-to-Unity conversion the
// VFCHARB1 path applies; see SplatConvention.
//
// The joints block is PARENT-LOCAL rest translation per rig bone, one row per bone (441), not per
// splat and not world positions - which is what jointRestPos has always held, so it needs no
// special handling here beyond the coordinate conversion.
//
// Version 1 means CORRECTED: the ankle bake and the ground shift are already in the gaussians and
// in the joints block, and the character stands on y = 0. There is no in-file signal about that
// and nothing below looks for one - the version answered it.

using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Viggle.SplatEngine
{
    internal sealed class SplatCharacterViggCharV1 : ISplatCharacterViggCharVersion
    {
        // ---- constants of version 1 ----
        // Fixed by the encoding itself, which is why they live here and not in the file. Anything
        // measured from a character's own data is per-file and lives in that block's entry.

        /// <summary>Spherical-harmonic DC coefficient.</summary>
        const double kShC0 = 0.28209479177387814;
        /// <summary>Position quantisation levels: stored values run 0..65535.</summary>
        const int kPositionLevels = 65536;
        /// <summary>Scale and opacity quantisation levels: stored values run 0..255.</summary>
        const int kByteLevels = 256;

        /// <summary>The five blocks every conforming file carries. A missing one is a rejection, by name.</summary>
        static readonly string[] kUnconditionalBlocks = { "position", "scale", "rotation", "color", "opacity" };

        /// <summary>
        /// The blocks quantised against a range measured from this file's own data, and so the
        /// only ones carrying min/max. `rotation`, `color` and `boneWeight` carry neither: their
        /// range is a property of their type - a unit quaternion, a colour, weights that sum to 1 -
        /// so there is nothing per-file to record.
        /// </summary>
        static readonly string[] kBoundedBlocks = { "position", "scale", "opacity" };

        /// <summary>
        /// The shape each block's entry must declare. A `components` of 0 means "whatever the entry
        /// says": boneIndex carries K and boneWeight carries K - 1, and K is per-file.
        ///
        /// A block name not in here is IGNORED, whole. The manifest is what the file contains, and
        /// a name this version does not define contributes nothing to the decode - so it is not
        /// validated against rules written for other blocks either.
        /// </summary>
        struct BlockShape
        {
            public string[] dtypes;
            public int components;
        }

        static readonly Dictionary<string, BlockShape> kBlockShapes = new Dictionary<string, BlockShape>(StringComparer.Ordinal)
        {
            { "position",   new BlockShape { dtypes = new[] { "u16" },        components = 3 } },
            { "scale",      new BlockShape { dtypes = new[] { "u8" },         components = 3 } },
            { "rotation",   new BlockShape { dtypes = new[] { "u8" },         components = 3 } },
            { "color",      new BlockShape { dtypes = new[] { "u8" },         components = 3 } },
            { "opacity",    new BlockShape { dtypes = new[] { "u8" },         components = 1 } },
            { "boneIndex",  new BlockShape { dtypes = new[] { "u8", "u16" },  components = 0 } },
            { "boneWeight", new BlockShape { dtypes = new[] { "u8" },         components = 0 } },
            { "joints",     new BlockShape { dtypes = new[] { "f32" },        components = 3 } },
        };

        /// <summary>
        /// The most bone influences per gaussian SplatCharacterBinData can carry. Not a property of
        /// the format - K is per-file and the entry declares it - but of the uint4/float4 arrays
        /// every consumer in this package indexes. Every file shipped so far is K = 4.
        /// </summary>
        const int kMaxInfluences = 4;

        /// <summary>One block's entry, located.</summary>
        struct Block
        {
            public string name;
            public int offset;        // relative to blockStart
            public int length;
            public string dtype;
            public int elemSize;      // bytes per value: f32 4, u16 2, u8 1
            public int components;
            public int count;
            public double[] min;      // bounded blocks only, one entry per component
            public double[] max;
            public int[] palette;     // boneIndex only
            public int baseOff;       // absolute byte offset of component 0: blockStart + offset

            /// <summary>Absolute byte offset of component c. Component-major, so it is one stride in.</summary>
            public int ComponentOffset(int c) => baseOff + c * count * elemSize;
        }

        // ------------------------------------------------------------------
        // Header
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads and fully validates the header. Both entry points start here, so PeekBoneCount
        /// rejects exactly what Parse rejects rather than reporting a bone count from a file that
        /// will not decode.
        /// </summary>
        static Dictionary<string, Block> ReadHeader(byte[] bytes, int payloadOffset, int payloadLength,
                                                    out int numSplats)
        {
            int totalLength = payloadOffset + payloadLength;

            if (payloadLength < 4)
                throw Fail("Truncated", $"payload is {payloadLength} B, too small to carry a header length");

            uint headerLengthRaw = BitConverter.ToUInt32(bytes, payloadOffset);
            int headerStart = payloadOffset + 4;
            if (headerLengthRaw > (uint)(totalLength - headerStart))
                throw Fail("Truncated",
                           $"header claims {headerLengthRaw} B, which ends past the file's {totalLength} B");
            int headerLength = (int)headerLengthRaw;
            int blockStart = headerStart + headerLength;

            // The writer pads the JSON with spaces until this holds. Block offsets are relative to
            // blockStart, so a misaligned base misaligns every block in the payload, and a u16 or
            // f32 block would then fail far from the cause.
            if ((headerLength & 3) != 0)
                throw Fail("UnalignedPayload", $"headerLength {headerLength} is not a multiple of 4");

            string text;
            SplatJsonValue root;
            try
            {
                // Strict UTF-8: the permissive decoder substitutes U+FFFD for malformed bytes,
                // which turns a corrupt header into a JSON syntax error somewhere else.
                text = new UTF8Encoding(false, true).GetString(bytes, headerStart, headerLength);
                root = SplatJson.Parse(text);
            }
            catch (Exception e)
            {
                throw Fail("MalformedHeader", $"header is not valid UTF-8 JSON: {e.Message}");
            }
            if (!root.IsObject)
                throw Fail("MalformedHeader", $"header is {root.Describe()}, expected a JSON object");

            numSplats = ReadRequiredUint(root, "header", "numSplats");
            if (!root.TryGet("blocks", out var blocksJson))
                throw Fail("MissingField", "header is missing 'blocks', the block table");
            if (!blocksJson.IsObject)
                throw Fail("BadFieldValue", $"header 'blocks' is {blocksJson.Describe()}, expected an object");

            var blocks = new Dictionary<string, Block>(StringComparer.Ordinal);
            foreach (var kv in blocksJson.Members)
            {
                if (!kBlockShapes.ContainsKey(kv.Key)) continue;   // not a block this version defines
                blocks.Add(kv.Key, ReadBlockEntry(kv.Key, kv.Value, blockStart, totalLength));
            }
            ValidateManifest(blocks, numSplats);
            return blocks;
        }

        /// <summary>
        /// The five common keys, and the arithmetic that ties them together. Everything checked
        /// here is a property of ONE entry - nothing reaches into a sibling key.
        /// </summary>
        static Block ReadBlockEntry(string name, SplatJsonValue entry, int blockStart, int totalLength)
        {
            string where = $"block '{name}'";
            if (!entry.IsObject)
                throw Fail("BadFieldValue", $"{where}: entry is {entry.Describe()}, expected an object");

            var b = new Block
            {
                name = name,
                offset = ReadRequiredUint(entry, where, "offset"),
                length = ReadRequiredUint(entry, where, "length"),
                components = ReadRequiredUint(entry, where, "components"),
                count = ReadRequiredUint(entry, where, "count"),
            };

            if (!entry.TryGet("dtype", out var dtypeJson))
                throw Fail("MissingField", $"{where}: missing required key 'dtype'");
            if (dtypeJson.Kind != SplatJsonKind.String)
                throw Fail("BadFieldValue", $"{where}: 'dtype' is {dtypeJson.Describe()}, expected a string");
            b.dtype = dtypeJson.AsString;
            b.elemSize = SizeOfDtype(b.dtype);
            if (b.elemSize == 0)
                throw Fail("BadFieldValue", $"{where}: dtype '{b.dtype}', expected one of f32, u16, u8");

            // A u16 or f32 block at an odd offset otherwise fails deep inside the decode, where the
            // cause is no longer visible.
            if ((b.offset & 3) != 0)
                throw Fail("UnalignedPayload", $"{where}: offset {b.offset} is not a multiple of 4");

            b.baseOff = blockStart + b.offset;
            long end = (long)b.baseOff + b.length;
            if (end > totalLength)
                throw Fail("Truncated",
                           $"{where}: bytes [{b.baseOff}, {end}) run past the file's {totalLength} B");

            // A block's bytes ARE its decoded bytes - there is no compression - so its stored
            // length is the size its own shape asks for. A disagreement is the file describing
            // itself wrong, caught here rather than as a short read inside the decode.
            long declared = (long)b.components * b.count * b.elemSize;
            if (declared != b.length)
                throw Fail("BlockSizeMismatch",
                           $"{where}: length {b.length} B, but {b.components} components x {b.count} rows x " +
                           $"{b.elemSize} B ({b.dtype}) = {declared} B");

            b.min = ReadOptionalNumberArray(entry, where, "min");
            b.max = ReadOptionalNumberArray(entry, where, "max");
            b.palette = ReadOptionalPalette(entry, where);
            return b;
        }

        /// <summary>
        /// The manifest as a whole: which blocks are present, and what their entries must then say
        /// about each other. This is the one place the reader learns what the file contains -
        /// nothing below probes the payload, guesses from a size, or reads meaning into an absence
        /// beyond the presence rules the format states outright.
        /// </summary>
        static void ValidateManifest(Dictionary<string, Block> blocks, int numSplats)
        {
            foreach (string name in kUnconditionalBlocks)
                if (!blocks.ContainsKey(name))
                    throw Fail("MissingField", $"manifest is missing the required block '{name}'");

            foreach (var kv in blocks)
            {
                Block b = kv.Value;
                var shape = kBlockShapes[b.name];
                if (Array.IndexOf(shape.dtypes, b.dtype) < 0)
                    throw Fail("InconsistentHeader",
                               $"block '{b.name}': dtype '{b.dtype}', expected {string.Join(" or ", shape.dtypes)}");
                if (shape.components != 0 && b.components != shape.components)
                    throw Fail("InconsistentHeader",
                               $"block '{b.name}': {b.components} components, expected exactly {shape.components}");
                if (b.name != "joints" && b.count != numSplats)
                    throw Fail("InconsistentHeader",
                               $"block '{b.name}': count {b.count}, expected numSplats {numSplats}");

                bool bounded = Array.IndexOf(kBoundedBlocks, b.name) >= 0;
                // Bounds live in the entry of the block they decode and nowhere else. A block that
                // carries them where it should not is as wrong as one that omits them: either way a
                // decoder cannot tell from an entry alone how to read it.
                if (bounded)
                {
                    RequireBound(b, b.min, "min");
                    RequireBound(b, b.max, "max");
                    for (int c = 0; c < b.components; c++)
                        if (b.max[c] < b.min[c])
                            throw Fail("BadFieldValue",
                                       $"block '{b.name}': component {c} has max {b.max[c]} below min {b.min[c]}, " +
                                       "an inverted span");
                }
                else
                {
                    if (b.min != null || b.max != null)
                        throw Fail("UnexpectedField",
                                   $"block '{b.name}': carries dequantisation bounds, but its range is fixed by its type");
                }

                if (b.name != "boneIndex" && b.palette != null)
                    throw Fail("UnexpectedField", $"block '{b.name}': carries a 'palette', which only 'boneIndex' takes");
            }

            // Checked before anything reads the rig's size: an empty rig is not a rig, and a
            // joints.count of 0 would otherwise surface as every palette entry being out of range.
            if (blocks.TryGetValue("joints", out var joints) && joints.count == 0)
                throw Fail("UnexpectedField", "block 'joints': count 0, and an empty rig is not a rig");

            // Skinning is one fact in two blocks. A bundle with only one of them has no defined
            // meaning, so neither half is guessed at from the other's absence.
            bool hasIndex = blocks.ContainsKey("boneIndex");
            bool hasWeight = blocks.ContainsKey("boneWeight");
            if (hasIndex && !hasWeight)
                throw Fail("MissingField", "manifest has 'boneIndex' but not 'boneWeight'; skinning needs both");
            if (hasWeight && !hasIndex)
                throw Fail("UnexpectedField", "manifest has 'boneWeight' but not 'boneIndex'; skinning needs both");
            if (!hasIndex) return;

            Block idx = blocks["boneIndex"];
            Block w = blocks["boneWeight"];
            if (idx.components < 1)
                throw Fail("InconsistentHeader", $"block 'boneIndex': {idx.components} components, expected at least 1");
            // K - 1 weights are stored and the last is derived, so this is what makes K unambiguous.
            if (w.components != idx.components - 1)
                throw Fail("InconsistentHeader",
                           $"block 'boneWeight': {w.components} components, expected boneIndex.components - 1 = " +
                           $"{idx.components - 1}");
            if (idx.palette == null)
                throw Fail("MissingField", "block 'boneIndex': missing 'palette'");
            // A palette entry is a global rig index, so the rig - when the file carries one - is
            // what says which indices exist.
            if (blocks.TryGetValue("joints", out var rig))
                for (int i = 0; i < idx.palette.Length; i++)
                    if (idx.palette[i] >= rig.count)
                        throw Fail("InconsistentHeader",
                                   $"block 'boneIndex': palette[{i}] is {idx.palette[i]}, outside the " +
                                   $"{rig.count}-bone rig");
        }

        static void RequireBound(Block b, double[] v, string key)
        {
            if (v == null)
                throw Fail("MissingField", $"block '{b.name}': missing dequantisation bound '{key}'");
            // Always an array, one entry per component, even for single-component opacity - which
            // is what lets every bounded block decode with one indexed expression and no special
            // case. A bare number, or an array of the wrong length, would decode some component
            // against nothing.
            if (v.Length != b.components)
                throw Fail("BadFieldValue",
                           $"block '{b.name}': '{key}' has {v.Length} entries, expected {b.components} " +
                           "- one per component");
        }

        // ------------------------------------------------------------------
        // Decode
        // ------------------------------------------------------------------

        public SplatCharacterRest PeekRest(byte[] bytes, int payloadOffset, int payloadLength)
        {
            var blocks = ReadHeader(bytes, payloadOffset, payloadLength, out _);
            // Whether the bundle carries a rig, and how many bones it has, is derived: `joints` is
            // in the manifest, and its `count` is the bone count. There is no numBones key, and a
            // reader must not look for one.
            if (!blocks.TryGetValue("joints", out var joints)) return default;
            return new SplatCharacterRest
            {
                numBones = joints.count,
                jointRestPos = ReadJoints(bytes, joints),
                // Version 1 means corrected; see this file's header. Nothing is read for it
                // because the spec forbids a receipt.
                corrected = true,
            };
        }

        public SplatCharacterBinData Parse(byte[] bytes, int payloadOffset, int payloadLength)
        {
            var blocks = ReadHeader(bytes, payloadOffset, payloadLength, out int numSplats);
            if (numSplats <= 0)
                throw Fail("BadFieldValue", $"numSplats is {numSplats}; there is nothing to draw");

            Block position = blocks["position"];
            Block scale = blocks["scale"];
            Block rotation = blocks["rotation"];
            Block color = blocks["color"];
            Block opacity = blocks["opacity"];
            bool skinned = blocks.ContainsKey("boneIndex");
            bool rigged = blocks.ContainsKey("joints");

            int K = skinned ? blocks["boneIndex"].components : 0;
            if (K > kMaxInfluences)
                throw Fail("InconsistentHeader",
                           $"block 'boneIndex': {K} influences per gaussian, and this package's skinning carries " +
                           $"at most {kMaxInfluences}. Re-encode the character with a smaller K.");

            var data = new SplatCharacterBinData
            {
                numSplats = numSplats,
                // Derived from the manifest, not from a header key: the rig is present when
                // `joints` is, and its row count is the bone count.
                numBones = rigged ? blocks["joints"].count : 0,
                weightsTopK = skinned ? K : 0,
                // Version 1 means corrected; see this file's header. There is no in-file signal to
                // read, and the joints block is therefore the rig these gaussians were baked
                // against - SplatSkeleton.For merges it over the canonical table.
                corrected = true,
                // The position block's own bounds ARE the character's bounding box, so the reader
                // needs no pass over the decoded positions to find them.
                boundsMin = new Vector3((float)position.min[0], (float)position.min[1], (float)position.min[2]),
                boundsMax = new Vector3((float)position.max[0], (float)position.max[1], (float)position.max[2]),
            };
            // The bounds are in the source frame, so they need the same conversion as the positions
            // they bound. Leaving them unconverted mirrors bounds.z, which is easy to miss because
            // a character is roughly symmetric about z.
            SplatConvention.Bounds(ref data.boundsMin, ref data.boundsMax);

            int N = numSplats;
            data.positions = new NativeArray<float3>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.scales = new NativeArray<float3>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.rotations = new NativeArray<quaternion>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.colors = new NativeArray<Color32>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.opacities = new NativeArray<float>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.boneIndices = new NativeArray<uint4>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.boneWeights = new NativeArray<float4>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            try
            {
                DecodePositions(bytes, position, data);
                DecodeScales(bytes, scale, data);
                DecodeRotations(bytes, rotation, data);
                // Opacity before colour: the renderer reads the colour's alpha channel as the
                // opacity, so the colour pass needs the decoded alpha to bake in.
                DecodeOpacities(bytes, opacity, data);
                DecodeColors(bytes, color, data);
                if (skinned) DecodeSkinning(bytes, blocks["boneIndex"], blocks["boneWeight"], K, data);
                else FillUnskinned(data);
                if (rigged) DecodeJoints(bytes, blocks["joints"], data);
            }
            catch
            {
                // The arrays are Allocator.Persistent, so a throw partway through would leak them
                // for the process's lifetime - and Parse's callers have no handle to dispose.
                data.Dispose();
                throw;
            }
            return data;
        }

        // position, scale and opacity share one decode - min[c] + q * (max[c] - min[c]) / (levels - 1)
        // over the component index of whichever block is being read. Nothing distinguishes the
        // single-component block from the three-component ones, which is why opacity's bounds are
        // one-element arrays like everybody else's.
        //
        // The three still land in different places, and in different units, so each gets its own
        // loop rather than a shared one writing through an indirection: SplatCharacterBinData wants
        // a float3 of Unity-convention position, a float3 of LINEAR scale, and a float of alpha.

        static void DecodePositions(byte[] bytes, Block b, SplatCharacterBinData data)
        {
            int n = data.numSplats;
            int ox = b.ComponentOffset(0), oy = b.ComponentOffset(1), oz = b.ComponentOffset(2);
            double minX = b.min[0], minY = b.min[1], minZ = b.min[2];
            double stepX = (b.max[0] - minX) / (kPositionLevels - 1);
            double stepY = (b.max[1] - minY) / (kPositionLevels - 1);
            double stepZ = (b.max[2] - minZ) / (kPositionLevels - 1);
            for (int i = 0; i < n; i++)
            {
                float px = (float)(minX + BitConverter.ToUInt16(bytes, ox + i * 2) * stepX);
                float py = (float)(minY + BitConverter.ToUInt16(bytes, oy + i * 2) * stepY);
                float pz = (float)(minZ + BitConverter.ToUInt16(bytes, oz + i * 2) * stepZ);
                // Source to Unity convention (handedness + forward axis) in one step, the same one
                // the VFCHARB1 path applies; see SplatConvention.
                data.positions[i] = SplatConvention.Position(px, py, pz);
            }
        }

        static void DecodeScales(byte[] bytes, Block b, SplatCharacterBinData data)
        {
            int n = data.numSplats;
            int ox = b.ComponentOffset(0), oy = b.ComponentOffset(1), oz = b.ComponentOffset(2);
            double minX = b.min[0], minY = b.min[1], minZ = b.min[2];
            double stepX = (b.max[0] - minX) / (kByteLevels - 1);
            double stepY = (b.max[1] - minY) / (kByteLevels - 1);
            double stepZ = (b.max[2] - minZ) / (kByteLevels - 1);
            for (int i = 0; i < n; i++)
            {
                // The stored value is a LOG-space per-axis standard deviation. VFCHARB1 hands back
                // a linear one and the asset builder writes it straight into the GPU record, so the
                // exp happens here, once, rather than in every consumer.
                double sx = minX + bytes[ox + i] * stepX;
                double sy = minY + bytes[oy + i] * stepY;
                double sz = minZ + bytes[oz + i] * stepZ;
                // Scales are deliberately left in the source axis order: SplatConvention carries
                // the mirror in the ROTATION, and conjugating by a diagonal +/-1 matrix leaves a
                // diagonal scale invariant.
                data.scales[i] = new float3((float)Math.Exp(sx), (float)Math.Exp(sy), (float)Math.Exp(sz));
            }
        }

        static void DecodeRotations(byte[] bytes, Block b, SplatCharacterBinData data)
        {
            int n = data.numSplats;
            int ox = b.ComponentOffset(0), oy = b.ComponentOffset(1), oz = b.ComponentOffset(2);
            const float kStep = 2f / 255f;
            for (int i = 0; i < n; i++)
            {
                // Three components, not four. The writer negated any quaternion with w < 0 before
                // quantising, which is what makes w recoverable from xyz. Order is xyzw.
                float qx = bytes[ox + i] * kStep - 1f;
                float qy = bytes[oy + i] * kStep - 1f;
                float qz = bytes[oz + i] * kStep - 1f;
                float qw = math.sqrt(math.max(0f, 1f - qx * qx - qy * qy - qz * qz));
                // Normalised, and not only for tidiness: quantising xyz can push x^2+y^2+z^2 just
                // past 1, which clamps w to 0 and leaves the quaternion longer than unit. The
                // renderer's smallest-three packing reconstructs the dropped component from the
                // other three assuming unit length, and the skinning shader treats the quaternion
                // as a rotation - a long one would scale the splat. Exactly unit already in the
                // ordinary case, so this costs one rsqrt and changes nothing.
                float4 q = math.normalize(new float4(qx, qy, qz, qw));
                data.rotations[i] = SplatConvention.Rotation(q.x, q.y, q.z, q.w);
            }
        }

        static void DecodeOpacities(byte[] bytes, Block b, SplatCharacterBinData data)
        {
            int n = data.numSplats;
            int o = b.ComponentOffset(0);
            double min = b.min[0];
            double step = (b.max[0] - min) / (kByteLevels - 1);
            for (int i = 0; i < n; i++)
            {
                // A LOGIT, not an alpha - the same trade scale makes, so that consumers who want
                // the transformed value pay a multiply-add rather than a transcendental. This
                // package's consumers want the alpha, so the sigmoid happens here.
                double logit = min + bytes[o + i] * step;
                data.opacities[i] = (float)(1.0 / (1.0 + Math.Exp(-logit)));
            }
        }

        static void DecodeColors(byte[] bytes, Block b, SplatCharacterBinData data)
        {
            int n = data.numSplats;
            int or_ = b.ComponentOffset(0), og = b.ComponentOffset(1), ob = b.ComponentOffset(2);
            for (int i = 0; i < n; i++)
            {
                // The stored byte is the spherical-harmonic DC term, decoded as
                // (q/255 - 0.5) / SH_C0. The engine's own inverse of that is SH_C0 * dc + 0.5,
                // which is exactly q/255 - so the byte IS the 0..1 colour and round-tripping it
                // through kShC0 would only add float error. kShC0 is kept as documentation of why
                // no arithmetic is needed here. There is no higher SH band; characters are lit flat.
                byte r = bytes[or_ + i], g = bytes[og + i], bl = bytes[ob + i];
                // Bake opacity into the alpha channel; the renderer reads col.a as opacity.
                byte a = (byte)math.clamp((int)math.round(math.saturate(data.opacities[i]) * 255f), 0, 255);
                data.colors[i] = new Color32(r, g, bl, a);
            }
        }

        static void DecodeSkinning(byte[] bytes, Block idx, Block w, int K, SplatCharacterBinData data)
        {
            int n = data.numSplats;
            int[] palette = idx.palette;

            // Indices first, into a scratch array, so the palette range check fails before any of
            // the weight arithmetic runs.
            var slotIdx = new uint[kMaxInfluences];
            var slotW = new float[kMaxInfluences];
            int[] idxOffsets = new int[K];
            for (int j = 0; j < K; j++) idxOffsets[j] = idx.ComponentOffset(j);
            int[] wOffsets = new int[w.components];
            for (int j = 0; j < w.components; j++) wOffsets[j] = w.ComponentOffset(j);
            bool wide = idx.dtype == "u16";

            for (int i = 0; i < n; i++)
            {
                // K - 1 weights are stored and the last is whatever is left. The writer allocated
                // the K byte weights by largest remainder so they total 255, so summing the BYTES
                // and dividing once evaluates 1 - sum(q/255) with no rounding drift.
                int stored = 0;
                for (int j = 0; j < K; j++)
                {
                    // The palette is what lets a narrow character on a 441-bone rig store its
                    // indices in a byte. It is present even when it is the identity mapping, so
                    // there is only ever one index space to read - and its dtype is per-file, so
                    // it is read from the entry rather than assumed.
                    int q = wide ? BitConverter.ToUInt16(bytes, idxOffsets[j] + i * 2) : bytes[idxOffsets[j] + i];
                    if (q >= palette.Length)
                        throw Fail("InconsistentHeader",
                                   $"block 'boneIndex': gaussian {i} slot {j} stores index {q}, outside its " +
                                   $"{palette.Length}-entry palette");
                    slotIdx[j] = (uint)palette[q];
                    if (j < w.components)
                    {
                        byte q8 = bytes[wOffsets[j] + i];
                        stored += q8;
                        slotW[j] = q8 * (1f / 255f);
                    }
                    else
                    {
                        slotW[j] = math.max(0, 255 - stored) * (1f / 255f);
                    }
                }
                // A file with fewer than four influences leaves the spare slots pointing at slot
                // 0's bone with zero weight, which is the same "no bone" spelling the encoder uses
                // and keeps the skinning shader's unconditional _BonePose[idx] read in range.
                for (int j = K; j < kMaxInfluences; j++)
                {
                    slotIdx[j] = slotIdx[0];
                    slotW[j] = 0f;
                }
                data.boneIndices[i] = new uint4(slotIdx[0], slotIdx[1], slotIdx[2], slotIdx[3]);
                data.boneWeights[i] = new float4(slotW[0], slotW[1], slotW[2], slotW[3]);
            }
        }

        // An unskinned bundle still has to fill these: the skinning path reads boneIndices and
        // boneWeights unconditionally, and an uninitialised NativeArray would send garbage bone
        // indices to the GPU. Bone 0 with weight 1 is the identity - every splat rides the root.
        static void FillUnskinned(SplatCharacterBinData data)
        {
            var idx = new uint4(0, 0, 0, 0);
            var w = new float4(1f, 0f, 0f, 0f);
            for (int i = 0; i < data.numSplats; i++)
            {
                data.boneIndices[i] = idx;
                data.boneWeights[i] = w;
            }
        }

        static void DecodeJoints(byte[] bytes, Block b, SplatCharacterBinData data)
        {
            data.jointRestPos = new NativeArray<float3>(ReadJoints(bytes, b), Allocator.Persistent);
        }

        /// <summary>
        /// The joints block as parent-local rest translations in Unity's convention. Shared by the
        /// full decode and by the peek, so the importer's rig and the runtime's cannot be read
        /// from different bytes.
        /// </summary>
        static float3[] ReadJoints(byte[] bytes, Block b)
        {
            int B = b.count;
            int ox = b.ComponentOffset(0), oy = b.ComponentOffset(1), oz = b.ComponentOffset(2);
            var joints = new float3[B];
            for (int j = 0; j < B; j++)
            {
                // Row j is bone j's rest translation relative to its PARENT, not its position in
                // the world - which is what jointRestPos has always held. Same source -> Unity
                // conversion as the splats: the conversion is a reflection, and mirroring every
                // local transform of a hierarchy reproduces the mirrored world pose.
                joints[j] = SplatConvention.Position(
                    BitConverter.ToSingle(bytes, ox + j * 4),
                    BitConverter.ToSingle(bytes, oy + j * 4),
                    BitConverter.ToSingle(bytes, oz + j * 4));
            }
            return joints;
        }

        // ------------------------------------------------------------------
        // Header field readers
        // ------------------------------------------------------------------

        static int SizeOfDtype(string dtype)
        {
            switch (dtype)
            {
                case "f32": return 4;
                case "u16": return 2;
                case "u8": return 1;
                default: return 0;
            }
        }

        static int ReadRequiredUint(SplatJsonValue owner, string where, string key)
        {
            if (!owner.TryGet(key, out var v))
                throw Fail("MissingField", $"{where}: missing required key '{key}'");
            if (!TryReadUint(v, out int n))
                throw Fail("BadFieldValue",
                           $"{where}: '{key}' is {v.Describe()}, expected a non-negative integer");
            return n;
        }

        static bool TryReadUint(SplatJsonValue v, out int n)
        {
            n = 0;
            if (!v.IsNumber) return false;
            double d = v.AsDouble;
            if (d < 0 || d > int.MaxValue || Math.Floor(d) != d) return false;
            n = (int)d;
            return true;
        }

        static double[] ReadOptionalNumberArray(SplatJsonValue entry, string where, string key)
        {
            if (!entry.TryGet(key, out var v)) return null;
            if (!v.IsArray)
                throw Fail("BadFieldValue", $"{where}: '{key}' is {v.Describe()}, expected an array of numbers");
            var a = new double[v.Count];
            for (int i = 0; i < a.Length; i++)
            {
                if (!v[i].IsNumber || double.IsNaN(v[i].AsDouble) || double.IsInfinity(v[i].AsDouble))
                    throw Fail("BadFieldValue", $"{where}: '{key}'[{i}] is {v[i].Describe()}, expected a finite number");
                a[i] = v[i].AsDouble;
            }
            return a;
        }

        static int[] ReadOptionalPalette(SplatJsonValue entry, string where)
        {
            if (!entry.TryGet("palette", out var v)) return null;
            if (!v.IsArray)
                throw Fail("BadFieldValue", $"{where}: 'palette' is {v.Describe()}, expected an array of rig indices");
            var a = new int[v.Count];
            for (int i = 0; i < a.Length; i++)
            {
                if (!TryReadUint(v[i], out a[i]))
                    throw Fail("BadFieldValue",
                               $"{where}: 'palette'[{i}] is {v[i].Describe()}, expected a non-negative rig index");
            }
            return a;
        }

        static InvalidOperationException Fail(string category, string message) =>
            SplatCharacterViggChar.Error(category, message);
    }
}
