// SPDX-License-Identifier: MIT
// SAMPLE (Splat Effects): fullscreen pass that turns the off-screen silhouette mask
// (GaussianSplatOutlineMask.shader) into an outline and composites it over the camera color.
// For each pixel OUTSIDE a silhouette it samples an 8-tap ring at the outline thickness; if any
// tap is inside a silhouette, the pixel is on the outline band and is shaded with that
// silhouette's (per-character) color. Same fullscreen-triangle + global-texture pattern as the
// core GaussianComposite.shader, so it works both via Blitter (URP RenderGraph) and a manual
// 3-vertex DrawProcedural (URP compatibility mode).
Shader "Hidden/Gaussian Splatting/Samples/Outline"
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

Texture2D _GaussianOutlineMaskRT;
float _OutlineThickness;   // band width, in pixels
float _OutlineIntensity;   // 0..1 opacity of the outline over the scene

half4 frag (v2f i) : SV_Target
{
    int2 c = int2(i.vertex.xy);
    half4 center = _GaussianOutlineMaskRT.Load(int3(c, 0));

    // Inside the silhouette: let the character itself show through (outline sits outside).
    if (center.a >= 0.5)
        discard;

    int off = max(1, (int)(_OutlineThickness + 0.5));

    // 8-tap ring at the thickness radius. A pixel within `off` of a silhouette edge has at
    // least one tap landing inside, so the detected band is ~`off` pixels wide. Pick the tap
    // with the most coverage as the (per-character) outline color source.
    half4 best = half4(0, 0, 0, 0);
    int2 dirs[8] = {
        int2( off,  0), int2(-off,  0), int2( 0,  off), int2( 0, -off),
        int2( off, off), int2(-off, off), int2( off, -off), int2(-off, -off)
    };
    [unroll] for (int t = 0; t < 8; t++)
    {
        half4 s = _GaussianOutlineMaskRT.Load(int3(c + dirs[t], 0));
        if (s.a >= 0.5 && s.a > best.a)
            best = s;
    }

    if (best.a < 0.5)
        discard; // no silhouette within range -> not an outline pixel

    // Recover the un-premultiplied per-character outline color.
    half3 outlineColor = best.rgb / max(best.a, 1e-4);
    return half4(outlineColor, saturate(_OutlineIntensity));
}
ENDCG
        }
    }
}
