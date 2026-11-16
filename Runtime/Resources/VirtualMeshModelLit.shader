Shader "Virtual Mesh/Model Lit"
{
Properties
    {
        // Specular vs Metallic workflow
        _WorkflowMode("WorkflowMode", Float) = 1.0

        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0

        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}

        _SpecColor("Specular", Color) = (0.2, 0.2, 0.2)
        _SpecGlossMap("Specular", 2D) = "white" {}

        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1.0

        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax("Scale", Range(0.005, 0.08)) = 0.005
        _ParallaxMap("Height Map", 2D) = "black" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        [HDR] _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMapScale("Scale", Range(0.0, 2.0)) = 1.0
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "linearGrey" {}
        _DetailNormalMapScale("Scale", Range(0.0, 2.0)) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}

        // SRP batching compatibility for Clear Coat (Not used in Lit)
        [HideInInspector] _ClearCoatMask("_ClearCoatMask", Float) = 0.0
        [HideInInspector] _ClearCoatSmoothness("_ClearCoatSmoothness", Float) = 0.0

        // Blending state
        _Surface("__surface", Float) = 0.0
        _Blend("__blend", Float) = 0.0
        _Cull("__cull", Float) = 2.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector] _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0
        [HideInInspector] _XRMotionVectorsPass("_XRMotionVectorsPass", Float) = 1.0

        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        // Editmode props
        _QueueOffset("Queue offset", Float) = 0.0

        // ObsoleteProperties
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _GlossMapScale("Smoothness", Float) = 0.0
        [HideInInspector] _Glossiness("Smoothness", Float) = 0.0
        [HideInInspector] _GlossyReflections("EnvironmentReflections", Float) = 0.0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    [HideInInspector] _VMUnlit("Unlit", Float) = 0
    [HideInInspector] _VMShadowCull("Shadow Cull", Float) = 2
    [HideInInspector] _VMReceiveShadows("Receive Shadows For This Draw", Float) = 1
}
SubShader
{
Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "UniversalMaterialType"="Lit" "VirtualMeshShading"="Lit" }
Pass
        {
            // Lightmode matches the ShaderPassName set in UniversalRenderPipeline.cs. SRPDefaultUnlit and passes with
            // no LightMode tag are also rendered by Universal Render Pipeline
            Name "ForwardLit"
            Tags
            {
                "LightMode" = "UniversalForwardOnly"
            }

            // -------------------------------------
            // Render State Commands
            Blend[_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
            ZWrite[_ZWrite]
            Cull[_Cull]
            AlphaToMask[_AlphaToMask]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS

            // -------------------------------------
            // Shader Stages
            #pragma vertex VMModelVertex
            #pragma fragment VMModelFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ _ALPHAPREMULTIPLY_ON _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"


            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            //--------------------------------------
            // GPU Instancing

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "VirtualMeshModelLighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            #include "VirtualMeshModelFetch.hlsl"
Varyings VMModelVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
 VMVertex v=VMFetchVertex(vertexID,instanceID); Attributes a=(Attributes)0;
 a.positionOS=float4(v.position,1); a.normalOS=v.normal; a.tangentOS=v.tangent;
 a.texcoord=v.uv0; a.staticLightmapUV=v.uv1; a.dynamicLightmapUV=0;
 return LitPassVertex(a);
}
float _VMUnlit;
void VMModelFragment(Varyings input, out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
, out uint outRenderingLayers : SV_Target1
#endif
)
{
 if (_VMUnlit > 0.5)
 {
  half4 albedo=SampleAlbedoAlpha(input.uv,TEXTURE2D_ARGS(_BaseMap,sampler_BaseMap));
  half alpha=Alpha(albedo.a,_BaseColor,_Cutoff);
  half3 color=albedo.rgb*_BaseColor.rgb;
#ifdef _ADDITIONAL_LIGHTS_VERTEX
  color=MixFog(color,input.fogFactorAndVertexLight.x);
#else
  color=MixFog(color,input.fogFactor);
#endif
  outColor=half4(color,alpha);
#ifdef _WRITE_RENDERING_LAYERS
  outRenderingLayers=EncodeMeshRenderingLayer();
#endif
  return;
 }
 LitPassFragment(input,outColor
#ifdef _WRITE_RENDERING_LAYERS
 ,outRenderingLayers
#endif
 );
}
ENDHLSL
        }
Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_VMShadowCull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex VMModelShadowVertex
            #pragma fragment ShadowPassFragment

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            #include "VirtualMeshModelFetch.hlsl"

            Varyings VMModelShadowVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                // The shadow draw binds its own LOD-selected caster list. Fetch already
                // applies the instance transform, inverse-transpose normal and mirrored winding.
                VMVertex vertex = VMFetchVertex(vertexID, instanceID);
                Varyings output = (Varyings)0;
                #if defined(_ALPHATEST_ON)
                    output.uv = TRANSFORM_TEX(vertex.uv0, _BaseMap);
                #endif

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirectionWS = normalize(_LightPosition - vertex.position);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                // Do not run TransformObjectToWorld again: VMFetchVertex returns world-space data.
                float3 biasedPositionWS = ApplyShadowBias(vertex.position, vertex.normal, lightDirectionWS);
                output.positionCS = ApplyShadowClamping(TransformWorldToHClip(biasedPositionWS));
                return output;
            }
            ENDHLSL
        }
Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS

            // -------------------------------------
            // Shader Stages
            #pragma vertex VMModelVertex
            #pragma fragment DepthOnlyFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            // -------------------------------------
            // Unity defined keywords

            //--------------------------------------
            // GPU Instancing

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            #include "VirtualMeshModelFetch.hlsl"
Varyings VMModelVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
 VMVertex v=VMFetchVertex(vertexID,instanceID); Attributes a=(Attributes)0;
 a.position=float4(v.position,1); a.texcoord=v.uv0; return DepthOnlyVertex(a);
}
ENDHLSL
        }
Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormalsOnly"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS

            // -------------------------------------
            // Shader Stages
            #pragma vertex VMModelVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            // -------------------------------------
            // Unity defined keywords

            // -------------------------------------
            // Universal Pipeline keywords
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            //--------------------------------------
            // GPU Instancing

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl"
            #include "VirtualMeshModelFetch.hlsl"
