// The Thundergun's air wave: bends what's behind it like heat haze. Each particle's texture is a normal-ish
// ripple (red/green = offset, alpha = strength), its vertex colour alpha fades it out. Built-in pipeline GrabPass.
Shader "Thundergun/Haze"
{
    Properties
    {
        _MainTex ("Ripple (RG offset, A mask)", 2D) = "black" {}
        _Strength ("Strength", Range(0, 0.2)) = 0.06
    }
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
        GrabPass { "_TgHazeGrab" }
        Pass
        {
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _TgHazeGrab;
            float _Strength;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 grab : TEXCOORD1; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.grab = ComputeGrabScreenPos(o.pos);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 ripple = tex2D(_MainTex, i.uv);
                float strength = ripple.a * i.color.a;
                float2 offset = (ripple.rg * 2 - 1) * _Strength * strength;
                fixed4 behind = tex2Dproj(_TgHazeGrab, float4(i.grab.xy + offset * i.grab.w, i.grab.zw));
                // A touch of brightening where the air is compressed hardest
                behind.rgb *= 1 + 0.15 * strength;
                return fixed4(behind.rgb, saturate(strength * 4));
            }
            ENDCG
        }
    }
    FallBack Off
}
