// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    Properties
    {
        // Overdraw debug switches these to One/One so each covered fragment ADDS 1, turning the
        // splat RT into a per-pixel layer count. Defaults are the normal premultiplied "over".
        // 1 = One, 10 = OneMinusSrcAlpha (UnityEngine.Rendering.BlendMode).
        [HideInInspector] _SrcBlend ("", Float) = 1
        [HideInInspector] _DstBlend ("", Float) = 10
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            // Premultiplied back-to-front "over" (uses src alpha, not dst): the dst-alpha
            // "under" blend (OneMinusDstAlpha One) accumulates incorrectly on OpenGL ES/ANGLE,
            // wrecking high-overlap regions. Requires far→near sort (useBackToFrontBlend).
            Blend [_SrcBlend] [_DstBlend]
            Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
// SPLAT_VERTEX_TEXTURE_FETCH: read per-splat view data from a texture (texelFetch) instead of
// from StructuredBuffers in the vertex stage. Many mobile GLES drivers (e.g. Mali-G72/G57)
// report GL_MAX_VERTEX_SHADER_STORAGE_BLOCKS == 0, so the default (buffer) variant fails to
// link with "vertex shader storage blocks (3) > maximum allowed (0)" and nothing renders.
// C# (GaussianSplatRenderer.UseVertexTextureFetch) enables this keyword when the vertex stage
// cannot bind compute buffers; desktop/console keep the buffer path unchanged.
#pragma multi_compile _ SPLAT_VERTEX_TEXTURE_FETCH

#include "GaussianSplatting.hlsl"

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

// Set per draw via MaterialPropertyBlock; must match what CalcViewData used for the axes.
// (The _ScreenParams built-in is not reliably populated in custom passes on all backends.)
float4 _VecScreenParams;

// Set globally via Shader.SetGlobalInteger. 0 = normal render, 1 = count layers.
int _SplatOverdrawCount;

// Manual scene-depth test for the reduced-resolution splat RT (SplatRenderScale < 1). At full
// scale the camera depth buffer is bound as a depth attachment and the hardware ZTest occludes
// splats; a scaled RT cannot bind it (attachment sizes must match), so the render paths set
// _SplatManualDepthTest = 1 and the fragment shader tests against the full-resolution camera
// depth TEXTURE instead, sampled at this fragment's normalized screen position (which makes the
// resolution mismatch irrelevant). _SplatDepthTexParams.xy = 1 / splat RT size in pixels.
// The depth texture is provided per pipeline: URP via ScriptableRenderPassInput.Depth (+ an
// explicit bind on the RenderGraph path), BiRP via camera.depthTextureMode. Also used on OpenGL
// ES (see GaussianSplatRenderer.SplatDepthOcclusion): there the camera depth ATTACHMENT cannot be
// bound alongside our offscreen colour target because the orientations disagree, but sampling the
// depth TEXTURE by normalised position has no such coupling.
int _SplatManualDepthTest;
float4 _SplatDepthTexParams;
Texture2D<float> _CameraDepthTexture;
SamplerState my_point_clamp_sampler;

#if defined(SPLAT_VERTEX_TEXTURE_FETCH)
// View data packed by the CSBuildViewTex compute kernel, already in draw order (the sort
// indirection is applied there), so no _OrderBuffer lookup is needed here. 3 RGBA32F texels
// per splat: [0]=clip pos, [1]=ellipse axes, [2]=color (rgba as plain floats, not fp16-packed).
// Selection highlighting is an editor-only feature and is omitted on this path (these are
// player-only devices). _SplatViewTexWidth is the texture width, set per draw.
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
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
#endif

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;

	float4 centerClipPos;
	float4 axis;
	half4 splatCol;
	bool selected = false;

#if defined(SPLAT_VERTEX_TEXTURE_FETCH)
	// 2 x uint4 per splat, packed by CSBuildViewTex - see the layout note there. Two Loads
	// instead of three, and 32 bytes instead of 48, on a path the vertex stage re-reads once
	// per quad corner.
	uint baseTexel = instID * 2u;
	uint4 t0 = _SplatViewDataTex.Load(ViewTexCoord(baseTexel + 0u));
	uint4 t1 = _SplatViewDataTex.Load(ViewTexCoord(baseTexel + 1u));

	centerClipPos = asfloat(t0);
	// f16tof32 reads the low half, so the second component of each pair is shifted down first.
	axis = float4(f16tof32(t1.x), f16tof32(t1.x >> 16),
	              f16tof32(t1.y), f16tof32(t1.y >> 16));
	// Same unpack order as the StructuredBuffer path below: r/b in the high half, g/a in the low.
	splatCol.r = f16tof32(t1.z >> 16);
	splatCol.g = f16tof32(t1.z);
	splatCol.b = f16tof32(t1.w >> 16);
	splatCol.a = f16tof32(t1.w);
#else
	// Clamp the ORDER lookup itself, not just its result. instID here is SV_InstanceID, i.e. the
	// draw's instance count, which on the indirect path is produced on the GPU (CSWriteDrawArgs)
	// and so is not something the CPU can bound. Reading _OrderBuffer past its end faults on
	// Metal before the existing clamp below ever runs.
	instID = _OrderBuffer[min(instID, max(_OrderBufferCount, 1u) - 1u)];
	// Defensive clamp: on the bitonic-sort fallback the order buffer is padded past splatCount and
	// its tail holds indices >= splatCount; if a real splat's sort key ties the tail sentinel, an
	// out-of-range index can reach here. _SplatViewData is sized to splatCount, so an OOB read faults
	// on Metal (D3D11 returns 0). Clamp to the last valid element (also guards the selection read below).
	instID = min(instID, max(_SplatViewDataCount, 1u) - 1u);
	SplatViewData view = _SplatViewData[instID];
	centerClipPos = view.pos;
	axis = view.axis;
	splatCol.r = f16tof32(view.color.x >> 16);
	splatCol.g = f16tof32(view.color.x);
	splatCol.b = f16tof32(view.color.y >> 16);
	splatCol.a = f16tof32(view.color.y);

	// is this splat selected?
	if (_SplatBitsValid)
	{
		uint wordIdx = instID / 32;
		uint bitIdx = instID & 31;
		uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
		if (selVal & (1 << bitIdx))
			selected = true;
	}
#endif

	// Cull behind-camera AND invalid (NaN/Inf) splats. !(w > 0) is true for w <= 0 and for
	// NaN w, so degenerate splats from bad input data are caught here too.
	bool cull = !(centerClipPos.w > 0);
	if (cull)
	{
		// Collapse all 4 quad vertices to a single point with w = 1, pushed far beyond the
		// far plane so it is reliably clipped on every API. The previous float4(0,0,2,0) used
		// w = 0: the perspective divide and attribute interpolation then compute 0/0 = NaN.
		// Metal/Vulkan discard such primitives, but GLES/ANGLE rasterizes them as colored
		// needles at screen center (NaN color -> the frag's "selected" magenta branch).
		o.vertex = float4(0, 0, 1e9, 1);
	}
	else
	{
		o.col = splatCol;

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		quadPos *= 2;

		o.pos = quadPos;

		// _VecScreenParams (ours, set per draw) instead of the _ScreenParams built-in:
		// built-in globals are not reliably populated in custom passes on all backends
		// (notably OpenGL ES), and must match what CalcViewData used to compute the axes.
		float2 deltaScreenPos = (quadPos.x * axis.xy + quadPos.y * axis.zw) * 2 / _VecScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

		if (selected)
			o.col.a = -1;
	}
	FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
    // Overdraw debug: with additive blending above, writing 1 per covered fragment makes the
    // splat RT hold the LAYER COUNT for each pixel - the ground truth for how much of the frame
    // is hidden behind already-opaque splats. Returns before the gaussian evaluation, so the
    // count reflects coverage rather than contribution (which is what we want to count).
    if (_SplatOverdrawCount != 0)
        return half4(1, 1, 1, 1);

	// Manual depth test (scaled splat RT only, see the uniform's comment). SV_Position.z is
	// this fragment's device depth, directly comparable to the depth-texture value; the
	// normalized position maps this RT pixel onto the full-res depth texture. Point sampling
	// picks one full-res depth per scaled fragment - at worst a 1-source-pixel silhouette
	// misclassification, invisible on soft gaussians.
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
	// Discard non-finite / wildly-out-of-range color from bad splat data, so it can never
	// reach the blend (where Inf/NaN saturates to green/cyan/magenta quads on GLES, and a
	// huge-but-finite opacity makes a fully saturated quad) or the "selected" branch. The
	// threshold sits far above any legitimate colour/opacity yet inside half range, so the
	// test stays meaningful in fp16: for NaN/Inf/huge values `< 1e4` is false.
	const half kMaxCol = 1e4;
	half4 c = i.col;
	if (!(abs(c.r) < kMaxCol && abs(c.g) < kMaxCol && abs(c.b) < kMaxCol && abs(c.a) < kMaxCol))
		discard;
	// Selection is the exact sentinel col.a = -1. Testing `< -0.5` (selected) and putting the
	// normal path in `else` is NaN-safe: NaN fails `< -0.5`, so any NaN that slipped past the
	// guard above still routes through the normal branch rather than the magenta outline.
	if (i.col.a < -0.5)
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}
	else
	{
		alpha = saturate(alpha * i.col.a);
	}

    if (alpha < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
