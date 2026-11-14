// SPDX-License-Identifier: MIT
// SAMPLE (Splat Effects): renders one splat character's silhouette into an off-screen mask so
// GaussianSplatOutline.shader can ring-expand it into an outline. The vertex stage mirrors the
// core RenderGaussianSplats.shader vert (it reads the same per-splat view data the renderer
// computed this frame), but the fragment outputs a flat, per-character outline color
// premultiplied by coverage instead of the lit splat color.
//
// GaussianSplatRenderer.RenderSilhouette() binds the view data and issues the draw; this shader
// only needs the core HLSL for the SplatViewData layout + helpers. If the core vert changes,
// mirror it here.
Shader "Gaussian Splatting/Samples/Outline Mask"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            // Same premultiplied "over" blend as the core splat pass: coverage accumulates in
            // alpha (→1 inside the silhouette) and the outline color builds up premultiplied, so
            // the composite pass recovers it as rgb/a at the edge.
            Blend One OneMinusSrcAlpha
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#pragma multi_compile _ SPLAT_VERTEX_TEXTURE_FETCH

// Core Gaussian-splat HLSL (SplatViewData layout, FlipProjectionIfBackbuffer, etc.).
#include "Packages/com.viggle.splat-engine/GaussianSplatting/Shaders/GaussianSplatting.hlsl"

struct v2f
{
    half4 col : COLOR0;      // .a carries the splat opacity (coverage weight)
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

// Per-character outline color (set per draw via MaterialPropertyBlock by the outline feature).
half4 _OutlineColor;
// Must match what the renderer's CalcViewData used for the axes.
float4 _VecScreenParams;

#if defined(SPLAT_VERTEX_TEXTURE_FETCH)
// uint4, and 2 texels per splat - must match CSBuildViewTex and RenderGaussianSplats.shader.
// This shader mirrors the renderer's vertex-stage data path, so a layout change there is a
// layout change here; reading the old 3-float-texel layout out of the new texture produced
// garbage geometry streaked across the whole screen.
Texture2D<uint4> _SplatViewDataTex;
uint _SplatViewTexWidth;
int3 ViewTexCoord(uint linearTexel)
{
    return int3((int)(linearTexel % _SplatViewTexWidth), (int)(linearTexel / _SplatViewTexWidth), 0);
}
#else
// Buffer lengths, passed in from C#. NOT queried with GetDimensions: on Metal, DXC+SPIRV-Cross
// lowers a buffer-size query into a lookup in an auxiliary "spvBufferSizeConstants" buffer that
// Unity never binds, so the query yields garbage and the clamp below silently does nothing. That
// is not theory - it is this exact assert on device:
//     Vertex Function(xlatMtlMain): missing buffer binding at index 25 for spvBufferSizeConstants[0]
// followed by a GPU address fault in the splat draw. The compute shaders already learned this and
// pass _SplatCountFull / _BoneCount as uniforms for the same reason.
// max(x,1)-1 rather than x-1: if these ever arrive unset (0), an underflow would produce
// 0xFFFFFFFF and disable the clamp exactly like the broken query did. Fail to splat 0 instead.
uint _OrderBufferCount;
uint _SplatViewDataCount;
StructuredBuffer<uint> _OrderBuffer;
StructuredBuffer<SplatViewData> _SplatViewData;
#endif

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;

    float4 centerClipPos;
    float4 axis;
    half4 splatCol;

#if defined(SPLAT_VERTEX_TEXTURE_FETCH)
    uint baseTexel = instID * 2u;
    uint4 t0 = _SplatViewDataTex.Load(ViewTexCoord(baseTexel + 0u));
    uint4 t1 = _SplatViewDataTex.Load(ViewTexCoord(baseTexel + 1u));

    centerClipPos = asfloat(t0);
    // f16tof32 reads the low half, so the second of each pair is shifted down first.
    axis = float4(f16tof32(t1.x), f16tof32(t1.x >> 16),
                  f16tof32(t1.y), f16tof32(t1.y >> 16));
    splatCol.r = f16tof32(t1.z >> 16);
    splatCol.g = f16tof32(t1.z);
    splatCol.b = f16tof32(t1.w >> 16);
    splatCol.a = f16tof32(t1.w);
#else
    // Clamp the order lookup itself as well as its result (mirrors the core vert).
    instID = _OrderBuffer[min(instID, max(_OrderBufferCount, 1u) - 1u)];
    // Defensive clamp: same as RenderGaussianSplats.shader - on the bitonic-sort fallback the order
    // buffer is padded past splatCount and a sentinel-tie can pull an out-of-range index here, which
    // faults on Metal (D3D11 returns 0). _SplatViewData is sized to splatCount; clamp to the last element.
    instID = min(instID, max(_SplatViewDataCount, 1u) - 1u);
    SplatViewData view = _SplatViewData[instID];
    centerClipPos = view.pos;
    axis = view.axis;
    splatCol.r = f16tof32(view.color.x >> 16);
    splatCol.g = f16tof32(view.color.x);
    splatCol.b = f16tof32(view.color.y >> 16);
    splatCol.a = f16tof32(view.color.y);
#endif

    // Cull behind-camera AND invalid (NaN/Inf) splats; same collapse-far trick as the core pass.
    bool cull = !(centerClipPos.w > 0);
    if (cull)
    {
        o.vertex = float4(0, 0, 1e9, 1);
    }
    else
    {
        o.col = splatCol;

        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        quadPos *= 2;
        o.pos = quadPos;

        float2 deltaScreenPos = (quadPos.x * axis.xy + quadPos.y * axis.zw) * 2 / _VecScreenParams.xy;
        o.vertex = centerClipPos;
        o.vertex.xy += deltaScreenPos * centerClipPos.w;
    }
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);

    // Same bad-data guard as the core pass: drop non-finite/huge values before the blend.
    const half kMaxCol = 1e4;
    if (!(abs(i.col.a) < kMaxCol))
        discard;

    half opacity = max(i.col.a, 0.0);
    alpha = saturate(alpha * opacity);
    if (alpha < 1.0/255.0)
        discard;

    // Premultiplied outline color so the composite pass can recover it as rgb/a.
    return half4(_OutlineColor.rgb * alpha, alpha);
}
ENDCG
        }
    }
}
