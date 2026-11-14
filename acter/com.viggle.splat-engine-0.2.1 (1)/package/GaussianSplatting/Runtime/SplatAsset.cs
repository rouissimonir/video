// SPDX-License-Identifier: MIT
//
// The static splat asset: splat data packed into the GPU layout the renderer reads. The splat
// analogue of a Mesh - the data, not the thing that draws it.
//
// BUILT AT LOAD TIME, ALWAYS
//
// Nothing imports one any more. Both content paths - a .vsplat character and an .spz capture -
// store their source file and build one of these when the component loads, so every instance is
// owned by the component that built it and dies with it. That is why this type is no longer
// reachable from anything public, and why shDegree below is deliberately not serialized.
//
// WHY IT LIVES IN THE VENDORED ASSEMBLY
//
// Every other public type in the package sits in Viggle.SplatEngine. This one is vendored
// alongside the format it describes, stays where upstream compiled it, and is made public in
// place, in the Viggle.SplatEngine NAMESPACE, so a host never has to type "GaussianSplatting" to
// name it. (Namespaces are free to span assemblies.)
//
// The chunk record it packs against does NOT live here any more. Viggle.SplatEngine.Jobs writes
// it, and Jobs is compiled into the player while everything here can be hot-swapped, so Jobs may
// not reference this assembly and the layout had to move down to the side that fills it. It is
// SplatChunkInfo now; see its header. This class names it from there, which is why
// GaussianSplatting.asmdef references Jobs and not the other way round.
//
// The consequence is that GaussianSplatting.asmdef must stay auto-referenced, or user code could
// not name the type at all. It is the only public type left in that assembly - the renderer, the
// URP feature and the HDRP pass all moved up into Viggle.SplatEngine - so the auto-reference
// exposes nothing else.
//
// PUBLIC SURFACE IS DELIBERATELY THREE MEMBERS
//
// splatCount, boundsMin, boundsMax: what a host can act on. Everything else here is the GPU packing
// layout (formats, chunk records, SH tables, the raw blob accessors) and stays internal - it is an
// implementation detail of the renderer and changing it must not be a breaking API change.

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Scripting.APIUpdating;
using Viggle.SplatEngine.Jobs;

namespace Viggle.SplatEngine
{
    // HybridCLR hot-reload stable ABI type: keep serialized fields and Serializable layouts stable.
    /// <summary>
    /// Gaussian splat data packed into the layout the renderer reads. The splat analogue of a
    /// <see cref="Mesh"/>.
    /// </summary>
    /// <remarks>
    /// Built when a splat component loads, from a <see cref="SplatCaptureAsset"/> or a
    /// <see cref="SplatCharacterAsset"/>, and owned by that component. Not something to create or
    /// assign by hand.
    /// </remarks>
    // Assets authored before the rename resolve their script by the .cs.meta GUID, which is
    // unchanged; this covers anything that resolves the type by name instead.
    [MovedFrom(true, "GaussianSplatting.Runtime", null, "GaussianSplatAsset")]
    public class SplatAsset : ScriptableObject
    {
        internal const int kCurrentVersion = 2023_10_20;
        internal const int kChunkSize = 256;
        internal const int kTextureWidth = 2048; // allows up to 32M splats on desktop GPU (2k width x 16k height)
        internal const int kMaxSplats = 8_600_000; // mostly due to 2GB GPU buffer size limit when exporting a splat (2GB / 248B is just over 8.6M)

        [SerializeField] int m_FormatVersion;
        [SerializeField] int m_SplatCount;
        [SerializeField] Vector3 m_BoundsMin;
        [SerializeField] Vector3 m_BoundsMax;
        [SerializeField] Hash128 m_DataHash;

        internal int formatVersion => m_FormatVersion;

        /// <summary>How many splats this capture contains.</summary>
        public int splatCount => m_SplatCount;

