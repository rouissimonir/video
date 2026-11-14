// SPDX-License-Identifier: MIT
// The "VFCHARB1" container: the original interleaved per-splat record format, and the whole of
// its decode.
//
// THE POINT OF THIS FILE IS THAT IT IS ONE FILE. Retiring VFCHARB1 is deleting it and the one arm
// of SplatCharacterBin's dispatch that names it - nothing else in the package changes, because
// nothing else in the package reads a byte of this container. What it hands back is the same
// SplatCharacterBinData every other reader produces, so no caller knows or asks which container
// arrived. It imports nothing from the VIGGCHAR reader and the VIGGCHAR reader imports nothing
// from it.
//
// These readers are PERMANENT, not migration scaffolding. Existing projects have these bytes
// baked into imported assets, files already in storage keep their old magic forever, and the
// policy is that consumers stay backward compatible. So the decode here is MOVED, never improved:
// not one line of it changed when VIGGCHAR arrived and took the magic compare off the front of
// it, and not one line changed when it moved into this file.
//
// TWO VERSIONS, one lineage, spelled in the JSON header rather than the container - which is
// precisely the accretion VIGGCHAR exists to end, and precisely why nothing about that lineage
// leaks past this file:
//   version 1  fp32. Splats stride 48 (pos/scale/rot/opacity float32, rgb+pad uint8); weights
//              stride 24 (topK uint16 indices + topK float32 weights).
//   version 2  fp16, also spelled encoding.precision "fp16". Splats stride 26 (pos/scale/rot/
//              opacity half-float, rgb+pad still uint8); weights stride 16 (uint16 indices
//              unchanged, half-float weights). Joints stay float32 in both.
// Every stride is header-driven, so those numbers are the fallback when a chunk omits its own.
//
// File layout:
//   [0..8)   magic  "VFCHARB1" (8 ASCII bytes) - checked by SplatCharacterMagic before we get here
//   [8..12)  uint32 headerLength (little-endian)
//   [12..12+headerLength)  UTF-8 JSON header
//   [base..) binary chunks, where base = 12 + headerLength
//
// Header (see example):
//   {
//     "format":"vfa3d-charbin", "version":1,
//     "numSplats":N, "numBones":B,
//     "bounds":{"min":[x,y,z],"max":[x,y,z]},
//     "encoding":{ "weights":{topK,indexType,weightType,stride}, "color":{format,levels} },
//     "chunks":{
//        "splats":{offset,length,stride:48},
//        "weights":{offset,length,stride:24},
//        "joints":{offset,length,stride:12}
//     },
//     "joints":{count,format,stride:12}
//   }
//
// Per-splat record (stride 48):
//   pos     float3   (12 bytes)
//   scale   float3   (12 bytes)   -- LINEAR scale (already exp'd)
//   rot     float4   (16 bytes)   -- unit quaternion, xyzw order
//   color   rgba8    ( 4 bytes)   -- alpha byte unused (always 255)
//   opacity float    ( 4 bytes)   -- 0..1
//
// Per-splat weights (stride 24, topK=4):
//   boneIndex uint16 x4 ( 8 bytes)
//   boneWeight float  x4 (16 bytes)  -- sum to 1
//
// Joints chunk (stride 12, count = numBones):
//   bind-pose position float3 -- rest translations only (rotations come from the
//   embedded skeleton table in VfBoneSkeleton; the .bin omits rest rotations).

// numBones says which rig the per-splat bone indices address: 86 for the body-only rig, where
// each facial bone's weight is folded onto its nearest body bone, or 441 for the full body +
// facial rig. Parsing is identical either way, since every count is header-driven, and the rig
// is chosen downstream by SplatSkeleton.For(numBones). A 441-bone file must not be skinned
// against the body rig, or every index >= 86 clamps onto the last body bone.

