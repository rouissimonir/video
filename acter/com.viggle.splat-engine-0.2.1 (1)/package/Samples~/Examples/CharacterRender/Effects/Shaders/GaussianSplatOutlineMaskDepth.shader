// SPDX-License-Identifier: MIT
// SAMPLE (Splat Effects): depth-aware variant of GaussianSplatOutlineMask.shader. Used ONLY when
// GaussianSplatOutlineFeature has "Occlude By Scene" turned on; with it off the feature keeps
// using the plain mask shader and nothing here runs.
//
// The vertex stage is the same mirror of the core RenderGaussianSplats.shader vert. What differs
// is the pass state, which is driven from material properties so a single shader covers both roles
// the occluded path needs:
//
//   occluder draw   _SplatMaskDepthOnly = 1, _SplatMaskColorMask = 0
//                   writes depth only. Every splat object in view gets one of these, outlined or
//                   not, so whatever stands in front can hide the outline of what is behind it.
//   silhouette draw _SplatMaskDepthOnly = 0, _SplatMaskColorMask = 15
//                   the same premultiplied outline color the non-occluded mask pass writes, but
//                   ZTested against the depth already laid down by the objects in front of it,
//                   so the mask is clipped where this character is hidden.
//
// That covers splat against splat. Ordinary opaque geometry is handled by the scene-depth test in
// frag below, which BOTH roles run: a mesh in front stops the silhouette drawing, and it stops the
// occluder writing depth as well - otherwise a character standing behind a wall would still be
// laying down an occluder that clips the outlines of characters behind it.
//
// Depth comes from SV_POSITION, not SV_Depth: the vert offsets only .xy of the splat's clip-space
// position to build the quad, so the interpolated z/w over the quad is exactly the splat center's
// clip depth. Writing depth explicitly would cost every platform its early-Z for no extra accuracy.
//
// SplatCustomPass.Draw() binds the view data and issues the draw; this shader only needs the core
// HLSL for the SplatViewData layout + helpers. If the core vert changes, mirror it here (and in
// GaussianSplatOutlineMask.shader).
Shader "Gaussian Splatting/Samples/Outline Mask Depth"
{
    Properties
    {
        // Declared so the pass state below can reference them. Set per material by
        // GaussianSplatOutlineFeature, never by a MaterialPropertyBlock - ShaderLab render state
        // reads the material, and a property block cannot override it.
        [HideInInspector] _SplatMaskDepthOnly ("Depth only", Float) = 0
        [HideInInspector] _SplatMaskColorMask ("Color mask", Float) = 15
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            // Nearest wins: the occluder draws walk front to back, so an object further away must
            // not stomp the depth of one already in front of it.
            ZTest LEqual
            ZWrite [_SplatMaskDepthOnly]
            ColorMask [_SplatMaskColorMask]
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
// 1 for the depth-only occluder draw, 0 for the silhouette draw. Also drives ZWrite/ColorMask
// above; read here so the two roles can use different coverage cutoffs.
float _SplatMaskDepthOnly;

// Scene depth, for the opaque-geometry half of "Occlude By Scene". The mask is an off-screen
// target with its own depth buffer, so the camera depth ATTACHMENT cannot be bound to it and
// meshes can only occlude through the depth TEXTURE. This is the core splat pass' manual depth
// test, verbatim (RenderGaussianSplats.shader): _SplatDepthTexParams.xy = 1 / the size in pixels
// of the target being drawn into, which maps this fragment's SV_Position onto the full-resolution
// depth texture and so makes any resolution mismatch between the two irrelevant. Point sampling
// picks one full-res depth per fragment - at worst a 1-source-pixel silhouette misclassification,
// invisible on soft gaussians. GaussianSplatOutlineFeature sets the uniform before the mask draws
// and asks URP for the texture with ConfigureInput(Depth); it does not rely on SplatRendererFeature
// having done either.
float4 _SplatDepthTexParams;
Texture2D<float> _CameraDepthTexture;
SamplerState my_point_clamp_sampler;

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
    // Note this pushes the vertex past the far plane, so it is clipped before it can reach the
    // depth buffer - a culled splat can never write occluder depth.
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

// Coverage below which the occluder draw does not write depth.
//
// A splat is a soft alpha ellipse: there is no one depth for a pixel it touches, so something has
// to decide where its occluding surface is, and the only knob available is coverage. Both ends of
// that choice are bad in their own way. Cut too low and the faint outer skirt of every ellipse
// writes depth, so each object's occluder grows a halo well outside the shape you can actually
// see and starts clipping outlines it has no business touching. Cut too high and only the dense
// cores write, leaving the depth surface full of pinholes for the outline behind it to speckle
// through - and a speckled outline is far more noticeable than one that is a pixel too tight.
//
// 0.25 leans to the safe end of that. For a fully opaque splat it is the ~1.2 sigma contour, a disc
// that already reads as solid, and it drops entirely any splat whose own opacity is under 25%,
// which is too faint to be hiding anything in the first place.
static const half k_DepthAlphaCutoff = 0.25h;

half4 frag (v2f i) : SV_Target
{
    // Scene depth test, first and for both roles. SV_Position.z is this fragment's device depth,
    // directly comparable to the depth-texture value; the normalized position maps this mask pixel
    // onto the full-res depth texture. Verbatim from the core splat pass, reversed-Z branch and
    // all - a depth comparison is not something to reinvent per shader.
    float sceneZ = _CameraDepthTexture.SampleLevel(my_point_clamp_sampler, i.vertex.xy * _SplatDepthTexParams.xy, 0);
    #if UNITY_REVERSED_Z
    if (i.vertex.z < sceneZ)
        discard;
    #else
    if (i.vertex.z > sceneZ)
        discard;
    #endif

    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);

    // Same bad-data guard as the core pass: drop non-finite/huge values before the blend.
    const half kMaxCol = 1e4;
    if (!(abs(i.col.a) < kMaxCol))
        discard;

    half opacity = max(i.col.a, 0.0);
    alpha = saturate(alpha * opacity);

    // Occluder draw: gate on the cutoff, so only the solid core of each splat lays down depth.
    // Silhouette draw: the same 1/255 floor the non-occluded mask pass uses, so a character with
    // nothing in front of it produces exactly the mask it produces with the option off.
    half cutoff = _SplatMaskDepthOnly > 0.5 ? k_DepthAlphaCutoff : (1.0h / 255.0h);
    if (alpha < cutoff)
        discard;

    // Premultiplied outline color so the composite pass can recover it as rgb/a. Discarded by
    // ColorMask 0 on the occluder draw.
    return half4(_OutlineColor.rgb * alpha, alpha);
}
ENDCG
        }
    }
}
