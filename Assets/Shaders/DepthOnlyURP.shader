Shader "URP/DepthOnlyColorMask0"
{
    SubShader
    {
        // Tags must be inside SubShader (or Pass), not at the root
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" "RenderType"="Opaque" }

        Cull Back
        ZWrite On
        ZTest LEqual
        ColorMask 0   // depth-only (no color)

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="SRPDefaultUnlit" } // simple unlit path

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionHCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return half4(0,0,0,0); // discarded by ColorMask 0
            }
            ENDHLSL
        }
    }
    // No Properties or Fallback needed
}
