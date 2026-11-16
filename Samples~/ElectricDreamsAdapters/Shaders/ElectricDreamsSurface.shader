// Source signal adapter. Dynamic moss masks and Substrate are not reconstructed.
// Binding evidence and limits: ArtSource/ElectricDreamsPcg/surface-material-contract.json
Shader "ElectricDreams/Source Surface"
{
    Properties
    {
        [ToggleUI] _SourceLichen("Source Use Lichen Layer", Float) = 0
        _SourceGrowthMask("Source Mask Input", 2D) = "white" {}
        _SourceLichenFuzzMap("Source hardcoded Boulder DpRF", 2D) = "white" {}
        _SourceLichenControls("Lichen intensity, contrast, world size cm, projection contrast", Vector) = (.2,3,950,5)
        _SourceWorldOriginCm("Source origin offset in centimetres", Vector) = (0,0,0,0)
        [ToggleUI] _SourceFuzzy("Source UseFuzzyShading", Float) = 0
        _SourceFuzzyControls("Fuzzy core darkness, power, edge brightness", Vector) = (.5,.5,1,0)
        [MainTexture] _BaseMap("Source base Albedo RGB", 2D) = "white" {}
        [MainColor] _BaseColor("Source base tint (linear)", Color) = (1,1,1,1)
        _BaseColorControls("Base saturation, brightness, contrast", Vector) = (1,1,1,0)
        [Normal] [NoScaleOffset] _BaseNormalMap("Source base Normal", 2D) = "bump" {}
        _BaseNormalScale("Base normal intensity", Float) = 1
        [Enum(FlattenNormal,0,ScaleXYKeepZ,1)] _BaseNormalMode("Audited base normal strength formula", Float) = 0
        [ToggleUI] _SourceUvTransform("Apply source ST before FBX V conversion", Float) = 0
        [NoScaleOffset] _BaseDataMap("Source base roughness data", 2D) = "white" {}
        _BaseRoughnessChannel("Verified base roughness RGBA channel", Vector) = (0,1,0,0)
        _BaseRoughnessControls("Base contrast, intensity, min, max", Vector) = (1,1,0,1)
        _BaseF0("Evaluated base specular F0 (linear)", Color) = (.04,.04,.04,1)
        _LuminanceWeights("Source desaturation luminance factors", Vector) = (.2126,.7152,.0722,0)
        [Enum(AdjustedUV0,0,SourceMlaUV1,1)] _SourceLayerMode("Audited source layer family", Float) = 0
        _BaseDataOcclusionChannel("Base data AO channel (zero = none)", Vector) = (0,0,0,0)
        _BaseOcclusionStrength("Source base AO Strength", Float) = 1

        [ToggleUI] _Layer1Enabled("Enable verified layer 1", Float) = 0
        _Layer1Map("Source layer 1 Albedo RGB", 2D) = "white" {}
        _Layer1Color("Source layer 1 tint (linear)", Color) = (1,1,1,1)
        _Layer1ColorControls("Layer 1 saturation, brightness, contrast", Vector) = (1,1,1,0)
        [Normal] [NoScaleOffset] _Layer1NormalMap("Source layer 1 Normal", 2D) = "bump" {}
        _Layer1NormalScale("Layer 1 normal intensity", Float) = 1
        [NoScaleOffset] _Layer1DataMap("Source layer 1 roughness data", 2D) = "white" {}
        _Layer1RoughnessChannel("Verified layer 1 roughness RGBA channel", Vector) = (0,1,0,0)
        _Layer1RoughnessControls("Layer 1 contrast, intensity, min, max", Vector) = (1,1,0,1)
        _Layer1F0("Evaluated layer 1 specular F0 (linear)", Color) = (.04,.04,.04,1)
        _Layer1DataOcclusionChannel("Layer 1 data AO channel (zero = none)", Vector) = (0,0,0,0)

        [ToggleUI] _Layer2Enabled("Enable verified layer 2", Float) = 0
        _Layer2Map("Source layer 2 Albedo RGB", 2D) = "white" {}
        _Layer2Color("Source layer 2 tint (linear)", Color) = (1,1,1,1)
        _Layer2ColorControls("Layer 2 saturation, brightness, contrast", Vector) = (1,1,1,0)
        [Normal] [NoScaleOffset] _Layer2NormalMap("Source layer 2 Normal", 2D) = "bump" {}
        _Layer2NormalScale("Layer 2 normal intensity", Float) = 1
        [NoScaleOffset] _Layer2DataMap("Source layer 2 roughness data", 2D) = "white" {}
        _Layer2RoughnessChannel("Verified layer 2 roughness RGBA channel", Vector) = (0,1,0,0)
        _Layer2RoughnessControls("Layer 2 contrast, intensity, min, max", Vector) = (1,1,0,1)
        _Layer2F0("Evaluated layer 2 specular F0 (linear)", Color) = (.04,.04,.04,1)
        _Layer2DataOcclusionChannel("Layer 2 data AO channel (zero = none)", Vector) = (0,0,0,0)

        [ToggleUI] _Layer3Enabled("Enable verified layer 3", Float) = 0
        _Layer3Map("Source layer 3 Albedo RGB", 2D) = "white" {}
        _Layer3Color("Source layer 3 tint (linear)", Color) = (1,1,1,1)
        _Layer3ColorControls("Layer 3 saturation, brightness, contrast", Vector) = (1,1,1,0)
        [Normal] [NoScaleOffset] _Layer3NormalMap("Source layer 3 Normal", 2D) = "bump" {}
        _Layer3NormalScale("Layer 3 normal intensity", Float) = 1
        [NoScaleOffset] _Layer3DataMap("Source layer 3 roughness data", 2D) = "white" {}
        _Layer3RoughnessChannel("Verified layer 3 roughness RGBA channel", Vector) = (0,1,0,0)
        _Layer3RoughnessControls("Layer 3 contrast, intensity, min, max", Vector) = (1,1,0,1)
        _Layer3F0("Evaluated layer 3 specular F0 (linear)", Color) = (.04,.04,.04,1)
        _Layer3DataOcclusionChannel("Layer 3 data AO channel (zero = none)", Vector) = (0,0,0,0)

        _LayerMaskMap("Evaluated source blend weights (linear, UV0)", 2D) = "black" {}
        _Layer1MaskChannel("Evaluated layer 1 mask RGBA channel", Vector) = (1,0,0,0)
        _Layer2MaskChannel("Evaluated layer 2 mask RGBA channel", Vector) = (0,1,0,0)
        _Layer3MaskChannel("Evaluated layer 3 mask RGBA channel", Vector) = (0,0,1,0)
        [Enum(EvaluatedWeights,0,SourceMlbaMaster,1)] _SourceMaskMode("Audited source mask formula", Float) = 0
        _Layer1BlendControls("Source blend 0 offset, modulation, contrast", Vector) = (0,1,0,0)
        _Layer2BlendControls("Source blend 1 offset, modulation, contrast", Vector) = (0,1,0,0)
        _Layer3BlendControls("Source blend 2 offset, modulation, contrast", Vector) = (0,1,0,0)
        [ToggleUI] _UniqueNormalEnabled("Combine source unique UV0 normal", Float) = 0
        [Normal] [NoScaleOffset] _UniqueNormalMap("Source Unique_Normal (UV0)", 2D) = "bump" {}
        [ToggleUI] _OcclusionEnabled("Enable evaluated source AO", Float) = 0
        _OcclusionMap("Evaluated source AO (linear, UV0)", 2D) = "white" {}
        _OcclusionChannel("Verified AO RGBA channel", Vector) = (1,0,0,0)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Source face culling", Float) = 2
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" "DisableBatching"="True" }
        Cull [_Cull]
        ZWrite On

        HLSLINCLUDE
        #define _SPECULAR_SETUP 1
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "ElectricDreamsDecalReceiver.hlsl"
        #include "ElectricDreamsSurfaceInput.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            half3 normalOS : NORMAL;
            half4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
            float2 uv1 : TEXCOORD1;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float4 uv : TEXCOORD1;
            half3 normalWS : TEXCOORD2;
            half4 tangentWS : TEXCOORD3;
            half fog : TEXCOORD4;
            half3 vertexLighting : TEXCOORD5;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };
        struct DepthVaryings
        {
            float4 positionCS : SV_POSITION;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings SurfaceVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS,input.tangentOS);
            output.normalWS = normals.normalWS;
            output.tangentWS = half4(normals.tangentWS,input.tangentOS.w * GetOddNegativeScale());
            // MLA_Master explicitly uses ObjectScale.X, not an averaged scale.
            float scaleX = length(mul((float3x3)GetObjectToWorldMatrix(),float3(1,0,0)));
            // UE's FBX exporter writes (u,1-v). Scale source UV1 before that
            // conversion, including the fractional object-scale V offset.
            output.uv = float4(input.uv,input.uv1.x * scaleX,(input.uv1.y-1.0) * scaleX + 1.0);
            output.fog = ComputeFogFactor(output.positionCS.z);
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                output.vertexLighting = VertexLighting(output.positionWS, normals.normalWS);
            #endif
            return output;
        }

        half3 SurfaceNormalWS(Varyings input, half3 weights, half faceSign)
        {
            float2 materialUV = _SourceLayerMode > .5h ? input.uv.zw : input.uv.xy;
            half3 normalTS = ReadSurfaceNormalTS(materialUV, weights);
            half3 geometric = SafeNormalize(input.normalWS);
            half3 tangent = input.tangentWS.xyz - geometric * dot(input.tangentWS.xyz,geometric);
            half length2 = dot(tangent,tangent);
            half3 axis = abs(geometric.y) < .99h ? half3(0,1,0) : half3(1,0,0);
            tangent = length2 > .0001h ? tangent * rsqrt(max(length2,.0001h))
                : SafeNormalize(cross(axis,geometric));
            half3 bitangent = cross(geometric,tangent) * (input.tangentWS.w < 0 ? -1.0h : 1.0h);
            if (_SourceLayerMode > .5h)
            {
                // Port of DeriveTangentBasis: equal-length basis normalization
                // retains UV1 anisotropy before applying the unique UV0 normal.
                float3 crossY = cross(ddy(input.positionWS),geometric);
                float3 crossX = cross(geometric,ddx(input.positionWS));
                float2 dxUV = ddx(input.uv.zw), dyUV = ddy(input.uv.zw);
                float3 derivedT = crossY * dxUV.x + crossX * dyUV.x;
                float3 derivedB = crossY * dxUV.y + crossX * dyUV.y;
                float basisLength2 = max(dot(derivedT,derivedT),dot(derivedB,derivedB));
                if (basisLength2 > 1e-12)
                {
                    float inverseLength = rsqrt(basisLength2);
                    float3 world = derivedT * (normalTS.x * inverseLength)
                        + derivedB * (normalTS.y * inverseLength) + geometric * normalTS.z;
                    normalTS = half3(dot(world,tangent),dot(world,bitangent),dot(world,geometric));
                }
            }
            if (_UniqueNormalEnabled > .5h)
            {
                half3 uniqueNormal = UnpackNormal(SAMPLE_TEXTURE2D(_UniqueNormalMap,sampler_TrilinearRepeat,input.uv.xy));
                normalTS = SourceAngleCorrectedNormals(uniqueNormal,normalTS);
            }
            normalTS.z *= faceSign;
            return SafeNormalize(TransformTangentToWorld(normalTS,half3x3(tangent,bitangent,geometric)));
        }


        void ApplySourceDynamicColor(Varyings input,inout SurfaceData surface,InputData data)
        {
            // This adapter covers graph-independent Lichen and outer Fuzzy.
            // Contact-field Moss/Oxidation is a separate explicit pending path.
            if (_SourceLichen > .5)
            {
                float3 sourcePositionCm = float3(-input.positionWS.x,input.positionWS.z,input.positionWS.y)*100.0+_SourceWorldOriginCm.xyz;
                float3 sourceNormal = float3(-input.normalWS.x,input.normalWS.z,input.normalWS.y);
                float3 q = sourcePositionCm / -max(abs(_SourceLichenControls.z),1e-6);
                // Generated source UVs must be converted for native exported
                // texture orientation; source graph keeps the negative divisor.
                float3 xz = SAMPLE_TEXTURE2D(_SourceGrowthMask,sampler_TrilinearRepeat,float2(q.x,1.0-q.z)).rgb;
                float3 yz = SAMPLE_TEXTURE2D(_SourceGrowthMask,sampler_TrilinearRepeat,float2(q.y,1.0-q.z)).rgb;
                float3 xy = SAMPLE_TEXTURE2D(_SourceGrowthMask,sampler_TrilinearRepeat,float2(q.x,1.0-q.y)).rgb;
                float mask = EDSourceWorldAlignedBlend(xz,yz,xy,sourceNormal,_SourceLichenControls.w).b;
                // MF_WaterEffect samples THIS hardcoded texture at raw UV0,
                // independent of material Tiling or the assigned DR texture.
                float fuzz = SAMPLE_TEXTURE2D(_SourceLichenFuzzMap,sampler_TrilinearRepeat,input.uv.xy).b;
                surface.albedo = EDSourceLichen(surface.albedo,mask,fuzz,_SourceLichenControls.x,_SourceLichenControls.y);
            }
            if (_SourceFuzzy > .5)
            {
                float ownFuzz = SAMPLE_TEXTURE2D(_BaseDataMap,sampler_TrilinearRepeat,SourceMapUV(input.uv.xy,_BaseMap_ST)).b;
                // FuzzyShading transforms its tangent normal without normalizing
                // that vector. Recover its magnitude after URP normal conversion.
                float normalLength = length(ReadSurfaceNormalTS(input.uv.xy,half3(0,0,0)));
                float viewDot = dot(data.normalWS,data.viewDirectionWS)*normalLength;
                float3 fuzzy = EDSourceFuzzy(surface.albedo,viewDot,_SourceFuzzyControls.x,_SourceFuzzyControls.y,_SourceFuzzyControls.z);
                surface.albedo = lerp(surface.albedo,fuzzy,ownFuzz);
            }
        }

        half4 SurfaceFragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            ) : SV_Target0
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            float2 materialUV = _SourceLayerMode > .5h ? input.uv.zw : input.uv.xy;
            half3 weights = ReadSurfaceWeights(input.uv.xy,materialUV);
            SurfaceData surface = ReadSurfaceData(input.uv.xy,materialUV,weights);
            InputData data = (InputData)0;
            data.positionWS = input.positionWS;
            data.positionCS = input.positionCS;
            data.normalWS = SurfaceNormalWS(input,weights,IS_FRONT_VFACE(face,1.0h,-1.0h));
            data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                data.shadowCoord = ComputeScreenPos(TransformWorldToHClip(input.positionWS));
            #else
                data.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #endif
            data.bakedGI = SampleSH(data.normalWS);
            data.vertexLighting = input.vertexLighting;
            data.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            data.shadowMask = half4(1,1,1,1);
            ApplySourceDynamicColor(input,surface,data);
            ApplyElectricDreamsDecal(input.positionCS,surface,data);
            half4 result = UniversalFragmentPBR(data,surface);
            result.rgb = MixFog(result.rgb,input.fog);
            result.a = 1.0h;
            return result;
        }

        DepthVaryings SurfaceDepthVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input,output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            return output;
        }
        float3 _LightDirection, _LightPosition;
        DepthVaryings SurfaceShadowVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input,output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 world = TransformObjectToWorld(input.positionOS.xyz);
            half3 normal = TransformObjectToWorldNormal(input.normalOS);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                half3 light = normalize(_LightPosition-world);
            #else
                half3 light = _LightDirection;
            #endif
            output.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(world,normal,light)));
            return output;
        }
        half4 SurfaceDepthFragment(DepthVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return half4(input.positionCS.z,0.0h,0.0h,1.0h);
        }
        void SurfaceDepthNormalsFragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC,
            out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 materialUV = _SourceLayerMode > .5h ? input.uv.zw : input.uv.xy;
            half3 weights = ReadSurfaceWeights(input.uv.xy,materialUV);
            half3 normal = SurfaceNormalWS(input,weights,IS_FRONT_VFACE(face,1.0h,-1.0h));
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormal = PackNormalOctQuadEncode(normal);
                outNormalWS = half4(PackFloat2To888(saturate(octNormal * .5 + .5)),1.0h);
            #else
                outNormalWS = half4(normal,1.0h);
            #endif
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceFragment
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fog
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SurfaceShadowVertex
            #pragma fragment SurfaceDepthFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SurfaceDepthVertex
            #pragma fragment SurfaceDepthFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SurfaceVertex
            #pragma fragment SurfaceDepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }
    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
