// SPDX-License-Identifier: MIT
// Ghost overlay for relight proxy meshes (SplatRelightingFeature Debug View > Proxy Overlay).
// Faint fill shows coverage, a fresnel-boosted rim traces the silhouette like a wireframe -
// actual line rendering would need geometry shaders or barycentrics, neither of which is
// portable to Metal, and the rim answers the same question: where does the proxy sit relative
// to the splats.
Shader "Hidden/Gaussian Splatting/Splat Proxy Debug"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "ProxyGhost"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            half4 _ProxyDebugColor;

            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 posWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(posWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewDirWS = GetWorldSpaceViewDir(posWS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half facing = saturate(abs(dot(normalize(i.normalWS), normalize(i.viewDirWS))));
                half rim = pow(1.0h - facing, 2.0h);
                half a = _ProxyDebugColor.a * (0.15h + 0.85h * rim);
                return half4(_ProxyDebugColor.rgb, a);
            }
            ENDHLSL
        }
    }
}
