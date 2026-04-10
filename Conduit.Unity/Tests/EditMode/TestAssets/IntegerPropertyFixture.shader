Shader "Hidden/Conduit/IntegerPropertyFixture"
{
    Properties
    {
        _TestInt ("Test Int", Integer) = 0
        _TestFloat ("Test Float", Float) = 0
        _TestColor ("Test Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
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

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = input.positionOS;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return _TestColor + (_TestFloat * 0.0) + (_TestInt * 0.0);
            }
            ENDHLSL
        }
    }
}
