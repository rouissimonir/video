// SPDX-License-Identifier: MIT
//
// Debug visualization for SplatRelightingFeature: draws the proxy lighting texture over the
// camera image so you can see what the splats are actually sampling. Not used in normal
// rendering - see SplatRelightingFeature.m_DebugView.
Shader "Hidden/Gaussian Splatting/Splat Relight Debug"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // 1 = lighting RGB, 2 = coverage mask in alpha. Matches SplatRelightingFeature.DebugView.
            int _RelightDebugMode;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);

                if (_RelightDebugMode == 2)
                {
                    // Coverage mask: white where a proxy covered the pixel. This is what decides
                    // whether a splat is relit at all, so a black character here means the proxy
                    // is missing, on the wrong layer, or not aligned with the splats.
                    half m = saturate(c.a);
                    return half4(m, m, m, 1);
                }

                // Lighting as rendered. Black means no proxy drew here; a flat mid-grey character
                // means the proxy is lit but nothing is varying across it.
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
