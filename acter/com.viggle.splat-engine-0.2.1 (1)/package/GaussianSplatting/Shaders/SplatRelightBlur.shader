// SPDX-License-Identifier: MIT
// 3x3 tent blur over the relight texture, color AND coverage alpha. This is what makes the
// relighting temporally stable under animation: the voxel proxy's staircase silhouette and its
// self-shadow re-rasterize differently every frame at half resolution, and splats sampling
// those hard edges pop between values. Blurring turns both into gradients that move
// continuously with the mesh.
Shader "Hidden/Gaussian Splatting/Splat Relight Blur"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always
        Pass
        {
            Name "RelightBlur"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _RelightBlurTexel; // xy = one texel in uv

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 t = _RelightBlurTexel.xy;
                float2 uv = input.texcoord;
                // Tent weights 1-2-1 x 1-2-1 (total 16). The bake writes rgb only where a=1, so
                // rgb is effectively premultiplied: averaging it raw would mix in the black
                // outside the silhouette and put a dark rim exactly where edge splats sample.
                // Un-premultiply on output - color stays full-strength to the very edge while
                // the coverage alpha fades.
                half4 sum = 0;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x, -t.y)) * 1;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(   0, -t.y)) * 2;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( t.x, -t.y)) * 1;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x,    0)) * 2;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv                      ) * 4;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( t.x,    0)) * 2;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x,  t.y)) * 1;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(   0,  t.y)) * 2;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( t.x,  t.y)) * 1;
                half a = sum.a;
                half3 rgb = a > 1e-4h ? sum.rgb / a : 0;
                return half4(rgb, a / 16.0h);
            }
            ENDHLSL
        }
    }
}
