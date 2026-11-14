// SPDX-License-Identifier: MIT
// One splat, from baked source data to projected view data, for the no-compute path
// (WebGL2 / GLES 3.0). This is the whole of what CSCalcViewData does on the compute path,
// written as a plain function so BOTH consumers can call it and neither can drift from the
// other:
//
//   - the per-object draw (pass 0 of RenderGaussianSplatsNoCompute.shader) calls it in the
//     vertex stage, once per quad vertex, and expands the result immediately;
//   - the view-data pre-pass (pass 2) calls it in the FRAGMENT stage, once per splat, and
//     writes the result into a shared texture that a single cross-object draw then reads.
//
// The pre-pass is the reason cross-object ordering is possible here at all. Nothing in the
// output below mentions the object it came from - it is clip space with the object transform,
// the skinning and the reveal already applied - so entries produced by different objects are
// directly comparable and directly drawable together, exactly as SplatViewData is on the
// compute path. See SplatNoComputeGlobalSort.cs.
//
// The maths comes from GaussianSplatMath.hlsl, deliberately the same file the compute path
// uses, so the two projections cannot diverge.
#ifndef GAUSSIAN_SPLAT_NO_COMPUTE_SOURCE_HLSL
#define GAUSSIAN_SPLAT_NO_COMPUTE_SOURCE_HLSL

#include "GaussianSplatNoComputeCommon.hlsl"
// Buffer-free maths only. The umbrella GaussianSplatting.hlsl declares StructuredBuffers from
// _SplatChunks onwards and so cannot be included on this path.
#include "GaussianSplatMath.hlsl"
// Occlusion reveal (self-contained uniforms, shared with the compute path's color hook).
#include "GaussianSplatOcclusionReveal.hlsl"

// ---- per-renderer static data (baked once, 2048-wide, 1 texel per splat) ----
Texture2D<float4> _SplatPosOpacityTex; // xyz = bind-pose centre (object space), w = opacity 0..1
Texture2D<float4> _SplatRotTex;        // xyzw = bind-pose rotation quat
Texture2D<float4> _SplatScaleTex;      // xyz = linear scale, w unused
Texture2D<float4> _SplatColTex;        // rgb = base colour (linear texture, raw bytes), a unused
Texture2D<float4> _SplatBoneIdxTex;    // top-4 bone indices, stored as floats
Texture2D<float4> _SplatBoneWtTex;     // top-4 normalised weights
// ---- per-frame data ----
Texture2D<float4> _BonePoseTex;        // 4 texels per bone: matrix rows 0..2, then delta-quat
uint _SplatTexWidth;                   // row width of the per-splat textures (and _OrderTex)
uint _SplatCount;
uint _SplatNCSkinningActive;           // distinct from the compute path's _SplatSkinningActive

float4x4 _MatrixVP;
float4x4 _MatrixMV;
float4x4 _MatrixP;
float4x4 _MatrixObjectToWorld;
float _SplatScale;
float _SplatOpacityScale;

/// Projected splat, in the same three pieces the compute path's SplatViewData carries.
/// pos.w <= 0 is the cull flag, exactly as it is there.
struct GsSplatView
{
    float4 pos;   // clip-space centre
    float4 axis;  // projected ellipse axes in pixels: axis1 = .xy, axis2 = .zw
    half4 col;    // rgb = base colour, a = opacity (reveal already applied)
};

GsSplatView GsCullSplat()
{
    GsSplatView v;
    v.pos = float4(0, 0, 0, 0);
    v.axis = float4(0, 0, 0, 0);
    v.col = half4(0, 0, 0, 0);
    return v;
}

