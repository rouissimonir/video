// SPDX-License-Identifier: MIT
// Builds a runtime SplatAsset in the exact packed layouts SplatRenderer's
// CreateResourcesForAsset expects. Both content types come through here, and both are built at
// LOAD time: a character from decoded SplatCharacterBinData, a static capture from the packed
// sections of an .spz. Nothing in this package converts splats at import any more.
//
// THE TWO PATHS PICK DIFFERENT FORMATS, AND THE REASON IS NOT SIZE
//
// A character is skinned every frame: the compute pass overwrites its positions and rotations
// wholesale, so quantizing the ones baked into the asset would buy nothing and cost precision in
// the frustum cull and the sort's depth range. It also carries no spherical harmonics at all.
// So it is stored losslessly and unchunked, at 32 bytes a splat plus a zeroed SH block.
//
// A capture is static: what is stored is what is drawn, forever, and it is typically ten times
// the splat count of a character. So it is quantized, which needs per-chunk min/max, which is
// what the chunk pass is for. See BuildCapture for the byte budget.
//
// Character formats (no chunk dequantization needed):
//   pos    : Float32   -> 12 bytes/splat, absolute positions
//   scale  : Float32   -> 12 bytes/splat, linear scale used directly by the shader
//   color  : Norm8x4   -> RGBA8 texture, Morton-16x16 swizzled
//   sh     : Norm6     -> 32 bytes/SH item, all zeros since SHOrder is forced to 0
//   chunks : none      -> absolute pos/scale, no per-chunk min/max
//
// otherData is 16 bytes per splat: rot 10_10_10_2 (4 bytes) + scale float3 (12 bytes). There is no
// SH index because shFmt Norm6 is within VECTOR_FMT_6.

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Viggle.SplatEngine.Jobs;

namespace Viggle.SplatEngine
{
    internal static class SplatRenderAssetBuilder
    {
        const int kTexWidth = 2048; // must match SplatAsset.kTextureWidth
        const int kChunkSize = 256; // must match SplatAsset.kChunkSize

        internal static SplatAsset Build(SplatCharacterBinData d)
        {
            int n = d.numSplats;

            var asset = ScriptableObject.CreateInstance<SplatAsset>();
            asset.name = "SplatCharacterSplatAsset";
            // Runtime-only asset, rebuilt on every load. HideAndDontSave here and on the data
            // TextAssets below keeps the [ExecuteAlways] renderer's edit-mode build out of scene
            // and prefab serialization: a binary blob baked into a text-YAML scene comes back as
            // escaped byte strings that Unity's own parser then rejects, corrupting the scene.
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.Initialize(
                n,
                SplatAsset.VectorFormat.Float32,  // pos
                SplatAsset.VectorFormat.Float32,  // scale
                SplatAsset.ColorFormat.Norm8x4,   // color
                SplatAsset.SHFormat.Norm6,        // sh (zeros)
                d.boundsMin, d.boundsMax,
                Array.Empty<SplatAsset.CameraInfo>());
            // A character file carries no spherical harmonics - the SH section above is zeros.
            // Recording degree 0 is what makes SplatRenderer.SHOrderEffective clamp to 0 for this
            // asset no matter what the (now inherited, now public) shOrder field is set to. The
            // default is 3, so without this a host could set shOrder on a character and have the
            // shader index a fixed-stride SH buffer full of zeros - harmless here only because the
            // buffer is full size, and not something to leave resting on that.
            asset.SetSHDegree(0);

            var posBytes = BuildPosData(d);
            var otherBytes = BuildOtherData(d);
            var colorBytes = BuildColorData(d);
            var shBytes = BuildShData(n);

#if UNITY_6000_0_OR_NEWER
            // Unity 6: store the blobs in TextAssets via the binary TextAsset(ReadOnlySpan<byte>)
            // constructor, matching how a serialized SplatAsset is laid out.
            var posTA = MakeTextAsset(posBytes, "SplatCharacterPos");
            var otherTA = MakeTextAsset(otherBytes, "SplatCharacterOther");
            var colorTA = MakeTextAsset(colorBytes, "SplatCharacterColor");
            var shTA = MakeTextAsset(shBytes, "SplatCharacterSH");

            // No chunk data (lossless absolute formats).
            asset.SetAssetFiles(null, posTA, otherTA, colorTA, shTA);
#else
            // Pre-Unity-6 (e.g. 2022.3): there is no runtime binary TextAsset constructor, so
            // hand the raw blobs to the asset directly (it copies them into persistent storage).
            asset.SetRawAssetData(posBytes, otherBytes, colorBytes, shBytes);
#endif
            asset.SetDataHash(new Hash128((uint)n, (uint)d.numBones, 0, 0));

            posBytes.Dispose();
            otherBytes.Dispose();
            colorBytes.Dispose();
            shBytes.Dispose();
            return asset;
        }

#if UNITY_6000_0_OR_NEWER
        static TextAsset MakeTextAsset(NativeArray<byte> bytes, string name)
        {
            // The constructor copies the blob onto the native object, so GetData and dataSize work
            // at runtime and disposing the source afterwards is safe. For HideAndDontSave, see the
            // note in Build.
            var ta = new TextAsset(bytes.AsReadOnlySpan()) { name = name, hideFlags = HideFlags.HideAndDontSave };
            return ta;
        }