Varyings VMModelVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
 VMVertex v=VMFetchVertex(vertexID,instanceID); Attributes a=(Attributes)0;
 a.positionOS=float4(v.position,1); a.normal=v.normal; a.tangentOS=v.tangent;
 a.texcoord=v.uv0; return DepthNormalsVertex(a);
}
ENDHLSL
        }
Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }
            ZWrite Off
            ZTest LEqual
            ColorMask RG
            Cull[_Cull]
            AlphaToMask[_AlphaToMask]
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex VMModelMotionVertex
            #pragma fragment VMModelMotionFragment
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "VirtualMeshModelGeometry.hlsl"
            #include "VirtualMeshModelMotion.hlsl"

            VMMotionVaryings VMModelMotionVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                uint instanceIndex;
                VMVertex vertex = VMFetchLocalVertex(vertexID, instanceID, instanceIndex);
                VMInstance instance = _VMInstances[instanceIndex];
                float3 currentWS = mul(instance.objectToWorld, float4(vertex.position, 1)).xyz;
                float3 previousWS = currentWS;
                if (_VMGraphDraw != 0 && _VMHasPreviousFrame != 0)
                    previousWS = mul(_VMPreviousObjectToWorld[instanceIndex], float4(vertex.position, 1)).xyz;
                VMMotionVaryings output = (VMMotionVaryings)0;
                output.positionCS = TransformWorldToHClip(currentWS);
                output.currentCS = mul(_VMCurrentViewProjection, float4(currentWS, 1));
                output.previousCS = mul(_VMPreviousViewProjection, float4(previousWS, 1));
                output.uv = TRANSFORM_TEX(vertex.uv0, _BaseMap);
                output.vmInstanceIndex = instanceIndex;
                return output;
            }
            float4 VMModelMotionFragment(VMMotionVaryings input) : SV_Target
            {
                if (_VMGraphDraw == 0) clip(-1);
                half coverage = Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a,
                    _BaseColor, _Cutoff);
                return float4(VMEncodeMotion(input.currentCS, input.previousCS), 0, coverage);
            }
            ENDHLSL
        }
}
Fallback Off
}
