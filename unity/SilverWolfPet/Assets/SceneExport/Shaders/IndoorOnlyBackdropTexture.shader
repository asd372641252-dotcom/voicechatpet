Shader "SceneExport/IndoorOnlyBackdropTexture"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _VisibleNormal ("Visible Side Normal", Vector) = (1,0,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float4 _VisibleNormal;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.worldPos = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float3 toCamera = normalize(_WorldSpaceCameraPos.xyz - input.worldPos);
                clip(dot(normalize(_VisibleNormal.xyz), toCamera));
                fixed4 color = tex2D(_MainTex, input.uv) * _Color;
                return fixed4(color.rgb, 1);
            }
            ENDCG
        }
    }
}
