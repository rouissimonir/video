// SPDX-License-Identifier: MIT
//
// The per-chunk quantization record: the min/max of everything GaussianSplatting.hlsl dequantizes
// against, 64 bytes for every 256 splats. Uploaded verbatim as the chunk StructuredBuffer, so the
// field order IS the shader's layout - see SplatChunkData in GaussianSplatting.hlsl. Vendored
// from org.nesnausk.gaussian-splatting, where it was nested inside the splat asset.
//
// WHY IT LIVES HERE AND NOT IN SplatAsset
//
// SplatCaptureJobs writes it, and this assembly may not reference any other assembly of ours (see
// AssemblyInfo.cs), so the declaration has to be on this side of that line. Everyone upstream -
// the asset that sizes its chunk blob by it, the builder that allocates the array, the renderer
// that hands it to the GPU - names it from here.
//
// Declaring it here rather than mirroring it also keeps NativeArray<SplatChunkInfo> and
// UnsafeUtility.SizeOf<SplatChunkInfo> instantiated over a type that is always compiled into the
// player, which is what a host that hot-swaps the assemblies above needs. It is the same
// arrangement as BonePose: a GPU layout contract declared in the assembly whose jobs fill it.

using Unity.Mathematics;

namespace Viggle.SplatEngine.Jobs
{
    /// <summary>
    /// One chunk's dequantization bounds. Halves are packed as two f16 in a uint, low half = min;
    /// position keeps full float precision because it is what the eye measures everything else
    /// against.
    /// </summary>
    internal struct SplatChunkInfo
    {
        public uint colR, colG, colB, colA;
        public float2 posX, posY, posZ;
        public uint sclX, sclY, sclZ;
        public uint shR, shG, shB;
    }
}
