Shader "SceneExport/DoubleSidedTransparentGlass"
{
    Properties
    {
        _Color ("Tint", Color) = (0.72,0.88,1,0.28)
        _MainTex ("Texture", 2D) = "white" {}
        _FresnelStrength ("Fresnel Strength", Range(0,1)) = 0.22
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            fixed4 _Color;
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _FresnelStrength;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldPos = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, input.uv);
                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.worldPos);
                float fresnel = pow(1.0 - saturate(abs(dot(normalize(input.worldNormal), viewDir))), 3.0);
                fixed4 color = _Color * tex;
                color.rgb += fresnel * _FresnelStrength;
                color.a = saturate(_Color.a + fresnel * _FresnelStrength);
                return color;
            }
            ENDCG
        }
    }
}
