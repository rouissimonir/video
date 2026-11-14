// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
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
#pragma require compute
#pragma use_dxc
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
// Reduced-resolution splat RT (GaussianSplatRenderer.SplatRenderScale): xy = 1 / composite
// target size in pixels, z = the render scale. When z is 1 - or the global was never set
// (z == 0, e.g. the HDRP path, which doesn't support scaling) - the RT texel-matches the
// target and the exact Load below is used; when 0 < z < 1 the RT is smaller and we
// bilinearly upsample by normalized position instead. Premultiplied color interpolates
// correctly, and soft gaussians upscale gracefully.
float4 _GaussianSplatRTParams;
SamplerState my_linear_clamp_sampler;

// Debug visualization of the offscreen splat RT (step-3 output). Set globally via
// Shader.SetGlobalInteger("_GaussianCompositeDebug", ...). 0 = normal compositing.
//   1 = accumulated coverage (alpha) as grayscale
//   2 = raw premultiplied RGB straight from the RT (no divide, no gamma)
//   3 = un-premultiplied RGB, gamma conversion skipped (compare vs normal to isolate gamma)
// Debug modes output alpha = 1 so they fully replace the scene for inspection.
int _GaussianCompositeDebug;

// Layer count that maps to full red in debug mode 4.
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
    // NOTE: driven by its own global, NOT by _GaussianCompositeDebug. SkinnedSplatRenderer pushes
    // that one from its own inspector field every time it validates, which silently reset the
    // heatmap to 0 and made it look like the scale slider did nothing.
    // (was mode 4)  overdraw heatmap. The RT holds a layer count (see _SplatOverdrawCount); map it onto a
    // blue->green->yellow->red ramp against _GaussianOverdrawScale layers so the number is
    // readable rather than a saturated white mass.
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
    // With Blend SrcAlpha OneMinusSrcAlpha, 0*NaN = NaN clamps to black and wipes
    // the destination (skybox). Output a clean transparent pixel instead so the
    // background shows through.
    col.rgb = col.a > 0 ? col.rgb / col.a : 0;

    // Splat colors decode from the SH DC term as sRGB-encoded values, and the splat RT is
    // a plain R16G16B16A16_SFloat with no sRGB flag, so nothing has decoded them yet.
    //
    // In a Linear project this conversion is what makes the round trip come out right: we
    // decode sRGB -> linear here, and the pipeline's final linear -> sRGB display encode
    // undoes it. Net identity, which is why this has always looked correct.
    //
    // In a Gamma project there is no final display encode - the framebuffer keeps exactly
    // what we write - so the same call would be applied and never undone, raising every
    // channel to roughly the 2.2 power (0.5 renders as 0.21). That reads as splats being
    // dramatically darker than the lit geometry beside them, with no error anywhere.
    // Unity defines UNITY_COLORSPACE_GAMMA only in Gamma projects, so Linear output is
    // bit-identical to before.
#ifndef UNITY_COLORSPACE_GAMMA
    col.rgb = GammaToLinearSpace(col.rgb);
#endif
    return float4(col.rgb, col.a);
}
ENDCG
        }
    }
}