        static TextAsset MakeTextAsset<T>(NativeArray<T> data, string name) where T : unmanaged =>
            MakeTextAsset(data.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), name);
#endif

        /// <summary>
        /// Builds a runtime SplatAsset from the packed sections of an .spz capture.
        /// </summary>
        /// <remarks>
        /// THE FORMATS, AND WHAT THEY COST
        ///
        /// pos Norm11 (4B) + rot 10.10.10.2 and scale Norm11 (8B together) + colour Norm8x4 (4B)
        /// + chunk records (64B per 256 splats, so 0.25B) = <b>16.25 bytes a splat</b>. On the
        /// 1.92M-splat capture this was written against that is 31MB, against 27MB for the
        /// compressed source and 432MB for what the old import-time packer produced.
        ///
        /// Norm11 is not a compromise here. Positions are stored relative to a 256-splat chunk's
        /// own bounds, and the Morton reorder in the packing jobs makes those bounds a few
        /// centimetres across, so eleven bits buys tens of microns - far finer than any splat is
        /// wide. The same argument covers scale, which is additionally stored through a
        /// perceptual curve. Colour at 8 bits a channel is what the source file stores.
        ///
        /// SPHERICAL HARMONICS FOLLOW THE FILE
        ///
        /// A degree-0 capture stores <b>no SH at all</b> - a 32-byte stub so the GPU buffer is not
        /// empty, and the asset records a degree of 0 so the renderer never reads it. That is the
        /// whole point of the exercise: the old importer wrote 192 bytes a splat of zeroes for a
        /// file whose SH section is zero bytes long, which was 370MB of the 432MB.
        ///
        /// Degree 1, 2 and 3 all store the full Norm6 table, 32 bytes a splat, because the shader's
        /// SH stride is fixed per format - there is no layout for "three coefficients". The slots
        /// past the file's degree are zero, and the recorded degree stops the shader evaluating
        /// them.
        /// </remarks>
        /// <param name="d">Consumed: this disposes it as soon as the packing jobs are done, which
        /// is before the blobs are copied into TextAssets. Freeing the 36MB of decompressed source
        /// a moment early rather than in the caller's finally takes about a quarter off the peak
        /// of a large load, and on a phone that quarter is the whole argument.</param>
        internal static SplatAsset Build(ref SpzPackedData d, int shDegree)
        {
#if !UNITY_6000_0_OR_NEWER
            // Unlike a character, a capture is chunk-quantized, and the renderer only reads chunk
            // data out of a TextAsset - there is no raw-blob fallback for it. Building one before
            // the binary TextAsset constructor existed would mean writing files from a load.
            Debug.LogError("SplatRenderAssetBuilder: .spz captures need Unity 6 or newer.");
            return null;
#else
            int n = d.splatCount;
            var asset = ScriptableObject.CreateInstance<SplatAsset>();
            asset.name = "SplatCaptureSplatAsset";
            // Runtime-only asset, rebuilt on every load; see the note in the character Build.
            asset.hideFlags = HideFlags.HideAndDontSave;

            // Four uints of slack past the last record. LoadAndDecodeVector reads a second uint at
            // addr + 4 for VECTOR_FMT_11 whether or not the address was misaligned, so the final
            // splat's read runs four bytes past a buffer sized to exactly its last record. The
            // load is side-effect free, so a shader compiler is free to hoist it, and Metal does -
            // it faulted a device this way once already. Padding here keeps the guard out of the
            // per-splat hot path.
            const int kSlackUints = 4;

            var (texWidth, texHeight) = CalcTextureSize(n);
            int chunkCount = (n + kChunkSize - 1) / kChunkSize;
            bool hasSH = shDegree > 0;

            var output = new SpzPackedOutput
            {
                // Rounded up to an even count so the byte length stays a multiple of 8: the blob
                // is serialized as ulongs.
                pos = Alloc(((n + 1) & ~1) + kSlackUints),
                other = Alloc(n * 2 + kSlackUints),
                color = Alloc(texWidth * texHeight),
                // 8 uints is one Norm6 SH item. At degree 0 that single item is the entire blob:
                // it exists only because a zero-length GraphicsBuffer is illegal, and nothing ever
                // reads it.
                sh = Alloc(hasSH ? n * 8 : 8),
                chunks = new NativeArray<SplatChunkInfo>(chunkCount, Allocator.TempJob,
                                                               NativeArrayOptions.ClearMemory),
            };

            try
            {
                SplatCaptureJobs.Build(d, output, out float3 boundsMin, out float3 boundsMax);
                SplatSpzFile.Dispose(ref d);   // see the parameter note

                asset.Initialize(
                    n,
                    SplatAsset.VectorFormat.Norm11,   // pos
                    SplatAsset.VectorFormat.Norm11,   // scale
                    SplatAsset.ColorFormat.Norm8x4,   // color
                    SplatAsset.SHFormat.Norm6,        // sh
                    boundsMin, boundsMax,
                    Array.Empty<SplatAsset.CameraInfo>());
                asset.SetSHDegree(shDegree);

                asset.SetAssetFiles(
                    MakeTextAsset(output.chunks, "SplatCaptureChunks"),
                    MakeTextAsset(output.pos, "SplatCapturePos"),
                    MakeTextAsset(output.other, "SplatCaptureOther"),
                    MakeTextAsset(output.color, "SplatCaptureColor"),
                    MakeTextAsset(output.sh, "SplatCaptureSH"));
                asset.SetDataHash(new Hash128((uint)n, (uint)shDegree, (uint)chunkCount, 0));
                var built = asset;
                asset = null;   // handed over; the cleanup below no longer owns it
                return built;
            }
            finally
            {
                // A HideAndDontSave ScriptableObject that nobody returned is a leak that outlives
                // the scene, so anything thrown on the way out takes the half-built asset with it.
                if (asset != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(asset);
                    else UnityEngine.Object.DestroyImmediate(asset);
                }
                if (output.pos.IsCreated) output.pos.Dispose();
                if (output.other.IsCreated) output.other.Dispose();
                if (output.color.IsCreated) output.color.Dispose();
                if (output.sh.IsCreated) output.sh.Dispose();
                if (output.chunks.IsCreated) output.chunks.Dispose();
            }
#endif
        }

