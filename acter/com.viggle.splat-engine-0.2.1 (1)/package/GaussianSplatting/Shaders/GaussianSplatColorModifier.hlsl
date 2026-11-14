// SPDX-License-Identifier: MIT
//
// PER-SPLAT COLOR HOOK — this is the extension point for altering a character's splat visuals
// (color / opacity / tint / view-angle effects) without touching the rest of the renderer.
//
// CSCalcViewData (SplatUtilities.compute) calls ModifySplatColor for every splat, right after
// the lit color and opacity are computed and just before they are packed for rendering:
//
//     ModifySplatColor(splatIndex, worldPos, clipPos, normal, viewDir, color);
//
//   splatIndex : index of the splat in the asset (stable per splat — use it for masks/noise)
//   worldPos   : the splat center in world space (use it for spatial effects: gradients, dissolve)
//   clipPos    : the splat center in clip space (the same value written to view.pos). Divide
//                .xy by .w for NDC — that is how the relight effect below finds its screen pixel.
//   normal     : approximate splat surface normal (object space) — its thinnest axis
//   viewDir    : direction from splat to camera (object space, normalized)
//                dot(normal, viewDir) is the view-facing term (1 = facing camera, 0 = edge-on)
//   color      : inout. color.rgb is the linear lit color; color.a is the opacity (after
//                m_OpacityScale). Write back whatever you want rendered.
//
// The default implementation below is data-driven (tint + a fresnel rim), both identity when
// their strength is 0 — so they cost nothing until used (see GaussianSplatRenderer's Tint and
// Fresnel fields). To build your own effect, replace the body. Examples are sketched at the
// bottom; everything you need (index + position + normal + view dir + color) is in scope.

// --- Tint (data-driven) ---
// Doubles as the per-character base color / brightness: lerp(c, c*t, s) == c * lerp(1, t, s), so
// tint at strength 1 IS a plain multiply by the tint color. There is deliberately no second
// "base color" uniform - it would be the same operation twice. SkinnedSplatRenderer.baseColor
// drives these two fields at full strength for the common "this character reads too bright/dark"
// correction; the strength remains for effects that need to blend a tint in and out over time.
float4 _GsTintColor;   // rgb = tint color
float _GsTintStrength; // 0 = no tint, 1 = fully tinted (i.e. a straight multiply)

// --- Fresnel rim (view-angle, data-driven) ---
float4 _GsFresnelColor;    // rim color (HDR ok)
float _GsFresnelStrength;  // 0 = off, 1 = full
float _GsFresnelPower;     // edge sharpness (higher = thinner rim)
float _GsFresnelThreshold; // 0..1: below this view-angle the splat is fully transparent

// --- Relight (screen-space, from the proxy mesh) ---
// SplatRelightingFeature renders a proxy mesh of the character with Unity's lights into a
// screen-space texture; each splat samples the pixel it projects to and blends that lighting
// over its own baked appearance. See ProxyLightingPass.cs.
//
// Read with BILINEAR filtering, not Load: an animated splat's screen position drifts across
// the half-res texture every frame, and a point load pops to a new texel each time it crosses
// a boundary - visible as temporal flicker wherever neighboring texels differ (shadow edges).
// Bilinear makes the sampled value continuous in the splat's motion.
// NOTE: this file is included BEFORE SplatUtilities.compute declares its own uniforms, so
// nothing here may reference those (_MatrixP, _VecScreenParams, ...). Referencing one compiles
// as an undeclared identifier and takes down EVERY kernel in the file - the shader still loads
// and still reports its kernel names, so the failure surfaces only as "kernels unusable" at
// runtime. Anything this hook needs must be its own uniform, uploaded in CalcViewData.
// --- Occlusion reveal (world-space, data-driven) ---
// Dissolves scene splats standing between the camera and a focus point; uniforms and
// math live in the include, which is shared with the no-compute vertex shader (that
// path has no ModifySplatColor to hook, so the logic cannot live in this file's body).
#include "GaussianSplatOcclusionReveal.hlsl"

