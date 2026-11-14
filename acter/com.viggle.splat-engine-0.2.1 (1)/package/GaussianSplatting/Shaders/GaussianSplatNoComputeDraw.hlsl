// SPDX-License-Identifier: MIT
// Turning a projected splat into pixels, for the no-compute path (WebGL2 / GLES 3.0).
//
// Everything downstream of the projection lives here - quad expansion, the backbuffer y-flip,
// the gaussian falloff, the manual scene-depth test - because the two draws that need it get
// their projected splats from different places: the per-object draw (pass 0) projects in the
// vertex stage from its own baked textures, the cross-object draw (pass 1) reads the shared
// view-data textures a pre-pass filled. Only that first step differs; from here they are the
// same shader, and this file is what makes that literally true rather than by inspection.
//
// Blend/sort convention matches RenderGaussianSplats.shader: premultiplied back-to-front "over".
#ifndef GAUSSIAN_SPLAT_NO_COMPUTE_DRAW_HLSL
#define GAUSSIAN_SPLAT_NO_COMPUTE_DRAW_HLSL

#include "GaussianSplatNoComputeCommon.hlsl"

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

// Overdraw debug, same global as the compute path.
int _SplatOverdrawCount;

// Manual scene-depth test. Always the live path here: SplatDepthOcclusion uses it on OpenGL ES
// regardless of render scale, because the camera depth ATTACHMENT cannot be bound alongside our
// offscreen colour target when the orientations disagree. See RenderGaussianSplats.shader.
int _SplatManualDepthTest;
float4 _SplatDepthTexParams;
Texture2D<float> _CameraDepthTexture;
SamplerState my_point_clamp_sampler;

// Same backbuffer-vs-RT detection as FlipProjectionIfBackbuffer in GaussianSplatting.hlsl,
// declared locally because this shader cannot include that buffer-declaring header.
float4 _CameraTargetTexture_TexelSize;

/// The clipped-away vertex. Pushed far beyond the far plane with w = 1 so it clips reliably on
/// every API; w = 0 would rasterise NaN needles on GLES (see RenderGaussianSplats.shader).
v2f GsCulledVertex()
{
    v2f o = (v2f)0;
    o.vertex = float4(0, 0, 1e9, 1);
    return o;
}

/// Expand one projected splat into its quad corner for vertex <paramref name="vtxID"/>.
v2f GsBuildQuad(uint vtxID, float4 clipPos, float4 axis, half4 col)
{
    v2f o = (v2f)0;
    o.col = col;

    uint vid = vtxID;
    float2 quadPos = float2(vid & 1, (vid >> 1) & 1) * 2.0 - 1.0;
    quadPos *= 2;
    o.pos = quadPos;

    float2 deltaScreenPos = (quadPos.x * axis.xy + quadPos.y * axis.zw) * 2 / _VecScreenParams.xy;
    o.vertex = clipPos;
    o.vertex.xy += deltaScreenPos * clipPos.w;

    // Backbuffer-vs-RT y-flip, same as FlipProjectionIfBackbuffer.
    if (_CameraTargetTexture_TexelSize.z == 1.0)
        o.vertex.y = -o.vertex.y;
    return o;
}

half4 GsSplatFrag(v2f i)
{
    // Overdraw debug: with additive blending, 1 per covered fragment makes the splat RT hold
    // the layer count. Returns before the gaussian evaluation, as on the compute path.
    if (_SplatOverdrawCount != 0)
        return half4(1, 1, 1, 1);

    // Manual scene-depth test - see the uniform's comment.
    if (_SplatManualDepthTest != 0)
    {
        float sceneZ = _CameraDepthTexture.SampleLevel(my_point_clamp_sampler, i.vertex.xy * _SplatDepthTexParams.xy, 0);
        #if UNITY_REVERSED_Z
        if (i.vertex.z < sceneZ)
            discard;
        #else
        if (i.vertex.z > sceneZ)
            discard;
        #endif
    }

    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);
    // Same NaN/Inf/out-of-range guard as the compute path. No selection branch: editing is a
    // compute-path/editor feature.
    const half kMaxCol = 1e4;
    half4 c = i.col;
    if (!(abs(c.r) < kMaxCol && abs(c.g) < kMaxCol && abs(c.b) < kMaxCol && abs(c.a) < kMaxCol))
        discard;
    alpha = saturate(alpha * i.col.a);

    if (alpha < 1.0/255.0)
        discard;

    return half4(i.col.rgb * alpha, alpha);
}

#endif // GAUSSIAN_SPLAT_NO_COMPUTE_DRAW_HLSL
