// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Debug/Render Boxes"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            // Premultiplied back-to-front "over", matching RenderGaussianSplats. This shader kept
            // upstream's dst-alpha "under" blend (OneMinusDstAlpha One) after the splat pass moved
            // off it, and "under" composites FRONT-to-back. The sort has been far->near ever since
            // useBackToFrontBlend went true, so every box was being blended in exactly the reverse
            // of the order its blend equation assumes - near boxes buried under far ones.
            Blend One OneMinusSrcAlpha
            // Cull Back, not Front: with one face drawn per box, that face should be the one facing
            // the camera. Drawing the far face (Cull Front) puts each box's contribution at the
            // wrong depth relative to its neighbours, which compounds the ordering error above.
            Cull Back

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

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

bool _DisplayChunks;

struct v2f
{
    half4 col : COLOR0;
    float4 vertex : SV_POSITION;
};

float _SplatScale;
float _SplatOpacityScale;

// based on https://iquilezles.org/articles/palettes/
// cosine based palette, 4 vec3 params
half3 palette(float t, half3 a, half3 b, half3 c, half3 d)
{
    return a + b*cos(6.28318*(c*t+d));
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    bool chunks = _DisplayChunks;
	uint idx = vtxID;
	float3 localPos = float3(idx&1, (idx>>1)&1, (idx>>2)&1) * 2.0 - 1.0;

    float3 centerWorldPos = 0;

    if (!chunks)
    {
        // display splat boxes
        // Bound the order lookup (mirrors the core vert): instID is the draw's instance count,
        // and LoadSplatData below indexes the per-splat buffers with the result. Debug-only path,
        // but an unbounded index here is still a Metal page fault.
        instID = _OrderBuffer[min(instID, max(_OrderBufferCount, 1u) - 1u)];
        SplatData splat = LoadSplatData(instID, 0); // debug display: base color only, no SH

        float4 boxRot = splat.rot;
        float3 boxSize = splat.scale;
        boxSize *= _SplatScale;

        float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);
        splatRotScaleMat = mul((float3x3)unity_ObjectToWorld, splatRotScaleMat);

        centerWorldPos = splat.pos;
        centerWorldPos = mul(unity_ObjectToWorld, float4(centerWorldPos,1)).xyz;

        o.col.rgb = saturate(splat.sh.col);
        o.col.a = saturate(splat.opacity * _SplatOpacityScale);

        localPos = mul(splatRotScaleMat, localPos) * 2;
    }
    else
    {
        // display chunk boxes
        localPos = localPos * 0.5 + 0.5;
        SplatChunkInfo chunk = _SplatChunks[instID];
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);

        localPos = lerp(posMin, posMax, localPos);
        localPos = mul(unity_ObjectToWorld, float4(localPos,1)).xyz;

        o.col.rgb = palette((float)instID / (float)_SplatChunkCount, half3(0.5,0.5,0.5), half3(0.5,0.5,0.5), half3(1,1,1), half3(0.0, 0.33, 0.67));
        o.col.a = 0.1;
    }

    float3 worldPos = centerWorldPos + localPos;
    o.vertex = UnityWorldToClipPos(worldPos);
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    half4 res = half4(i.col.rgb * i.col.a, i.col.a);
    return res;
}
ENDCG
        }
    }
}
