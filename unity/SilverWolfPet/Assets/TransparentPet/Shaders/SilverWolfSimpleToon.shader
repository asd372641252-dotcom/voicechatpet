Shader "DesktopPet/SilverWolfSimpleToon"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        _Color ("Base Color", Color) = (1, 1, 1, 1)
        _ShadowColor ("Shadow Color", Color) = (0.62, 0.58, 0.78, 1)
        _RampThreshold ("Ramp Threshold", Range(0, 1)) = 0.48
        _RampSoftness ("Ramp Softness", Range(0.001, 0.5)) = 0.08
        _Alpha ("Alpha", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Back
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _ShadowColor;
            float _RampThreshold;
            float _RampSoftness;
            float _Alpha;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float ndl = dot(n, l) * 0.5 + 0.5;
                float lit = smoothstep(_RampThreshold - _RampSoftness, _RampThreshold + _RampSoftness, ndl);
                fixed4 tex = tex2D(_MainTex, i.uv) * _Color;
                fixed3 toon = lerp(tex.rgb * _ShadowColor.rgb, tex.rgb, lit);
                return fixed4(toon, tex.a * _Alpha);
            }
            ENDCG
        }
    }

    Fallback "Unlit/Texture"
}