Texture2D _GsRelightTex;
SamplerState gs_linear_clamp_sampler; // inline sampler (Unity parses the magic name)
float4 _GsRelightParams;   // xy = 1/RT size, z = 1/reference luminance, w = 1 when the RT is valid
float _GsRelightStrength;  // 0 = off (the whole effect early-outs)
float4 _GsRelightTint;     // tints the received light only, not the base color
float _GsRelightAmbient;   // floor under the light term, so unlit faces don't go black
// 1 when the sampled row must be mirrored (the projection is NOT y-flipped, i.e. OpenGL), 0
// otherwise. Computed CPU-side from the same projection matrix the axes use, because the
// matrix uniform itself is out of scope here (see the note above).
float _GsRelightFlipY;
float _GsRelightDebug;     // 1 = paint each splat with its own relight sample (see below)

void ModifySplatColor(uint splatIndex, float3 worldPos, float3 objPos, float4 clipPos, float3 normal, float3 viewDir, inout half4 color)
{
    // Tint: multiply toward a color (identity when strength == 0).
    color.rgb = lerp(color.rgb, color.rgb * _GsTintColor.rgb, saturate(_GsTintStrength));

    // Relight: replace part of the baked appearance with lighting sampled from the proxy mesh.
    //
    // Ordered BEFORE fresnel deliberately: the fresnel branch below REPLACES color.rgb with the
    // rim color, so lighting applied after it would modulate the rim rather than the character.
    // Tint, then light the tinted color, then let the rim overwrite - which is also the sensible
    // reading physically, since the rim is an added highlight rather than a lit surface.
    //
    // Strength 0 skips the texture fetch entirely, so the effect costs nothing until enabled;
    // params.w is 0 on frames where no proxy rendered, which skips it just the same.
    // clipPos.w > 0 rejects splats behind the camera BEFORE the divide rather than clamping it:
    // a negative w flips the sign of both NDC components, which can land a splat that is behind
    // the viewer squarely inside [0,1] and have it steal the lighting of an unrelated pixel.
    // Diagnostic: paint each splat with the outcome of its own relight sample, so a failure
    // shows WHERE in the chain it happens instead of as a uniformly unchanged character.
    //   magenta = no lighting texture was published (feature/pass not running for this camera)
    //   yellow  = splat is behind the camera
    //   red     = splat projects outside the screen
    //   blue    = sampled texel has no proxy coverage (mask 0 - alignment or y-flip problem)
    //   greys   = the actual sampled lighting (what the effect would multiply in)
    if (_GsRelightDebug > 0.5)
    {
        if (_GsRelightParams.w <= 0)
            color.rgb = half3(1, 0, 1);
        else if (clipPos.w <= 0)
            color.rgb = half3(1, 1, 0);
        else
        {
            float2 dbgUv = (clipPos.xy / clipPos.w) * 0.5 + 0.5;
            if (_GsRelightFlipY > 0.5)
                dbgUv.y = 1.0 - dbgUv.y;
            if (any(dbgUv != saturate(dbgUv)))
                color.rgb = half3(1, 0, 0);
            else
            {
                float4 dbgSample = _GsRelightTex.SampleLevel(gs_linear_clamp_sampler, dbgUv, 0);
                if (dbgSample.a > 0)
                {
                    // Exactly what the real path multiplies in: the surface-lit sample.
                    color.rgb = half3(dbgSample.rgb);
                }
                else
                {
                    // No coverage at the computed texel - check the vertically mirrored one.
                    // Coverage THERE (orange) convicts the y-flip choice specifically; coverage
                    // nowhere (blue) says the texture's alpha channel is never written.
                    float2 dbgMir = float2(dbgUv.x, 1.0 - dbgUv.y);
                    color.rgb = _GsRelightTex.SampleLevel(gs_linear_clamp_sampler, dbgMir, 0).a > 0
                        ? half3(1, 0.5, 0)
                        : half3(0, 0, 1);
                }
            }
        }
        color.a = 1;
    }
    else if (_GsRelightStrength > 0 && _GsRelightParams.w > 0 && clipPos.w > 0)
    {
        float2 ndc = clipPos.xy / clipPos.w;
        float2 uv = ndc * 0.5 + 0.5;
        // Render-target y orientation, same convention as the covariance axes in CSCalcViewData:
        // D3D/Metal render-into-texture projections are y-flipped (P._m11 < 0), OpenGL's never
        // are. Our proxy RT is a render texture, so on the non-flipped backends the sampled row
        // has to be mirrored or the lighting lands upside down.
        if (_GsRelightFlipY > 0.5)
            uv.y = 1.0 - uv.y;
        // Off-screen splats have no proxy pixel to read (Load would clamp to an edge texel and
        // smear it). Leave them with their baked color.
        if (all(uv == saturate(uv)))
        {
            float4 relightSample = _GsRelightTex.SampleLevel(gs_linear_clamp_sampler, uv, 0);
            // .a is the proxy coverage mask. Splats outside the proxy silhouette (stray hair,
            // the edge of a skirt) fall back to their baked color rather than to black, which
            // is what keeps the character from getting a hard lit/unlit seam at its outline.
            float mask = saturate(relightSample.a);
            // The texture holds the proxy surface's FULL lighting (N.L x shadow + SH ambient,
            // see SplatProxyLight.shader), so every splat along a view ray reads the same
            // value - overlapping semi-transparent splats agree, and re-sorting cannot expose
            // a contrast between them. Normalize against the reference luminance so "correctly
            // lit" comes out near 1 and the blend below changes lighting CONTRAST rather than
            // overall brightness.
            float3 lighting = relightSample.rgb * _GsRelightParams.z;
            lighting = max(lighting, _GsRelightAmbient.xxx) * _GsRelightTint.rgb;
            float3 relit = color.rgb * lighting;
            color.rgb = lerp(color.rgb, relit, saturate(_GsRelightStrength) * mask);
        }
    }

    // Fresnel: transparent when the splat faces the camera, colored rim at grazing angles.
    // abs() so the (sign-ambiguous) splat normal works whichever way it points.
    if (_GsFresnelStrength > 0)
    {
        half facing = saturate(abs(dot(normalize(normal), normalize(viewDir))));
        // Rim term: 0 facing the camera, 1 edge-on. smoothstep eases in from the threshold (zero
        // slope at the threshold, so the rim turns on smoothly instead of with a hard edge);
        // raising the threshold shrinks the visible band, and power then shapes its falloff.
        half r = saturate(1.0 - facing);
        half t = smoothstep(_GsFresnelThreshold, 1.0, r);
        half rim = pow(t, max(_GsFresnelPower, 1e-3));
        color.rgb = lerp(color.rgb, _GsFresnelColor.rgb, saturate(_GsFresnelStrength));
        color.a = lerp(color.a, color.a * rim, saturate(_GsFresnelStrength));
    }

    // Occlusion reveal: LAST, and a multiply — a dissolved splat stays dissolved no
    // matter what the effects above did to its alpha. CSCalcViewData culls splats whose
    // alpha comes out 0, so a dissolved splat costs no fill either.
    color.a *= GsRevealOpacity(splatIndex, worldPos);

    // --- Build your own here, e.g. ---
    // Height-based fade (dissolve from the feet up):
    //   color.a *= saturate((worldPos.y - _FadeBottom) / max(_FadeHeight, 1e-3));
    //
    // Per-splat sparkle using the index as a stable seed:
    //   float n = frac(sin(splatIndex * 12.9898) * 43758.5453);
    //   color.rgb += n * _SparkleColor.rgb;
}
