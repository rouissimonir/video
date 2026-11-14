// SPDX-License-Identifier: MIT
// Bakes the proxy surface's FULL lighting into the relight texture: N.L from the smoothed
// proxy's real normals, main-light shadows, and spherical-harmonics ambient. Every splat then
// samples the pixel it projects to - so every splat along a view ray reads the SAME lighting,
// which is what keeps overlapping semi-transparent splats consistent: with per-splat N.L (the
// previous design) the bright back and dark front of a head disagreed, and the per-frame depth
// re-sort reshuffled that mix into visible flicker. The cost is view-dependence - you see the
// shading of the surfaces that face you - which on a smooth proxy is mostly just physics.
//
// Used by SplatRelightingFeature as the override material for the proxy draw. Alpha 1 = the
// proxy coverage mask.
Shader "Hidden/Gaussian Splatting/Splat Proxy Light"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "ProxyLightBake"
            ZWrite On
            ZTest LEqual
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            // Self-shadow acne bias, in meters, driven by SplatRelightingFeature. The trade it
            // controls: larger kills shadow acne on the proxy surface but ALSO eats real close-
            // range self-shadows (an arm 10cm from the torso stops shadowing it); smaller keeps
            // those body shadows but risks speckle on rough proxies.
            float _GsProxyShadowBias;

            half4 Frag(Varyings i) : SV_Target
            {
                // Push the shadow lookup out along the surface normal and toward the light
                // (_MainLightPosition.xyz points TOWARD a directional light), so the surface
                // does not shadow itself at zero distance.
                float3 biased = i.positionWS
                              + normalize(i.normalWS) * _GsProxyShadowBias
                              + normalize(_MainLightPosition.xyz) * _GsProxyShadowBias;
                float4 shadowCoord = TransformWorldToShadowCoord(biased);
                Light light = GetMainLight(shadowCoord);
                half3 N = normalize(i.normalWS);
                half ndl = saturate(dot(N, normalize(_MainLightPosition.xyz)));
                // Directional term shadowed and shaped by the surface, plus SH ambient so the
                // unlit side settles at the scene's environment level instead of black.
                half3 lighting = light.color * (light.shadowAttenuation * ndl) + SampleSH(N);

                // Additional lights (point / spot / extra directionals). Requires the URP asset
                // to have Additional Lights set to Per Pixel. The normal-offset position damps
                // per-light shadow acne the same way the main-light bias does; LIGHT_LOOP_BEGIN
                // reads inputData in the cluster (Forward+) variant, hence the local.
                #if defined(_ADDITIONAL_LIGHTS) || defined(_CLUSTER_LIGHT_LOOP)
                {
                    float3 lightPos = i.positionWS + N * _GsProxyShadowBias;
                    InputData inputData = (InputData)0;
                    inputData.positionWS = i.positionWS;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light al = GetAdditionalLight(lightIndex, lightPos, half4(1, 1, 1, 1));
                        half andl = saturate(dot(N, al.direction));
                        lighting += al.color * (al.distanceAttenuation * al.shadowAttenuation * andl);
                    LIGHT_LOOP_END
                }
                #endif
                return half4(lighting, 1);
            }
            ENDHLSL
        }
    }
}