        /// <summary>Minimum corner of the capture's bounds, in the asset's own space.</summary>
        public Vector3 boundsMin => m_BoundsMin;

        /// <summary>Maximum corner of the capture's bounds, in the asset's own space.</summary>
        public Vector3 boundsMax => m_BoundsMax;

        internal Hash128 dataHash => m_DataHash;

        // Match VECTOR_FMT_* in HLSL
        internal enum VectorFormat
        {
            Float32, // 12 bytes: 32F.32F.32F
            Norm16, // 6 bytes: 16.16.16
            Norm11, // 4 bytes: 11.10.11
            Norm6   // 2 bytes: 6.5.5
        }

        internal static int GetVectorSize(VectorFormat fmt)
        {
            return fmt switch
            {
                VectorFormat.Float32 => 12,
                VectorFormat.Norm16 => 6,
                VectorFormat.Norm11 => 4,
                VectorFormat.Norm6 => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        internal enum ColorFormat
        {
            Float32x4,
            Float16x4,
            Norm8x4,
            // A BC7 variant used to live here. It went with the import-time packer: block
            // compression needs EditorUtility.CompressTexture, which does not exist at runtime,
            // and every asset is built at load time now.
        }
        internal static int GetColorSize(ColorFormat fmt)
        {
            return fmt switch
            {
                ColorFormat.Float32x4 => 16,
                ColorFormat.Float16x4 => 8,
                ColorFormat.Norm8x4 => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        internal enum SHFormat
        {
            Float32,
            Float16,
            Norm11,
            Norm6,
            // Five k-means-clustered variants used to follow. They went with the import-time
            // packer: clustering a large capture was minutes of work, which is fine to spend once
            // during an import and impossible to spend during a load. The shader still knows how
            // to read them (anything past VECTOR_FMT_6 carries a per-splat table index), so
            // reviving them would only need an encoder.
        }

        internal struct SHTableItemFloat32
        {
            public float3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
            public float3 shPadding; // pad to multiple of 16 bytes
        }
        internal struct SHTableItemFloat16
        {
            public half3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
            public half3 shPadding; // pad to multiple of 16 bytes
        }
        internal struct SHTableItemNorm11
        {
            public uint sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        }
        internal struct SHTableItemNorm6
        {
            public ushort sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
            public ushort shPadding; // pad to multiple of 4 bytes
        }

        internal void Initialize(int splats, VectorFormat formatPos, VectorFormat formatScale, ColorFormat formatColor, SHFormat formatSh, Vector3 bMin, Vector3 bMax, CameraInfo[] cameraInfos)
        {
            m_SplatCount = splats;
            m_FormatVersion = kCurrentVersion;
            m_PosFormat = formatPos;
            m_ScaleFormat = formatScale;
            m_ColorFormat = formatColor;
            m_SHFormat = formatSh;
            m_Cameras = cameraInfos;
            m_BoundsMin = bMin;
            m_BoundsMax = bMax;
        }

        internal void SetDataHash(Hash128 hash)
        {
            m_DataHash = hash;
        }

        // The spherical-harmonic degree this asset's SH blob actually contains, 0 to 3. NOT
        // serialized, and initialised to 3, deliberately: assets are built at load time now, and a
        // serialized field would deserialize an older asset to 0 and silently switch its SH off.
        //
        // It exists because the SH blob is no longer always full size. A degree-0 capture stores a
        // 32-byte stub instead of 32 bytes a splat, and the shader indexes SH by splat with a
        // fixed stride - so if the renderer asked for order 1 or higher on such an asset it would
        // read a megabyte past the end of a 32-byte buffer. On D3D and Vulkan that returns zeroes;
        // on Metal it faults the device. The renderer clamps its SH order against this.
        [NonSerialized] int m_SHDegree = 3;
        internal int shDegree => m_SHDegree;
        internal void SetSHDegree(int degree) => m_SHDegree = math.clamp(degree, 0, 3);

        internal void SetAssetFiles(TextAsset dataChunk, TextAsset dataPos, TextAsset dataOther, TextAsset dataColor, TextAsset dataSh)
        {
            m_ChunkData = dataChunk;
            m_PosData = dataPos;
            m_OtherData = dataOther;
            m_ColorData = dataColor;
            m_SHData = dataSh;
        }

        internal static int GetOtherSizeNoSHIndex(VectorFormat scaleFormat)
        {
            return 4 + GetVectorSize(scaleFormat);
        }

        internal static int GetSHCount(SHFormat fmt, int splatCount)
        {
            return fmt switch
            {
                SHFormat.Float32 => splatCount,
                SHFormat.Float16 => splatCount,
                SHFormat.Norm11 => splatCount,
                SHFormat.Norm6 => splatCount,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        internal static (int,int) CalcTextureSize(int splatCount)
        {
            int width = kTextureWidth;
            int height = math.max(1, (splatCount + width - 1) / width);
            // our swizzle tiles are 16x16, so make texture multiple of that height
            int blockHeight = 16;
            height = (height + blockHeight - 1) / blockHeight * blockHeight;
            return (width, height);
        }

        internal static GraphicsFormat ColorFormatToGraphics(ColorFormat format)
        {
            return format switch
            {
                ColorFormat.Float32x4 => GraphicsFormat.R32G32B32A32_SFloat,
                ColorFormat.Float16x4 => GraphicsFormat.R16G16B16A16_SFloat,
                ColorFormat.Norm8x4 => GraphicsFormat.R8G8B8A8_UNorm,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };
        }

        internal static long CalcPosDataSize(int splatCount, VectorFormat formatPos)
        {
            return splatCount * GetVectorSize(formatPos);
        }
        internal static long CalcOtherDataSize(int splatCount, VectorFormat formatScale)
        {
            return splatCount * GetOtherSizeNoSHIndex(formatScale);
        }
        internal static long CalcColorDataSize(int splatCount, ColorFormat formatColor)
        {
            var (width, height) = CalcTextureSize(splatCount);
            return width * height * GetColorSize(formatColor);
        }
        internal static long CalcSHDataSize(int splatCount, SHFormat formatSh)
        {
            int shCount = GetSHCount(formatSh, splatCount);
            return formatSh switch
            {
                SHFormat.Float32 => shCount * UnsafeUtility.SizeOf<SHTableItemFloat32>(),
                SHFormat.Float16 => shCount * UnsafeUtility.SizeOf<SHTableItemFloat16>(),
                SHFormat.Norm11 => shCount * UnsafeUtility.SizeOf<SHTableItemNorm11>(),
                SHFormat.Norm6 => shCount * UnsafeUtility.SizeOf<SHTableItemNorm6>(),
                _ => throw new ArgumentOutOfRangeException(nameof(formatSh), formatSh, null)
            };
        }
        internal static long CalcChunkDataSize(int splatCount)
        {
            int chunkCount = (splatCount + kChunkSize - 1) / kChunkSize;
            return chunkCount * UnsafeUtility.SizeOf<SplatChunkInfo>();
        }

        [SerializeField] VectorFormat m_PosFormat = VectorFormat.Norm11;
        [SerializeField] VectorFormat m_ScaleFormat = VectorFormat.Norm11;
        [SerializeField] SHFormat m_SHFormat = SHFormat.Norm11;
        [SerializeField] ColorFormat m_ColorFormat;

        [SerializeField] TextAsset m_PosData;
        [SerializeField] TextAsset m_ColorData;
        [SerializeField] TextAsset m_OtherData;
        [SerializeField] TextAsset m_SHData;
        // Chunk data is optional (if data formats are fully lossless then there's no chunking)
        [SerializeField] TextAsset m_ChunkData;

        [SerializeField] CameraInfo[] m_Cameras;

        internal VectorFormat posFormat => m_PosFormat;
        internal VectorFormat scaleFormat => m_ScaleFormat;
        internal SHFormat shFormat => m_SHFormat;
        internal ColorFormat colorFormat => m_ColorFormat;

        internal TextAsset posData => m_PosData;
        internal TextAsset colorData => m_ColorData;
        internal TextAsset otherData => m_OtherData;
        internal TextAsset shData => m_SHData;
        internal TextAsset chunkData => m_ChunkData;
        internal CameraInfo[] cameras => m_Cameras;

        // --- Runtime raw-data fallback ---------------------------------------------------
        // The binary TextAsset constructor (TextAsset(ReadOnlySpan<byte>)) only exists in
        // Unity 6+. On older editors (e.g. 2022.3 LTS) an asset assembled at runtime cannot
        // store its blobs in TextAssets, so it keeps them here instead. The accessors below
        // transparently read whichever source is populated, so the renderer is version-agnostic.
        [NonSerialized] NativeArray<byte> m_PosRaw, m_OtherRaw, m_ColorRaw, m_SHRaw;

        internal void SetRawAssetData(NativeArray<byte> pos, NativeArray<byte> other, NativeArray<byte> color, NativeArray<byte> sh)
        {
            DisposeRawData();
            // Copy into persistent storage owned by this asset; callers keep ownership of their inputs.
            m_PosRaw = new NativeArray<byte>(pos, Allocator.Persistent);
            m_OtherRaw = new NativeArray<byte>(other, Allocator.Persistent);
            m_ColorRaw = new NativeArray<byte>(color, Allocator.Persistent);
            m_SHRaw = new NativeArray<byte>(sh, Allocator.Persistent);
        }

        void DisposeRawData()
        {
            if (m_PosRaw.IsCreated) m_PosRaw.Dispose();
            if (m_OtherRaw.IsCreated) m_OtherRaw.Dispose();
            if (m_ColorRaw.IsCreated) m_ColorRaw.Dispose();
            if (m_SHRaw.IsCreated) m_SHRaw.Dispose();
        }

        void OnDisable() => DisposeRawData();

        // True when pos/other/sh/color data is available, from either a TextAsset or raw blob.
        internal bool HasData =>
            (m_PosData != null || m_PosRaw.IsCreated) &&
            (m_OtherData != null || m_OtherRaw.IsCreated) &&
            (m_SHData != null || m_SHRaw.IsCreated) &&
            (m_ColorData != null || m_ColorRaw.IsCreated);

        internal long PosDataSize => m_PosData != null ? m_PosData.dataSize : m_PosRaw.Length;
        internal long OtherDataSize => m_OtherData != null ? m_OtherData.dataSize : m_OtherRaw.Length;
        internal long SHDataSize => m_SHData != null ? m_SHData.dataSize : m_SHRaw.Length;
        internal long ColorDataSize => m_ColorData != null ? m_ColorData.dataSize : m_ColorRaw.Length;
        internal long ChunkDataSize => m_ChunkData != null ? m_ChunkData.dataSize : 0;

        internal NativeArray<T> GetPosData<T>() where T : struct => m_PosData != null ? m_PosData.GetData<T>() : m_PosRaw.Reinterpret<T>(1);
        internal NativeArray<T> GetOtherData<T>() where T : struct => m_OtherData != null ? m_OtherData.GetData<T>() : m_OtherRaw.Reinterpret<T>(1);
        internal NativeArray<T> GetSHData<T>() where T : struct => m_SHData != null ? m_SHData.GetData<T>() : m_SHRaw.Reinterpret<T>(1);
        internal NativeArray<T> GetColorData<T>() where T : struct => m_ColorData != null ? m_ColorData.GetData<T>() : m_ColorRaw.Reinterpret<T>(1);

        [Serializable]
        internal struct CameraInfo
        {
            public Vector3 pos;
            public Vector3 axisX, axisY, axisZ;
            public float fov;
        }
    }
}
