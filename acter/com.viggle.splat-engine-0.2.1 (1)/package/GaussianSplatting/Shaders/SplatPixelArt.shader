// SPDX-License-Identifier: MIT
//
// Pixel-art filter, ported from the web engine's effects/pixelart (see
// SplatEngine/engine/src/effects/pixelart/pixelart-shaders.ts). Two things carried over, the two
// that make the look:
//
//   1. Nearest-neighbour integer-pixel math via texel LOADs rather than UV sampling. Same reason
//      the web version gives - a filtered sample of a render target is exactly what you do not
//      want here, and integer coords sidestep every half-texel/origin convention argument.
//   2. Bayer-4x4 ordered dither applied BEFORE quantisation, indexed by the PERCEPTUAL pixel
//      coordinate (the low-res texel, not the screen pixel) so the dither cells are the same
//      apparent size as the colour blocks.
//
// What is deliberately NOT carried over: the character mask RT. The web version needs a mask
// because it pixelates one buffer holding everything and has to know which pixels are character.
// URP hands us frame ordering instead - the splat composite runs at BeforeRenderingTransparents,
// so a coarse pass injected before it and a fine pass after it separate scene from character with
// no mask, no extra render, and no per-entity tagging. See SplatPixelArtFeature.
//
// Also dropped for now: outline (the Effects sample already does splat outlines), env darken
// (game-specific), colour grade (a Volume component's job in URP).

Shader "Hidden/Viggle/Splat Pixel Art"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // x = block edge in SOURCE pixels, y = 1/x, zw = source size in pixels (for the load clamp).
        float4 _PixelArtBlock;
        // x = quantisation levels per channel (0 = off), y = dither amplitude in quant steps (0 = off),
        // z = dither cell edge in SCREEN pixels.
        float4 _PixelArtColor;
        // x = 1 when a character mask is bound, yz = full-resolution frame size.
        float4 _PixelArtMask;
        Texture2D<float4> _PixelArtMaskTex;
        ENDHLSL

        // ------------------------------------------------------------------------------------
        // Pass 0 - downsample. One load per destination texel, taken from the centre of the block
        // it represents. No box filter on purpose: averaging is what makes a pixelate filter look
        // like a blur instead of like pixel art.
        // ------------------------------------------------------------------------------------
        Pass
        {
            Name "SplatPixelArtDownsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int block = (int)_PixelArtBlock.x;
                int2 dst = int2(input.positionCS.xy);
                int2 src = dst * block + (block >> 1);
                src = min(src, int2(_PixelArtBlock.zw) - 1);
                return LOAD_TEXTURE2D_X(_BlitTexture, src);
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------------------------
        // Pass 1 - upscale. Every screen pixel inside a block loads the same low-res texel, which
        // is what produces the hard block edges. Dither + quantise ride along here so they run
        // once per frame over the whole image rather than once per pixelate stage.
        // ------------------------------------------------------------------------------------
        Pass
        {
            Name "SplatPixelArtUpscale"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            // Classic Bayer 4x4, same table and same ordering as the web version.
            static const float kBayer[16] =
            {
                 0.0,  8.0,  2.0, 10.0,
                12.0,  4.0, 14.0,  6.0,
                 3.0, 11.0,  1.0,  9.0,
                15.0,  7.0, 13.0,  5.0
            };

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int block = (int)_PixelArtBlock.x;
                int2 dst = int2(input.positionCS.xy);
                // The coarse environment pass leaves character pixels untouched. The fine pass
                // runs with masking disabled and applies Character Scale to the finished frame.
                if (_PixelArtMask.x > 0.5)
                {
                    bool character = _PixelArtMaskTex.Load(
                        int3(min(dst, int2(_PixelArtMask.yz) - 1), 0)).a > 0.01;
                    // Mode 1 is the environment pass (discard inside); mode 2 is the character
                    // pass (discard outside). The passes are disjoint, so their grids never
                    // re-sample one another and the two scale sliders remain independent.
                    if ((_PixelArtMask.x < 1.5 && character) ||
                        (_PixelArtMask.x >= 1.5 && !character))
                        discard;
                }
                // The perceptual pixel: which low-res texel this screen pixel belongs to. Doubles
                // as the dither cell index, so the dot pattern scales with the blocks.
                // Unsigned: positionCS.xy is never negative here, and Metal warns that a signed
                // integer divide is much slower - both of these run per pixel.
                int2 percept = int2(uint2(dst) / (uint)block);
                percept = min(percept, int2(_PixelArtBlock.zw) - 1);

                half4 col = LOAD_TEXTURE2D_X(_BlitTexture, percept);

                float levels = _PixelArtColor.x;
                if (levels > 0.5)
                {
                    // Quantise in display space, not in the buffer's own space. The web version
                    // gets this for free - its RT is rgba8 sRGB, so "6 levels" means six evenly
                    // spaced values as the eye sees them. URP hands us a linear HDR target, where
                    // the same arithmetic bunches every step into the highlights and crushes the
                    // whole bottom half of the range to black. Convert, quantise, convert back.
                    // The saturate is what caps HDR values that would otherwise land off the top
                    // rung; bloom-bright pixels flatten to white, which is the retro look anyway.
                    float3 c = saturate(col.rgb);
                #ifndef UNITY_COLORSPACE_GAMMA
                    c = LinearToSRGB(c);
                #endif

                    float lv = levels - 1.0;
                    float dither = _PixelArtColor.y;
                    if (dither > 0.0)
                    {
                        // Sized independently of this pass's block. The colour work all happens
                        // in the fine pass, so borrowing its block put 3x3 dither cells inside
                        // every coarse environment block - dither finer than the pixel art.
                        int2 cell = int2(uint2(dst) / (uint)max(1, (int)_PixelArtColor.z));
                        int bx = cell.x & 3;
                        int by = cell.y & 3;
                        // Threshold in [-0.5, +0.5) added before the snap, so neighbouring cells
                        // land on different rungs and read as a gradient from a distance.
                        float threshold = (kBayer[by * 4 + bx] / 16.0) - 0.5;
                        c = saturate(c + threshold / lv * dither);
                    }
                    c = floor(c * lv + 0.5) / lv;

                #ifndef UNITY_COLORSPACE_GAMMA
                    c = SRGBToLinear(c);
                #endif
                    col.rgb = c;
                }

                return col;
            }
            ENDHLSL
        }

        // ------------------------------------------------------------------------------------
        // Pass 2 - override material for ordinary Character-layer renderers. Procedural splats
        // write their own alpha into the same target; this covers meshes and loading placeholders.
        // ------------------------------------------------------------------------------------
        Pass
        {
            Name "SplatPixelArtCharacterMask"
            ZTest Always
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex MaskVert
            #pragma fragment MaskFrag
            #pragma target 3.5

            struct MaskAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct MaskVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            MaskVaryings MaskVert(MaskAttributes input)
            {
                MaskVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 MaskFrag(MaskVaryings input) : SV_Target
            {
                return half4(0, 0, 0, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
