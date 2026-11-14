// SPDX-License-Identifier: MIT
// No-compute splat renderer for platforms without compute shaders (WebGL2 / GLES 3.0).
//
// The compute path precomputes per-splat view data (CSCalcViewData / CSBuildViewTex) and sorts
// on the GPU; neither exists on GLES 3.0. Note that this is NOT the same thing as the
// SPLAT_VERTEX_TEXTURE_FETCH variant of RenderGaussianSplats.shader: that one still reads
// ALREADY-PROJECTED view data, it just reads it from a texture instead of a buffer, so it
// still needs the compute kernel that produced it. Here nothing has been projected, so this
// shader does the whole job, the way web splat viewers do:
//
//   - draw order comes from _OrderTex, an R32F texture the CPU sorter uploads (sorted
//     far-to-near every sortNthFrame; see CpuSplatSorter.cs / CpuGlobalSplatSorter.cs);
//   - splat source data (bind pos/rot/scale/colour, bone bindings) is texelFetched from static
//     textures baked once from the decoded splat arrays (SplatNoComputeData.cs) - GLES 3.0 has
//     no vertex SSBOs, so textures are the only option;
//   - skinning is the same 4-bone LBS as SplatSkinning.compute (matrix blend for position,
//     hemisphere-aligned delta-quat nlerp for rotation), reading the per-bone palette from
//     _BonePoseTex, which the skinner re-uploads every frame (4 texels per bone: 3 matrix rows
//     + delta-quat);
//   - the 2D covariance projection is the same maths as CSCalcViewData, via the shared
//     GaussianSplatMath.hlsl - deliberately the same file the compute path uses, so the two
//     cannot drift.
//
// THREE PASSES, and which ones run depends on whether cross-object ordering is on.
//
//   Pass 0 - per-object draw. Projects in the vertex stage and rasterises, all in one. One
//     draw per splat object, each ordered only within itself. This is the default.
//
//   Pass 2 - view-data pre-pass, and Pass 1 - cross-object draw. A single draw cannot span two
//     objects while the vertex stage still needs the object's own source textures and matrices,
//     so pass 2 supplies what the compute path gets from CSCalcViewData: one fragment per splat,
//     projecting into a SHARED view-data texture set at a per-object offset. Its output mentions
//     no object - clip space, skinned, reveal applied - so pass 1 can then draw every visible
//     splat in the scene, in one global depth order, as ONE draw. See SplatNoComputeGlobalSort.cs.
//
// Passes 0 and 1 differ ONLY in where the projected splat comes from; everything after that is
// GaussianSplatNoComputeDraw.hlsl, and passes 0 and 2 share the projection itself through
// GaussianSplatNoComputeSource.hlsl. Neither pair can drift.
//
// Not supported here, all of them compute-path/editor features: spherical harmonics (flat base
// colour only), splat selection highlighting, and cutouts.
//
// No keywords, no SSBOs, no wave ops - nothing to strip, links on any GLES 3.0 driver.
// Blend/sort convention matches RenderGaussianSplats.shader: premultiplied back-to-front "over".
Shader "Gaussian Splatting/Render Splats (No Compute)"
{
    Properties
    {
        // Overdraw debug switches these to One/One, exactly as in RenderGaussianSplats.shader.
        [HideInInspector] _SrcBlend ("", Float) = 1
        [HideInInspector] _DstBlend ("", Float) = 10
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        // ---- Pass 0: per-object draw (project and rasterise in one) ------------------------
        Pass
        {
            ZWrite Off
            Blend [_SrcBlend] [_DstBlend]
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 3.5

#include "GaussianSplatNoComputeSource.hlsl"
#include "GaussianSplatNoComputeDraw.hlsl"

Texture2D<float4> _OrderTex; // draw slot -> splat index (as float), far-to-near

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    // Draw slot -> splat index via the CPU-sorted order texture. The index is stored as a
    // plain float, which is exact below 2^24 splats.
    uint idx = (uint)(_OrderTex.Load(GsTexCoord(instID, _SplatTexWidth)).r + 0.5);

    GsSplatView v = GsProjectSplat(idx);
    if (!(v.pos.w > 0))
        return GsCulledVertex();
    return GsBuildQuad(vtxID, v.pos, v.axis, v.col);
}

half4 frag (v2f i) : SV_Target { return GsSplatFrag(i); }
ENDCG
        }

        // ---- Pass 1: cross-object draw over the shared view data ---------------------------
        Pass
        {
            ZWrite Off
            Blend [_SrcBlend] [_DstBlend]
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 3.5

#include "GaussianSplatNoComputeDraw.hlsl"

// Filled by pass 2, one texel per splat, every visible object concatenated. Split across three
// targets rather than packed into one because the centre needs full float precision (a half
// clip position wobbles by a fraction of a pixel at room scale) while the axes and the colour
// do not - the compute path stores that colour as 4xFP16 for the same reason.
Texture2D<float4> _GsViewPosTex;  // clip-space centre; w <= 0 means culled
Texture2D<float4> _GsViewAxisTex; // axis1 = .xy, axis2 = .zw, in pixels
Texture2D<float4> _GsViewColTex;  // rgb = colour, a = opacity (reveal already applied)
Texture2D<float4> _OrderTex;      // draw slot -> GLOBAL view index (as float), far-to-near
uint _GsViewTexWidth;             // row width shared by all four textures above
// One order texture holds every render-order group, laid end to end, because it is uploaded from
// the CPU at record time and a second upload would rewrite what an already-recorded draw reads.
// So each draw needs to know where in it its instance 0 sits, and how far its own group runs.
uint _GsDrawSlotBase;
uint _GsDrawCount;

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    if (instID >= _GsDrawCount)
        return GsCulledVertex();
    uint slot = instID + _GsDrawSlotBase;
    uint idx = (uint)(_OrderTex.Load(GsTexCoord(slot, _GsViewTexWidth)).r + 0.5);

    int3 coord = GsTexCoord(idx, _GsViewTexWidth);
    float4 clipPos = _GsViewPosTex.Load(coord);
    // Culled in the pre-pass (behind the camera, degenerate axes, fully dissolved), or a slot
    // the pre-pass never wrote. !(w > 0) is NaN-safe, which matters: an unwritten texel of a
    // float render target holds whatever was there before.
    if (!(clipPos.w > 0))
        return GsCulledVertex();

    return GsBuildQuad(vtxID, clipPos, _GsViewAxisTex.Load(coord), (half4)_GsViewColTex.Load(coord));
}

half4 frag (v2f i) : SV_Target { return GsSplatFrag(i); }
ENDCG
        }

        // ---- Pass 2: view-data pre-pass (one fragment per splat) ---------------------------
        Pass
        {
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 3.5

#include "GaussianSplatNoComputeSource.hlsl"

// The slice of the shared view textures this object owns, as texel rows. The draw covers whole
// rows - a splat run starts and ends mid-row, and a quad cannot be clipped to a partial row -
// so the fragment shader discards the texels at either end that belong to a neighbour.
// x = first row, y = last row (exclusive), z = texture width, w = texture height.
float4 _GsViewRect;
uint _GsViewBase;   // this object's first slot in the shared textures
// 1 where texel row 0 sits at the TOP of the render target (D3D/Metal), 0 where it sits at the
// bottom (OpenGL). Set from SystemInfo.graphicsUVStartsAtTop. The texel index below is carried
// as an interpolant rather than read from SV_Position precisely so that this single uniform is
// the only place the convention appears - fragment-position origin differs between backends
// too, and having two flips to keep in agreement is how this goes wrong silently.
float _GsViewFlipY;

struct v2fView
{
    float2 texel : TEXCOORD0;  // texel coordinate within the shared textures, +0.5 at the centre
    float4 vertex : SV_POSITION;
};

v2fView vert (uint vtxID : SV_VertexID)
{
    // Two triangles over the row range, corners (0,0) (1,0) (1,1) (0,1) as 0,1,2, 0,2,3.
    uint c = vtxID < 3 ? vtxID : (vtxID == 3 ? 0 : (vtxID == 4 ? 2 : 3));
    float2 t = float2((c == 1 || c == 2) ? 1 : 0, c >= 2 ? 1 : 0);

    // Normalised texel-space v (0 = texel row 0), then the clip position that lands on it.
    float v = lerp(_GsViewRect.x, _GsViewRect.y, t.y) / _GsViewRect.w;

    v2fView o;
    o.vertex = float4(2.0 * t.x - 1.0,
                      _GsViewFlipY != 0 ? (1.0 - 2.0 * v) : (2.0 * v - 1.0),
                      0, 1);
    o.texel = float2(t.x * _GsViewRect.z, v * _GsViewRect.w);
    return o;
}

struct GsViewTargets
{
    float4 pos : SV_Target0;
    float4 axis : SV_Target1;
    float4 col : SV_Target2;
};

GsViewTargets frag (v2fView i)
{
    int2 xy = (int2)floor(i.texel);
    int local = xy.y * (int)_GsViewRect.z + xy.x - (int)_GsViewBase;
    // The partial rows this object shares with its neighbours. Discarding leaves their texels
    // as they wrote them, which is exactly right - each object owns its own run and nothing else.
    if (local < 0 || local >= (int)_SplatCount)
        discard;

    GsSplatView v = GsProjectSplat((uint)local);
    GsViewTargets o;
    o.pos = v.pos;
    o.axis = v.axis;
    o.col = (float4)v.col;
    return o;
}
ENDCG
        }
    }
}
