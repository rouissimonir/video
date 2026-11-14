// SPDX-License-Identifier: MIT
// Static per-splat source textures for the no-compute render path (WebGL2 / GLES 3.0).
//
// GLES 3.0 has no compute shaders and no vertex-stage SSBOs, so the splat data the render
// vertex shader needs is baked once into point-sampled textures it can texelFetch
// (RenderGaussianSplatsNoCompute.shader). Data arrives as plain decoded arrays - whatever
// produced them (a character .bin, an .spz capture) has already unpacked the on-disk format,
// so nothing here knows about SplatAsset's chunked/packed layout. CPU-side copies of the
// positions and each splat's dominant bone are kept for the depth sorter (CpuSplatSorter),
// with a lifetime independent of the source data.
//
// Limitation carried over from the path this was ported from: base colour only, no spherical
// harmonics. View-dependent colour needs either a per-frame CPU evaluation or a much larger
// texture budget, and the compute path remains the one that renders SH.

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class SplatNoComputeData : IDisposable
    {
        // Same row width as the compute path's packed view texture (SplatRenderer.kViewTexWidth);
        // power-of-two, and far below the GLES 3.0 minimum texture size guarantee (2048).
        public const int kTexWidth = 2048;

        public int splatCount;
        public int texWidth;
        public int texHeight;
        public bool hasBones;

        public Texture2D posOpacityTex; // RGBA32F: xyz bind center, w opacity
        public Texture2D rotTex;        // RGBA32F: bind rotation quat xyzw
        public Texture2D scaleTex;      // RGBA32F: xyz linear scale
        public Texture2D colorTex;      // RGBA8 (linear): rgb base color
        public Texture2D boneIdxTex;    // RGBA32F: top-4 bone indices as floats
        public Texture2D boneWtTex;     // RGBA32F: top-4 normalized weights

        // For the CPU depth sorter.
        public NativeArray<float3> positions;
        public NativeArray<ushort> dominantBone;

        public static SplatNoComputeData Build(
            NativeArray<float3> srcPositions,
            NativeArray<quaternion> srcRotations,
            NativeArray<float3> srcScales,
            NativeArray<Color32> srcColors,
            NativeArray<float> srcOpacities,
            NativeArray<uint4> srcBoneIndices,
            NativeArray<float4> srcBoneWeights)
        {
            int n = srcPositions.Length;
            var data = new SplatNoComputeData
            {
                splatCount = n,
                texWidth = kTexWidth,
                texHeight = Mathf.Max(1, (n + kTexWidth - 1) / kTexWidth),
                hasBones = srcBoneIndices.IsCreated && srcBoneIndices.Length == n &&
                           srcBoneWeights.IsCreated && srcBoneWeights.Length == n,
            };
            int texels = data.texWidth * data.texHeight;

            data.positions = new NativeArray<float3>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.positions.CopyFrom(srcPositions);
            data.dominantBone = new NativeArray<ushort>(n, Allocator.Persistent); // zero-filled: bone 0 when unskinned

            var f4 = new NativeArray<float4>(texels, Allocator.Temp, NativeArrayOptions.ClearMemory);

            for (int i = 0; i < n; ++i)
            {
                float3 p = srcPositions[i];
                f4[i] = new float4(p.x, p.y, p.z, i < srcOpacities.Length ? srcOpacities[i] : 1f);
            }
            data.posOpacityTex = MakeFloatTex("SplatNCPosOpacity", data.texWidth, data.texHeight, f4);

            for (int i = 0; i < n; ++i)
                f4[i] = srcRotations[i].value;
            for (int i = n; i < texels; ++i)
                f4[i] = new float4(0, 0, 0, 1);
            data.rotTex = MakeFloatTex("SplatNCRot", data.texWidth, data.texHeight, f4);

            for (int i = 0; i < n; ++i)
            {
                float3 s = srcScales[i];
                f4[i] = new float4(s.x, s.y, s.z, 0);
            }
            for (int i = n; i < texels; ++i)
                f4[i] = float4.zero;
            data.scaleTex = MakeFloatTex("SplatNCScale", data.texWidth, data.texHeight, f4);

            if (data.hasBones)
            {
                for (int i = 0; i < n; ++i)
                {
                    uint4 bi = srcBoneIndices[i];
                    f4[i] = new float4(bi.x, bi.y, bi.z, bi.w);
                }
                for (int i = n; i < texels; ++i)
                    f4[i] = float4.zero;
                data.boneIdxTex = MakeFloatTex("SplatNCBoneIdx", data.texWidth, data.texHeight, f4);

                for (int i = 0; i < n; ++i)
                {
                    float4 w = srcBoneWeights[i];
                    f4[i] = w;
                    // Dominant bone for the approximate CPU depth sort.
                    int arg = 0;
                    float best = w.x;
                    if (w.y > best) { best = w.y; arg = 1; }
                    if (w.z > best) { best = w.z; arg = 2; }
                    if (w.w > best) { arg = 3; }
                    data.dominantBone[i] = (ushort)srcBoneIndices[i][arg];
                }
                for (int i = n; i < texels; ++i)
                    f4[i] = float4.zero;
                data.boneWtTex = MakeFloatTex("SplatNCBoneWt", data.texWidth, data.texHeight, f4);
            }
            f4.Dispose();

            var c32 = new NativeArray<Color32>(texels, Allocator.Temp, NativeArrayOptions.ClearMemory);
            NativeArray<Color32>.Copy(srcColors, c32, n);
            data.colorTex = new Texture2D(data.texWidth, data.texHeight, TextureFormat.RGBA32, false, true)
            {
                name = "SplatNCColor",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            data.colorTex.SetPixelData(c32, 0);
            data.colorTex.Apply(false, true);
            c32.Dispose();

            return data;
        }

        static Texture2D MakeFloatTex(string name, int w, int h, NativeArray<float4> texels)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixelData(texels, 0);
            tex.Apply(false, true);
            return tex;
        }

        public void Dispose()
        {
            DestroyTex(ref posOpacityTex);
            DestroyTex(ref rotTex);
            DestroyTex(ref scaleTex);
            DestroyTex(ref colorTex);
            DestroyTex(ref boneIdxTex);
            DestroyTex(ref boneWtTex);
            if (positions.IsCreated) positions.Dispose();
            if (dominantBone.IsCreated) dominantBone.Dispose();
            splatCount = 0;
        }

        static void DestroyTex(ref Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
            else UnityEngine.Object.DestroyImmediate(tex);
            tex = null;
        }
    }
}