/// Load splat <paramref name="idx"/> from this renderer's baked textures, skin it if the
/// skinner has published a pose, and project it. Returns a culled entry (pos.w == 0) rather
/// than a flag, so the two callers handle "not drawn" the same way the compute path does.
GsSplatView GsProjectSplat(uint idx)
{
    int3 coord = GsTexCoord(idx, _SplatTexWidth);
    float4 posOp = _SplatPosOpacityTex.Load(coord);
    float3 pos = posOp.xyz;
    float opacity = posOp.w;
    float4 rot = _SplatRotTex.Load(coord);
    float3 scale = _SplatScaleTex.Load(coord).xyz;
    half3 baseCol = (half3)_SplatColTex.Load(coord).rgb;

    if (_SplatNCSkinningActive != 0)
    {
        // No bind-pose yaw here, unlike the older external implementation this was ported from:
        // SplatCharacterBin.Parse already converts the bind pose to +Z (see SplatConvention), so
        // it lines up with the rig and is skinned as standard LBS - exactly what CSBoneSkinning
        // does with _SplatBind. Re-applying a yaw would double-rotate every splat.
        int4 bi = (int4)round(_SplatBoneIdxTex.Load(coord));
        float4 w = _SplatBoneWtTex.Load(coord);

        // Position: blend the top-4 bone matrices (rows), transform the bind centre.
        int4 b4 = bi * 4;
        float4 r0 = w.x * _BonePoseTex.Load(int3(b4.x + 0, 0, 0)) + w.y * _BonePoseTex.Load(int3(b4.y + 0, 0, 0))
                  + w.z * _BonePoseTex.Load(int3(b4.z + 0, 0, 0)) + w.w * _BonePoseTex.Load(int3(b4.w + 0, 0, 0));
        float4 r1 = w.x * _BonePoseTex.Load(int3(b4.x + 1, 0, 0)) + w.y * _BonePoseTex.Load(int3(b4.y + 1, 0, 0))
                  + w.z * _BonePoseTex.Load(int3(b4.z + 1, 0, 0)) + w.w * _BonePoseTex.Load(int3(b4.w + 1, 0, 0));
        float4 r2 = w.x * _BonePoseTex.Load(int3(b4.x + 2, 0, 0)) + w.y * _BonePoseTex.Load(int3(b4.y + 2, 0, 0))
                  + w.z * _BonePoseTex.Load(int3(b4.z + 2, 0, 0)) + w.w * _BonePoseTex.Load(int3(b4.w + 2, 0, 0));
        float3 skinned = float3(dot(r0.xyz, pos) + r0.w,
                                dot(r1.xyz, pos) + r1.w,
                                dot(r2.xyz, pos) + r2.w);

        // Rotation: nlerp the delta-quats, hemisphere-aligned to the dominant bone
        // (same as CSBoneSkinning).
        float4 q0 = _BonePoseTex.Load(int3(b4.x + 3, 0, 0));
        float4 q1 = _BonePoseTex.Load(int3(b4.y + 3, 0, 0));
        float4 q2 = _BonePoseTex.Load(int3(b4.z + 3, 0, 0));
        float4 q3 = _BonePoseTex.Load(int3(b4.w + 3, 0, 0));
        q1 *= (dot(q0, q1) < 0.0) ? -1.0 : 1.0;
        q2 *= (dot(q0, q2) < 0.0) ? -1.0 : 1.0;
        q3 *= (dot(q0, q3) < 0.0) ? -1.0 : 1.0;
        float4 dq = normalize(w.x * q0 + w.y * q1 + w.z * q2 + w.w * q3);

        pos = skinned;
        rot = normalize(QuatMul(dq, rot));
    }

    float3 centerWorldPos = mul(_MatrixObjectToWorld, float4(pos, 1)).xyz;
    float4 centerClipPos = mul(_MatrixVP, float4(centerWorldPos, 1));

    // Cull behind-camera AND invalid (NaN/Inf) splats, plus degenerate indices past the splat
    // count. !(w > 0) is NaN-safe.
    if (idx >= _SplatCount || !(centerClipPos.w > 0))
        return GsCullSplat();

    // Same projection as CSCalcViewData: covariance from rot+scale, EWA 2D projection,
    // ellipse decomposition - all from the shared maths header.
    float3 cov3d0, cov3d1;
    CalcCovariance3DFromRotScale(rot, scale, cov3d0, cov3d1);
    float splatScale2 = _SplatScale * _SplatScale;
    cov3d0 *= splatScale2;
    cov3d1 *= splatScale2;
    float3 cov2d = CalcCovariance2D(pos, cov3d0, cov3d1, _MatrixMV, _MatrixP, _VecScreenParams);

    float2 axis1, axis2;
    DecomposeCovariance(cov2d, axis1, axis2);
    // The axes are authored against the y-flipped render-into-texture projection of D3D/Metal;
    // OpenGL projections are never y-flipped (P._m11 > 0), so mirror there - the same fix as
    // CSCalcViewData, and the live branch on this path (WebGL2 is always OpenGL-style).
    if (_MatrixP._m11 > 0)
    {
        axis1.y = -axis1.y;
        axis2.y = -axis2.y;
    }
    // Cull splats whose axes came out non-finite or absurdly large (degenerate input data):
    // on GLES such quads rasterise as garbage needles rather than being discarded.
    float axisLen2 = dot(axis1, axis1) + dot(axis2, axis2);
    if (!(axisLen2 < 16384.0 * 16384.0)) // also true for NaN
        return GsCullSplat();

    GsSplatView v;
    v.pos = centerClipPos;
    v.axis = float4(axis1, axis2);
    v.col.rgb = baseCol; // no SH on this path - flat colour
    v.col.a = (half)min(opacity * _SplatOpacityScale, 65000);
    // Occlusion reveal: a dissolved splat is culled outright rather than drawn at zero alpha -
    // this path is fill-rate bound, and a transparent quad still pays overdraw. Folded in here
    // rather than at the draw so the pre-pass bakes it too; it hashes the LOCAL splat index,
    // which only this side still knows.
    v.col.a *= GsRevealOpacity(idx, centerWorldPos);
    if (v.col.a <= 0)
        return GsCullSplat();
    return v;
}

#endif // GAUSSIAN_SPLAT_NO_COMPUTE_SOURCE_HLSL