using System;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Viggle.SplatEngine
{
    internal struct SplatChunkRange
    {
        public int offset;
        public int length;
        public int stride;
    }

    /// <summary>
    /// The VFCHARB1 reader: interleaved per-splat records, v1 fp32 or v2 fp16. Total over its own
    /// container, and unaware that any other exists.
    /// </summary>
    internal static class SplatCharacterVfCharB1
    {
        // The magic constants live in SplatCharacterMagic, and only there. This reader used to
        // carry its own copy and the editor importer carried a second one, which is how editor
        // import came to check for a format the runtime reader no longer insisted on.

        struct Header
        {
            public string format;
            public int version;
            public int numSplats;
            public int numBones;
            public Vector3 boundsMin;
            public Vector3 boundsMax;
            public int weightsTopK;
            public string precision;
            public SplatChunkRange splats;
            public SplatChunkRange weights;
            public SplatChunkRange joints;
        }

        /// <summary>
        /// Decodes a VFCHARB1 file into the arrays every reader in this package produces. Throws
        /// <see cref="InvalidOperationException"/> on malformed input.
        /// </summary>
        public static SplatCharacterBinData Parse(byte[] bytes)
        {
            if (bytes.Length < 12)
                throw new InvalidOperationException("SplatCharacterBin: file too small");

            int headerLen = BitConverter.ToInt32(bytes, 8);
            int baseOff = 12 + headerLen;
            if (baseOff > bytes.Length)
                throw new InvalidOperationException("SplatCharacterBin: header length exceeds file");

            string json = Encoding.UTF8.GetString(bytes, 12, headerLen);
            Header h = ParseHeader(json);
            if (h.numSplats <= 0)
                throw new InvalidOperationException("SplatCharacterBin: numSplats <= 0");

            var data = new SplatCharacterBinData
            {
                numSplats = h.numSplats,
                numBones = h.numBones,
                weightsTopK = h.weightsTopK > 0 ? h.weightsTopK : 4,
                boundsMin = h.boundsMin,
                boundsMax = h.boundsMax,
                // Read off the header, not assumed: these containers predate the corrections, so
                // most of them were never corrected and must keep the canonical rest tables. See
                // SplatCharacterRest for the two spellings and why neither is optional.
                corrected = HasCorrectionReceipt(json),
            };
            // The header bounds are in the source frame, so they need the same conversion as the
            // positions they bound. Leaving them unconverted mirrors bounds.z, which is easy to
            // miss because a character is roughly symmetric about z.
            SplatConvention.Bounds(ref data.boundsMin, ref data.boundsMax);

            bool fp16 = h.version >= 2 || h.precision == "fp16";

            int N = h.numSplats;
            data.positions = new NativeArray<float3>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.scales = new NativeArray<float3>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.rotations = new NativeArray<quaternion>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.colors = new NativeArray<Color32>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.opacities = new NativeArray<float>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.boneIndices = new NativeArray<uint4>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.boneWeights = new NativeArray<float4>(N, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            // ---- splats chunk ----
            {
                var c = h.splats;
                int stride = c.stride > 0 ? c.stride : (fp16 ? 26 : 48);
                int off = baseOff + c.offset;
                if (off + N * stride > bytes.Length)
                    throw new InvalidOperationException("SplatCharacterBin: splats chunk out of range");
                for (int i = 0; i < N; i++)
                {
                    int p = off + i * stride;
                    float px, py, pz, sx, sy, sz, qx, qy, qz, qw, opacity;
                    byte r, g, b;
                    if (fp16)
                    {
                        // v2 layout (stride 26): pos/scale/rot/opacity half-floats; rgb+pad uint8.
                        px = ReadHalf(bytes, p + 0);  py = ReadHalf(bytes, p + 2);  pz = ReadHalf(bytes, p + 4);
                        sx = ReadHalf(bytes, p + 6);  sy = ReadHalf(bytes, p + 8);  sz = ReadHalf(bytes, p + 10);
                        qx = ReadHalf(bytes, p + 12); qy = ReadHalf(bytes, p + 14); qz = ReadHalf(bytes, p + 16); qw = ReadHalf(bytes, p + 18);
                        r = bytes[p + 20]; g = bytes[p + 21]; b = bytes[p + 22];
                        // bytes[p+23] is the unused pad (255)
                        opacity = ReadHalf(bytes, p + 24);
                    }
                    else
                    {
                        // v1 layout (stride 48): all float32.
                        px = BitConverter.ToSingle(bytes, p + 0);
                        py = BitConverter.ToSingle(bytes, p + 4);
                        pz = BitConverter.ToSingle(bytes, p + 8);
                        sx = BitConverter.ToSingle(bytes, p + 12);
                        sy = BitConverter.ToSingle(bytes, p + 16);
                        sz = BitConverter.ToSingle(bytes, p + 20);
                        qx = BitConverter.ToSingle(bytes, p + 24);
                        qy = BitConverter.ToSingle(bytes, p + 28);
                        qz = BitConverter.ToSingle(bytes, p + 32);
                        qw = BitConverter.ToSingle(bytes, p + 36);
                        r = bytes[p + 40];
                        g = bytes[p + 41];
                        b = bytes[p + 42];
                        // bytes[p+43] is the unused alpha (255)
                        opacity = BitConverter.ToSingle(bytes, p + 44);
                    }

                    // Source to Unity convention (handedness + forward axis) in one step; see
                    // SplatConvention. Everything downstream reads Unity-convention data, so the
                    // renderer's bind pose and the skinned pose agree without further correction.
                    // Scales are left as authored: the rotation carries the mirror.
                    data.positions[i] = SplatConvention.Position(px, py, pz);
                    data.scales[i] = new float3(sx, sy, sz);
                    data.rotations[i] = SplatConvention.Rotation(qx, qy, qz, qw);
                    // Bake opacity into the color alpha channel; the renderer reads col.a as opacity.
                    byte a = (byte)math.clamp((int)math.round(math.saturate(opacity) * 255f), 0, 255);
                    data.colors[i] = new Color32(r, g, b, a);
                    data.opacities[i] = opacity;
                }
            }

            // ---- weights chunk ----
            {
                var c = h.weights;
                int topK = data.weightsTopK;
                // Weight element is a half in v2 (fp16 LODs), a float32 in v1.
                int wsize = fp16 ? 2 : 4;
                int stride = c.stride > 0 ? c.stride : (topK * 2 + topK * wsize);
                int off = baseOff + c.offset;
                if (off + N * stride > bytes.Length)
                    throw new InvalidOperationException("SplatCharacterBin: weights chunk out of range");
                for (int i = 0; i < N; i++)
                {
                    int p = off + i * stride;
                    // topK uint16 indices (unchanged across versions), then topK weights
                    // (float32 in v1, float16 in v2).
                    uint i0 = topK > 0 ? BitConverter.ToUInt16(bytes, p + 0) : 0u;
                    uint i1 = topK > 1 ? BitConverter.ToUInt16(bytes, p + 2) : 0u;
                    uint i2 = topK > 2 ? BitConverter.ToUInt16(bytes, p + 4) : 0u;
                    uint i3 = topK > 3 ? BitConverter.ToUInt16(bytes, p + 6) : 0u;
                    int wbase = p + topK * 2;
                    float w0 = topK > 0 ? ReadWeight(bytes, wbase + 0 * wsize, fp16) : 0f;
                    float w1 = topK > 1 ? ReadWeight(bytes, wbase + 1 * wsize, fp16) : 0f;
                    float w2 = topK > 2 ? ReadWeight(bytes, wbase + 2 * wsize, fp16) : 0f;
                    float w3 = topK > 3 ? ReadWeight(bytes, wbase + 3 * wsize, fp16) : 0f;
                    data.boneIndices[i] = new uint4(i0, i1, i2, i3);
                    data.boneWeights[i] = new float4(w0, w1, w2, w3);
                }
            }

            // ---- joints chunk (rest translations) ----
            float3[] joints = ReadJoints(bytes, baseOff, h.joints, h.numBones);
            if (joints != null)
                data.jointRestPos = new NativeArray<float3>(joints, Allocator.Persistent);

            return data;
        }

        /// <summary>
        /// The joints chunk as parent-local rest translations in Unity's convention, or null when
        /// the file carries none or the chunk does not fit. Shared by the full decode and by the
        /// peek, so the importer's rig and the runtime's cannot be read from different bytes.
        /// </summary>
        static float3[] ReadJoints(byte[] bytes, int baseOff, SplatChunkRange c, int numBones)
        {
            if (c.length <= 0 || numBones <= 0) return null;
            int stride = c.stride > 0 ? c.stride : 12;
            int off = baseOff + c.offset;
            if (off < 0 || (long)off + (long)numBones * stride > bytes.LongLength) return null;

            var joints = new float3[numBones];
            for (int j = 0; j < numBones; j++)
            {
                int p = off + j * stride;
                // Same source -> Unity conversion as the splats, so joint rest positions stay in
                // the frame the splats live in.
                joints[j] = SplatConvention.Position(
                    BitConverter.ToSingle(bytes, p + 0),
                    BitConverter.ToSingle(bytes, p + 4),
                    BitConverter.ToSingle(bytes, p + 8));
            }
            return joints;
        }

        /// <summary>
        /// Did this VFCHARB1 header claim the corrections ran? Two spellings, both accepted; see
        /// <see cref="SplatCharacterRest"/> for which writer emits which and why `groundOffset` is
        /// not one of them.
        /// </summary>
        /// <remarks>
        /// Read through the package's own JSON parser rather than the header mirrors below:
        /// JsonUtility zero-fills what the JSON omits, so it cannot tell an absent receipt from a
        /// present but empty one - and absent is the whole answer here. An unparseable header is
        /// not corrected, which is the safe way to be wrong: the character keeps the rest pose it
        /// has always rendered with.
        ///
        /// A receipt is a property of THIS container only. VIGGCHAR version 1 carries none - the
        /// version means corrected, so there is no question left to ask - and nothing here is
        /// shared with that path.
        /// </remarks>
        static bool HasCorrectionReceipt(string json)
        {
            try
            {
                var root = SplatJson.Parse(json);
                return root.IsObject && (IsPresent(root, "corrections") || IsPresent(root, "ankleCorrection"));
            }
            catch
            {
                return false;
            }

            static bool IsPresent(SplatJsonValue owner, string key) =>
                owner.TryGet(key, out var v) && v.Kind != SplatJsonKind.Null;
        }

        /// <summary>
        /// The rig's rest pose - bone count, joints and correction receipt - without decoding a
        /// single splat. All-zero when the bytes carry no rig or will not read; never throws.
        /// </summary>
        public static SplatCharacterRest PeekRest(byte[] bytes)
        {
            try
            {
                if (bytes.Length < 12) return default;
                int headerLen = BitConverter.ToInt32(bytes, 8);
                if (headerLen <= 0 || 12L + headerLen > bytes.LongLength) return default;
                // Deliberately not ParseHeader: a peek answers what it can off a header it does
                // not otherwise vet. The importer needs the rig even from a file whose chunks will
                // not decode - that is what puts a rigged character and a console error on screen
                // instead of an inert asset.
                string json = Encoding.UTF8.GetString(bytes, 12, headerLen);
                var root = JsonUtility.FromJson<HeaderJson>(json);
                if (root == null) return default;
                return new SplatCharacterRest
                {
                    numBones = root.numBones,
                    jointRestPos = ReadJoints(bytes, 12 + headerLen, ReadChunk(root.chunks?.joints), root.numBones),
                    corrected = HasCorrectionReceipt(json),
                };
            }
            catch
            {
                // Unreadable is not exceptional here: the importer claims .bin globally and will
                // hand this whatever a host project happens to keep under that extension.
                return default;
            }
        }

        // ---- header JSON mirrors ----
        //
        // Deserialised with UnityEngine.JsonUtility rather than a third-party JSON library, so the
        // package adds no dependency a host project could conflict with. Every key is fixed and
        // every value is a scalar, string, or float[3], which is all JsonUtility handles; unknown
        // keys are ignored.
        //
        // The cost is that JsonUtility zero-fills anything the JSON omits, so "absent" and "present
        // but zero" are indistinguishable here. ParseHeader re-imposes the required/optional split
        // explicitly, treating an all-zero object or an empty array as absent.

        [Serializable]
        class HeaderJson
        {
            public string format;
            public int version;
            public int numSplats;
            public int numBones;
            public BoundsJson bounds;
            public EncodingJson encoding;
            public ChunksJson chunks;
        }

        [Serializable]
        class BoundsJson
        {
            public float[] min;
            public float[] max;
        }

        [Serializable]
        class EncodingJson
        {
            public WeightsEncodingJson weights;
            public string precision;
        }

        [Serializable]
        class WeightsEncodingJson
        {
            public int topK;
        }

        [Serializable]
        class ChunksJson
        {
            public ChunkJson splats;
            public ChunkJson weights;
            public ChunkJson joints;
        }

        [Serializable]
        class ChunkJson
        {
            public int offset;
            public int length;
            public int stride;
        }

        static Header ParseHeader(string json)
        {
            HeaderJson root;
            try
            {
                root = JsonUtility.FromJson<HeaderJson>(json);
            }
            catch (Exception e)
            {
                // JsonUtility reports malformed JSON as ArgumentException; callers of Parse only
                // ever have to handle InvalidOperationException.
                throw new InvalidOperationException($"SplatCharacterBin: malformed header JSON: {e.Message}");
            }
            if (root == null)
                throw new InvalidOperationException("SplatCharacterBin: malformed header JSON: empty header");

            BoundsJson bounds = root.bounds;
            if (bounds == null || (IsAbsent(bounds.min) && IsAbsent(bounds.max)))
                throw new InvalidOperationException("SplatCharacterBin: missing header field 'bounds'");
            ChunksJson chunks = root.chunks;
            if (chunks == null || (IsAbsent(chunks.splats) && IsAbsent(chunks.weights) && IsAbsent(chunks.joints)))
                throw new InvalidOperationException("SplatCharacterBin: missing header field 'chunks'");
            // A zero count is indistinguishable from an absent one, and both are unusable.
            if (root.numSplats <= 0)
                throw new InvalidOperationException("SplatCharacterBin: missing header field 'numSplats'");
            if (IsAbsent(bounds.min))
                throw new InvalidOperationException("SplatCharacterBin: missing header field 'min'");
            if (IsAbsent(bounds.max))
                throw new InvalidOperationException("SplatCharacterBin: missing header field 'max'");
            if (IsAbsent(chunks.splats))
                throw new InvalidOperationException("SplatCharacterBin: missing header field 'splats'");
            if (IsAbsent(chunks.weights))
                throw new InvalidOperationException("SplatCharacterBin: missing header field 'weights'");

            return new Header
            {
                format = root.format,
                version = root.version,
                numSplats = root.numSplats,
                numBones = root.numBones,
                boundsMin = ReadVec3(bounds.min, "bounds.min"),
                boundsMax = ReadVec3(bounds.max, "bounds.max"),
                weightsTopK = root.encoding?.weights?.topK ?? 0,
                precision = root.encoding?.precision,
                splats = ReadChunk(chunks.splats),
                weights = ReadChunk(chunks.weights),
                joints = ReadChunk(chunks.joints),
            };
        }

        static bool IsAbsent(float[] a) => a == null || a.Length == 0;

        // JsonUtility cannot report a missing object, only a default-constructed one, so a chunk
        // with nothing in it counts as absent. Real chunks always carry a length and a stride, and
        // the "splats" chunk is the only one whose offset is legitimately 0.
        static bool IsAbsent(ChunkJson c) => c == null || (c.offset == 0 && c.length == 0 && c.stride == 0);

        static Vector3 ReadVec3(float[] a, string name)
        {
            if (a == null || a.Length < 3)
                throw new InvalidOperationException($"SplatCharacterBin: header field '{name}' must be a float[3]");
            return new Vector3(a[0], a[1], a[2]);
        }

        static SplatChunkRange ReadChunk(ChunkJson c)
        {
            if (c == null)
                return default;
            return new SplatChunkRange
            {
                offset = c.offset,
                length = c.length,
                stride = c.stride,
            };
        }

        // Read a half-float (fp16) at byte offset o and widen to float32.
        static float ReadHalf(byte[] b, int o) => math.f16tof32(BitConverter.ToUInt16(b, o));

        // Read a per-splat bone weight: fp16 half in v2 bundles, float32 in v1.
        static float ReadWeight(byte[] b, int o, bool fp16) =>
            fp16 ? math.f16tof32(BitConverter.ToUInt16(b, o)) : BitConverter.ToSingle(b, o);
    }
}
