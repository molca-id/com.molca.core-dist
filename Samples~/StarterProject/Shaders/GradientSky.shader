Shader "Molca/Skybox/Gradient Sky"
{
	Properties
    {
        [Header(Gradient Colors)]
        _TopColor("Top Color", Color) = (0.4, 0.7, 1.0, 1.0)
        _MiddleColor("Middle Color", Color) = (0.8, 0.8, 0.9, 1.0)
        _BottomColor("Bottom Color", Color) = (0.2, 0.3, 0.4, 1.0)
        
        [Header(Gradient Control)]
        _TopExponent("Top Gradient Exponent", Range(0.1, 8.0)) = 2.0
        _BottomExponent("Bottom Gradient Exponent", Range(0.1, 8.0)) = 2.0
        _MiddlePoint("Middle Point", Range(0.0, 1.0)) = 0.5
        
        [Header(Environment Integration)]
        _Exposure("Exposure", Range(0.0, 8.0)) = 1.3
        _SkyboxContribution("Environment Light Contribution", Range(0.0, 2.0)) = 1.0
    }

    SubShader
    {
        Tags 
        { 
            "Queue" = "Background" 
            "RenderType" = "Background" 
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        
        Cull Off 
        ZWrite Off

        Pass
        {
            Name "Skybox"
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _TopColor;
                half4 _MiddleColor;
                half4 _BottomColor;
                half _TopExponent;
                half _BottomExponent;
                half _MiddlePoint;
                half _Exposure;
                half _SkyboxContribution;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.worldPos = vertexInput.positionWS;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Normalize world position to get direction
                float3 worldDir = normalize(input.worldPos);
                
                // Get the vertical component (-1 to 1, where 1 is up)
                float verticalGradient = worldDir.y;
                
                // Remap to 0-1 range
                float normalizedY = (verticalGradient + 1.0) * 0.5;
                
                half3 skyColor;
                
                // Create three-way gradient
                if (normalizedY > _MiddlePoint)
                {
                    // Top half: interpolate between middle and top
                    float topBlend = (normalizedY - _MiddlePoint) / (1.0 - _MiddlePoint);
                    topBlend = pow(topBlend, _TopExponent);
                    skyColor = lerp(_MiddleColor.rgb, _TopColor.rgb, topBlend);
                }
                else
                {
                    // Bottom half: interpolate between bottom and middle
                    float bottomBlend = normalizedY / _MiddlePoint;
                    bottomBlend = pow(bottomBlend, _BottomExponent);
                    skyColor = lerp(_BottomColor.rgb, _MiddleColor.rgb, bottomBlend);
                }
                
                // Apply exposure for HDR and environment lighting integration
                skyColor *= _Exposure;
                
                // Enhance contribution to environment lighting
                skyColor *= _SkyboxContribution;
                
                return half4(skyColor, 1.0);
            }
            ENDHLSL
        }
    }
    
    Fallback "Skybox/Procedural"
}