        static NativeArray<uint> Alloc(int uints) =>
            new NativeArray<uint>(uints, Allocator.TempJob, NativeArrayOptions.ClearMemory);

        // pos: Float32 float3 per splat, NextMultipleOf 8.
        static NativeArray<byte> BuildPosData(SplatCharacterBinData d)
        {
            int n = d.numSplats;
            int len = NextMultipleOf(n * 12, 8) + kTailSlack;
            var outBytes = new NativeArray<byte>(len, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            unsafe
            {
                byte* dst = (byte*)outBytes.GetUnsafePtr();
                for (int i = 0; i < n; i++)
                {
                    float3 p = d.positions[i];
                    float* fp = (float*)(dst + i * 12);
                    fp[0] = p.x; fp[1] = p.y; fp[2] = p.z;
                }
            }
            return outBytes;
        }

        // other: rot(10_10_10_2, 4B) + scale(float3, 12B) = 16B per splat, NextMultipleOf 8.
        static NativeArray<byte> BuildOtherData(SplatCharacterBinData d)
        {
            int n = d.numSplats;
            const int stride = 16;
            int len = NextMultipleOf(n * stride, 8) + kTailSlack;
            var outBytes = new NativeArray<byte>(len, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            unsafe
            {
                byte* dst = (byte*)outBytes.GetUnsafePtr();
                for (int i = 0; i < n; i++)
                {
                    byte* rec = dst + i * stride;
                    quaternion q = d.rotations[i];
                    float4 packed = PackSmallest3Rotation(new float4(q.value.x, q.value.y, q.value.z, q.value.w));
                    *(uint*)rec = EncodeQuatToNorm10(packed);
                    float3 s = d.scales[i];
                    float* fs = (float*)(rec + 4);
                    fs[0] = s.x; fs[1] = s.y; fs[2] = s.z;
                }
            }
            return outBytes;
        }

        // color: RGBA8 in a Morton-16x16 swizzled texture (matches SplatIndexToPixelIndex).
        static NativeArray<byte> BuildColorData(SplatCharacterBinData d)
        {
            int n = d.numSplats;
            var (w, hgt) = CalcTextureSize(n);
            int len = w * hgt * 4;
            var outBytes = new NativeArray<byte>(len, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            unsafe
            {
                byte* dst = (byte*)outBytes.GetUnsafePtr();
                int tilesPerRow = w / 16;
                for (int i = 0; i < n; i++)
                {
                    uint2 xy = DecodeMorton2D_16x16((uint)i);
                    uint blk = (uint)i >> 8;
                    int px = (int)((blk % (uint)tilesPerRow) * 16 + xy.x);
                    int py = (int)((blk / (uint)tilesPerRow) * 16 + xy.y);
                    int o = (py * w + px) * 4;
                    Color32 c = d.colors[i];
                    dst[o + 0] = c.r;
                    dst[o + 1] = c.g;
                    dst[o + 2] = c.b;
                    dst[o + 3] = c.a;
                }
            }
            return outBytes;
        }

        // sh: Norm6 -> 16x ushort = 32 bytes per splat, all zeros (SHOrder forced to 0).
        static NativeArray<byte> BuildShData(int n)
        {
            int len = n * 32;
            return new NativeArray<byte>(len, Allocator.TempJob, NativeArrayOptions.ClearMemory);
        }

        // ---- helpers mirroring the package encoders ----

        static int NextMultipleOf(int size, int multipleOf) => (size + multipleOf - 1) / multipleOf * multipleOf;

        // Slack past the last splat's record, so a speculative over-read of the final element stays
        // inside the allocation. LoadAndDecodeVector reads a float3 as three uints plus a fourth
        // load at addr + 12 that is guarded by an alignment test; the load is side-effect free, so a
        // shader compiler may hoist it out of the branch, and Metal does - faulting four bytes past
        // the end of a buffer whose length rounded to exactly its last record. 16 bytes covers the
        // widest over-read of this shape. Padding here rather than guarding in the shader keeps it
        // out of the hot path and covers every such load.
        const int kTailSlack = 16;

        static (int, int) CalcTextureSize(int splatCount)
        {
            int width = kTexWidth;
            int height = math.max(1, (splatCount + width - 1) / width);
            int blockHeight = 16;
            height = (height + blockHeight - 1) / blockHeight * blockHeight;
            return (width, height);
        }

        // Returns three smallest quaternion components in xyz (0..1) and index/3 of largest in w.
        static float4 PackSmallest3Rotation(float4 q)
        {
            float4 absQ = math.abs(q);
            int index = 0;
            float maxV = absQ.x;
            if (absQ.y > maxV) { index = 1; maxV = absQ.y; }
            if (absQ.z > maxV) { index = 2; maxV = absQ.z; }
            if (absQ.w > maxV) { index = 3; maxV = absQ.w; }

            if (index == 0) q = q.yzwx;
            else if (index == 1) q = q.xzwy;
            else if (index == 2) q = q.xywz;

            float3 three = q.xyz * (q.w >= 0 ? 1f : -1f);
            three = (three * math.SQRT2) * 0.5f + 0.5f;
            return new float4(three, index / 3.0f);
        }

        static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
        {
            return (uint)(v.x * 1023.5f) | ((uint)(v.y * 1023.5f) << 10) | ((uint)(v.z * 1023.5f) << 20) | ((uint)(v.w * 3.5f) << 30);
        }

        static uint2 DecodeMorton2D_16x16(uint t)
        {
            t = (t & 0xFF) | ((t & 0xFE) << 7);
            t &= 0x5555;
            t = (t ^ (t >> 1)) & 0x3333;
            t = (t ^ (t >> 2)) & 0x0f0f;
            return new uint2(t & 0xF, t >> 8);
        }
    }
}
