// SPDX-License-Identifier: MIT
//
// OCCLUSION REVEAL — scene splats between the camera and a focus point (the game's
// player character) dissolve away in a circle around the camera->focus axis, so the
// character stays readable behind scenery. The dissolve is DENSITY, not alpha: each
// splat vanishes whole or stays whole, with probability 1 inside the core radius
// falling to 0 at the rim — so the edge reads as a thinning ring of missing splats
// instead of a soft ghost.
//
// Shared by both render paths: CSCalcViewData reaches it through ModifySplatColor
// (GaussianSplatColorModifier.hlsl), the no-compute vertex shader calls it directly.
// Everything here is self-contained (own uniforms only) because both hosts include it
// before their own uniforms are declared. Uploaded per camera per renderer from
// SplatRenderer.CalcViewData / BindNoCompute; values come from the game via the
// SplatOcclusionReveal statics, gated by each renderer's m_OcclusionReveal.

float4 _GsRevealPos;   // xyz = focus world pos, w = strength (0 = whole effect off)
float4 _GsRevealCam;   // xyz = camera world pos (own upload — the shared camera uniforms are out of scope here)
float4 _GsRevealShape; // x = rim radius, y = core radius (full dissolve), z = focus margin, w = unused

// Opacity multiplier for one splat: 0 when it dissolves, 1 when it stays.
half GsRevealOpacity(uint splatIndex, float3 worldPos)
{
    float strength = _GsRevealPos.w;
    if (strength <= 0)
        return 1;
    float3 toFocus = _GsRevealPos.xyz - _GsRevealCam.xyz;
    float axisLen = length(toFocus);
    if (axisLen < 1e-3)
        return 1;
    float3 axis = toFocus / axisLen;
    float along = dot(worldPos - _GsRevealCam.xyz, axis);
    // Only splats IN FRONT of the focus dissolve. The margin keeps the stretch just
    // before it — the floor at the character's feet projects closer to the camera than
    // its chest, and without the margin the reveal would eat the ground stood on. This
    // interval test is also the occlusion test itself: with nothing between camera and
    // focus, nothing satisfies it and the effect is invisible.
    if (along < 0 || along > axisLen - _GsRevealShape.z)
        return 1;
    float radial = length(worldPos - (_GsRevealCam.xyz + axis * along));
    float density = (1 - smoothstep(_GsRevealShape.y, _GsRevealShape.x, radial)) * strength;
    // Stable per-splat draw (same hash shape as the color hook's sparkle example), so
    // the set of missing splats holds still while the camera does.
    float draw = frac(sin(splatIndex * 12.9898) * 43758.5453);
    return draw < density ? (half)0 : (half)1;
}
