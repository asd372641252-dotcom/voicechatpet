Shader "Room/Tyndall Beam URP"
{
    Properties
    {
        _BaseMap ("Soft Noise", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 0.86, 0.62, 0.075)
        _FadePower ("Length Fade", Range(0.2, 4)) = 1.35
        _NoiseStrength ("Noise Strength", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent+20"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "TyndallBeam"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _Color;
                half _FadePower;
                half _NoiseStrength;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half noiseAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                half across = saturate(1.0h - abs(input.uv.x - 0.5h) * 2.0h);
                half along = pow(saturate(sin(saturate(input.uv.y) * 3.14159h)), _FadePower);
                half noise = lerp(1.0h, noiseAlpha, _NoiseStrength);
                half alpha = _Color.a * input.color.a * across * along * noise;
                return half4(_Color.rgb, alpha);
            }
            ENDHLSL
        }
    }
}
