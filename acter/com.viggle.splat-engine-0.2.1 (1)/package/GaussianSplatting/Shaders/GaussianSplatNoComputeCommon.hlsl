// SPDX-License-Identifier: MIT
// The handful of declarations the no-compute path's projection half and its drawing half both
// need. Split out for one reason: HLSL has no way to say "declare this if nobody else did", so a
// uniform used by two headers that are sometimes included together and sometimes alone has to
// live in a third one. Keep it to exactly that - anything used by only one side belongs there.
#ifndef GAUSSIAN_SPLAT_NO_COMPUTE_COMMON_HLSL
#define GAUSSIAN_SPLAT_NO_COMPUTE_COMMON_HLSL

// Pixel size of the camera's viewport, passed explicitly rather than read from the _ScreenParams
// built-in: that one is not reliably populated in custom passes on OpenGL ES, which is the only
// platform this path runs on. The projection reads it for the focal length, the quad expansion
// for the clip-space size of a pixel.
float4 _VecScreenParams;

// Splat index -> texel in a 1-texel-per-splat texture of the given row width. Every per-splat
// texture on this path (source data, draw order, shared view data) uses this same layout, so
// the same helper indexes all of them.
int3 GsTexCoord(uint idx, uint width)
{
    return int3((int)(idx % width), (int)(idx / width), 0);
}

#endif // GAUSSIAN_SPLAT_NO_COMPUTE_COMMON_HLSL
