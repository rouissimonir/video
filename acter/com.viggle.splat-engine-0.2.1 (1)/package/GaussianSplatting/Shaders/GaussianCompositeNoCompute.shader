// SPDX-License-Identifier: MIT
// Composite pass for the no-compute render path (WebGL2 / GLES 3.0).
//
// Byte-for-byte the same program as GaussianComposite.shader; the ONLY differences are the
// pragmas. That shader declares "#pragma require compute" and "#pragma use_dxc", which mark it
// unsupported on a platform without compute shaders, so it never loads on GLES 3.0 even though
// nothing in its body needs compute - Texture2D.Load only wants target 3.5.
//
// KEEP IN SYNC with GaussianComposite.shader. The two bodies are duplicated deliberately:
// factoring them into a shared .cginc would mean editing the shader that every shipping
// platform already depends on, and this fallback is not worth that risk. If you change the
// compositing maths, the debug modes or the colorspace handling there, change them here too.
Shader "Hidden/Gaussian Splatting/Composite (No Compute)"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 3.5
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;
// Reduced-resolution splat RT (SplatRenderer.SplatRenderScale): xy = 1 / composite target
// size in pixels, z = the render scale. See GaussianComposite.shader for the full note.
float4 _GaussianSplatRTParams;
SamplerState my_linear_clamp_sampler;

// Debug visualization of the offscreen splat RT. See GaussianComposite.shader.
int _GaussianCompositeDebug;

// Layer count that maps to full red in the overdraw view.
float _GaussianOverdrawScale;

// 1 = show the layer count as a heatmap. Independent of _GaussianCompositeDebug on purpose.
int _GaussianOverdrawView;

half4 frag (v2f i) : SV_Target
{
    half4 col;
    float scale = _GaussianSplatRTParams.z;
    if (scale > 0.0 && scale < 0.999)
        col = _GaussianSplatRT.SampleLevel(my_linear_clamp_sampler, i.vertex.xy * _GaussianSplatRTParams.xy, 0);
    else
        col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));

    if (_GaussianCompositeDebug == 1)
        return float4(col.a, col.a, col.a, 1);
    if (_GaussianCompositeDebug == 2)
        return float4(col.rgb, 1);
    if (_GaussianOverdrawView != 0)
    {
        float layers = col.r;
        float t = saturate(layers / max(_GaussianOverdrawScale, 1.0));
        float3 heat = t < 0.5
            ? lerp(float3(0,0,0.6), float3(0,1,0), t * 2.0)
            : lerp(float3(0,1,0), float3(1,0,0), (t - 0.5) * 2.0);
        if (layers < 0.5) heat = 0;   // untouched pixels stay black
        return float4(heat, 1);
    }
    if (_GaussianCompositeDebug == 3)
        return float4(col.a > 0 ? col.rgb / col.a : 0, 1);

    // Guard the un-premultiply divide: empty pixels have a==0, and 0/0 = NaN.
    col.rgb = col.a > 0 ? col.rgb / col.a : 0;

    // Linear projects decode sRGB -> linear here and the pipeline's display encode undoes it;
    // Gamma projects have no display encode, so the conversion must not happen at all.
#ifndef UNITY_COLORSPACE_GAMMA
    col.rgb = GammaToLinearSpace(col.rgb);
#endif
    return float4(col.rgb, col.a);
}
ENDCG
        }
    }
}
