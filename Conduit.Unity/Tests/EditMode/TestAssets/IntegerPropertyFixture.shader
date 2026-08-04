Shader "Hidden/Conduit/IntegerPropertyFixture"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Glossiness ("Glossiness", Range(0, 1)) = 0
        _Metallic ("Metallic", Range(0, 1)) = 0
        [Enum(Opaque, 0, Transparent, 1)] _Surface ("Surface", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _TestInt ("Test Int", Integer) = 0
        _TestFloat ("Test Float", Float) = 0
        _TestColor ("Test Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Name "MOTIONVECTORS"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            int _TestInt;
            float _TestFloat;
            float4 _TestColor;
            float4 _BaseColor;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = input.positionOS;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return _BaseColor + (_TestColor * 0.0) + (_TestFloat * 0.0) + (_TestInt * 0.0);
            }
            ENDHLSL
        }
    }
}
