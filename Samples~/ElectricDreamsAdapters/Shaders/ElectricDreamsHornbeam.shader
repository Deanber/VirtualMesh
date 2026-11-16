// Audited M_Foliage branch adapter for Electric Dreams Hornbeam.
// Binding rules and known differences: ArtSource/ElectricDreamsPcg/hornbeam-material-contract.json
Shader "ElectricDreams/Source Hornbeam"
{
    Properties
    {
        [MainTexture] _BaseMap("Source Albedo RGB", 2D) = "white" {}
        [MainColor] _BaseColor("Source Albedo Tint (RGB Overlay, A weight)", Color) = (1,1,1,0)
        _AlbedoControls("Source saturation, brightness, contrast", Vector) = (1,1,1,0)
        _LuminanceWeights("Source desaturation luminance factors", Vector) = (.2126,.7152,.0722,0)
        [NoScaleOffset] _PackedMap("Source ART (linear data)", 2D) = "white" {}
        [NoScaleOffset] _OpacityMap("Source standalone OPACITY R", 2D) = "white" {}
        [Enum(Opaque,0,PackedR,1,PackedB,2,StandaloneR,3)] _OpacitySource("Opacity source", Float) = 0
        [ToggleUI] _UseBaseAlpha("Multiply source Albedo alpha (only if connected)", Float) = 0
        _OpacityIntensity("Source Opacity Intensity", Float) = 1
        [ToggleUI] _AlphaClip("Enable source cutout", Float) = 1
        _Cutoff("Source mask clip value", Range(0,1)) = .3333
        [Normal] [NoScaleOffset] _BumpMap("Source Normal", 2D) = "bump" {}
        _BumpScale("Normal intensity adapter", Range(0,4)) = 1
        [NoScaleOffset] _RoughnessMap("Source standalone Roughness R", 2D) = "white" {}
        [Enum(Constant,0,PackedG,1,StandaloneR,2)] _RoughnessSource("Roughness source", Float) = 1
        _Roughness("Constant roughness", Range(0,1)) = .7
        [NoScaleOffset] _OcclusionMap("Source standalone AO R", 2D) = "white" {}
        [Enum(None,0,PackedR,1,PackedB,2,PackedA,3,StandaloneR,4)] _OcclusionSource("Verified AO source", Float) = 0
        _OcclusionStrength("AO strength", Range(0,1)) = 1
        [NoScaleOffset] _TransmissionMap("Source Translucency RGB", 2D) = "white" {}
        [ToggleUI] _TransmissionEnabled("Enable mapped thin-leaf transmission", Float) = 0
        _TransmissionControls("Source transmission saturation, brightness, contrast", Vector) = (1,1,1,0)
        _TransmissionRange("Source transmission min, max", Vector) = (0,1,0,0)
        _TransmissionStrength("URP transmission response calibration", Range(0,1)) = 1
        [ToggleUI] _BackfaceTintEnabled("Source Tint Leaf Backside", Float) = 0
        _BackfaceTint("Source BackFace Tint (linear)", Color) = (1,1,1,1)
        _ColorVariation("Optional calibrated variation (source disabled = 0)", Range(0,1)) = 0
        _VariationColor("Optional calibrated variation tint", Color) = (1,1,1,1)
        [Enum(Leaves,0,Trunk,1,Decoration,2)] _SourceBranch("Resolved M_Foliage branch", Float) = 0
        [ToggleUI] _TintBaseColor("Source Tint BaseColor", Float) = 0
        [ToggleUI] _SplitAlbedoControls("Source Split Albedo Controls", Float) = 0
        _LeafTint("Source Albedo Tint Leaves (A weight)", Color) = (1,1,1,0)
        _BranchTint("Source Albedo Tint Branches (A weight)", Color) = (1,1,1,0)
        _LeafColorControls("Source Albedo Controls Leaves", Vector) = (1,1,1,0)
        _BranchColorControls("Source Albedo Controls Branches", Vector) = (1,1,1,0)
        [ToggleUI] _Plant("Source Plant (force leaf mask)", Float) = 0
        [ToggleUI] _BasicControls("Source Basic Controls", Float) = 0
        [ToggleUI] _AdvancedControls("Source Advanced Controls", Float) = 0
        [ToggleUI] _LeafBacksideRoughness("Source Leaf Backside Roughness", Float) = 0
        _LeafRoughness("Leaves strength, contrast, constant", Vector) = (.5,1,.5,0)
        _BranchRoughness("Branches strength, contrast, constant", Vector) = (.5,1,.5,0)
        _LeafRoughnessRange("Source roughness min, max, backside", Vector) = (0,1,3,0)
        _SurfaceRoughnessControls("Trunk/decoration strength, contrast, min, max", Vector) = (.5,1,0,1)
        _SurfaceConstantRoughness("Source Constant Roughness", Float) = .5
        [ToggleUI] _TintTranslucency("Source Tint Translucency", Float) = 0
        _TranslucencyTint("Source Translucency Tint (A weight)", Color) = (1,1,1,0)
        [ToggleUI] _DistanceOpacity("Source Distance Based Opacity", Float) = 0
        _OpacityDistance("Source Opacity Distance converted to metres", Float) = 100
        _FarOpacityStrength("Source Opacity Strength", Float) = 1.5

        [ToggleUI] _SourceColorVariation("Source ColorVariations", Float) = 0
        _SourceColorControls("Source random amount, intensity", Vector) = (1,1,0,0)
        _SourceColor1("Source Color Var Color 01", Vector) = (.947307,.871367,.577581,1)
        _SourceColor2("Source Color Var Color 02", Vector) = (1.3,1.3,1.17606,1)
        _SourceColor3("Source Color Var Color 03", Vector) = (.623961,.768151,.89627,1)
        _SourceWorldOriginCm("Source origin offset in centimetres", Vector) = (0,0,0,0)
        [ToggleUI] _SourceSeasons("Source Seasons, non-Winter mesh path", Float) = 0
        _SourceHealth("Health offset, overlay amount", Vector) = (1,0,0,0)
        _SourceHealthTint("Health Color Overlay", Vector) = (.117647,.003922,0,0)
        _SourceSeason("Season offset, overlay amount, brightness, saturation", Vector) = (0,0,1,1)
        _SourceSeasonTint("Season Color Overlay", Vector) = (.223228,.074214,0,0)
        [ToggleUI] _SourceSeasonRandom("Source season PerInstanceRandom", Float) = 0
        _MpcHealthSeason("MPC Health, Strength, Saturation, Brightness", Vector) = (10,0,1,.5)
        [ToggleUI] _SourceLightInfluence("Source High quality light influence", Float) = 0
        _SourceLightControls("BaseColor, Subsurface strength", Vector) = (3,.2,0,0)
        _SourceLightBaseTint("Light Influence Base Color", Vector) = (1,1,1,0)
        _SourceLightTransmissionTint("Light Influence Translucency Color", Vector) = (1,1,1,0)
        [ToggleUI] _WindEnabled("Source WPO gate", Float) = 0
        _WindAmplitude("Calibrated wind displacement, metres", Range(0,.5)) = 0
        _WindFrequency("Calibrated wind rate", Float) = 1
        _WindDirection("Horizontal wind X,Z", Vector) = (.91,.38,0,0)
        [Enum(LocalHeight,0,VertexRed,1)] _WindMaskSource("Source animation mask", Float) = 1
        _WindHeight("Procedural gradient per local metre", Float) = 1
        [ToggleUI] _WindInvertMask("Source hanging-vine gradient inversion", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" "RenderType"="TransparentCutout" "DisableBatching"="True" }
        Cull Off
        ZWrite On
        AlphaToMask On

        HLSLINCLUDE
        #define _ALPHATEST_ON 1
        #ifndef UNITY_INSTANCED_RENDERER_BOUNDS
            #define UNITY_INSTANCED_RENDERER_BOUNDS 1
        #endif
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "ElectricDreamsDecalReceiver.hlsl"

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_PackedMap); SAMPLER(sampler_PackedMap);
        TEXTURE2D(_OpacityMap); SAMPLER(sampler_OpacityMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_RoughnessMap); SAMPLER(sampler_RoughnessMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_TransmissionMap); SAMPLER(sampler_TransmissionMap);

        // Identical layout in every pass and every classic/DOTS variant.
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _AlbedoControls, _LuminanceWeights, _TransmissionControls, _TransmissionRange, _WindDirection;
            half4 _BaseColor, _BackfaceTint, _VariationColor;
            half _SourceBranch, _TintBaseColor, _SplitAlbedoControls, _Plant, _BasicControls, _AdvancedControls;
            half _LeafBacksideRoughness, _SurfaceConstantRoughness, _TintTranslucency, _DistanceOpacity, _OpacityDistance, _FarOpacityStrength;
            half4 _LeafTint, _BranchTint, _LeafColorControls, _BranchColorControls;
            half4 _LeafRoughness, _BranchRoughness, _LeafRoughnessRange, _SurfaceRoughnessControls, _TranslucencyTint;
            half _OpacitySource, _UseBaseAlpha, _OpacityIntensity, _AlphaClip, _Cutoff;
            half _BumpScale, _RoughnessSource, _Roughness, _OcclusionSource, _OcclusionStrength;
            half _TransmissionEnabled, _TransmissionStrength, _BackfaceTintEnabled, _ColorVariation;
            half _WindEnabled, _WindAmplitude, _WindFrequency, _WindMaskSource, _WindHeight, _WindInvertMask;
            float4 _SourceColorControls, _SourceColor1, _SourceColor2, _SourceColor3, _SourceWorldOriginCm;
            float4 _SourceHealth, _SourceHealthTint, _SourceSeason, _SourceSeasonTint, _MpcHealthSeason;
            float4 _SourceLightControls, _SourceLightBaseTint, _SourceLightTransmissionTint;
            float _SourceColorVariation, _SourceSeasons, _SourceSeasonRandom, _SourceLightInfluence;
        CBUFFER_END

        // GRD instances normally use shared material defaults. Metadata permits
        // explicit BRG overrides without MaterialPropertyBlocks or material clones.
        // Keep channel selectors and texture bindings material-wide.
        #ifdef UNITY_DOTS_INSTANCING_ENABLED
        UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
            UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
            UNITY_DOTS_INSTANCED_PROP(float4, _BackfaceTint)
            UNITY_DOTS_INSTANCED_PROP(float4, _VariationColor)
            UNITY_DOTS_INSTANCED_PROP(float, _OpacityIntensity)
            UNITY_DOTS_INSTANCED_PROP(float, _Cutoff)
            UNITY_DOTS_INSTANCED_PROP(float, _BumpScale)
            UNITY_DOTS_INSTANCED_PROP(float, _Roughness)
            UNITY_DOTS_INSTANCED_PROP(float, _OcclusionStrength)
            UNITY_DOTS_INSTANCED_PROP(float, _TransmissionStrength)
            UNITY_DOTS_INSTANCED_PROP(float, _ColorVariation)
            UNITY_DOTS_INSTANCED_PROP(float, _WindAmplitude)
            UNITY_DOTS_INSTANCED_PROP(float, _WindFrequency)
            UNITY_DOTS_INSTANCED_PROP(float, _WindHeight)
        UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

        static float4 ed_DOTS_BaseColor, ed_DOTS_BackfaceTint, ed_DOTS_VariationColor;
        static float ed_DOTS_OpacityIntensity, ed_DOTS_Cutoff, ed_DOTS_BumpScale;
        static float ed_DOTS_Roughness, ed_DOTS_OcclusionStrength, ed_DOTS_TransmissionStrength;
        static float ed_DOTS_ColorVariation, ed_DOTS_WindAmplitude, ed_DOTS_WindFrequency, ed_DOTS_WindHeight;

        void SetupElectricDreamsMaterialCaches()
        {
            ed_DOTS_BaseColor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
            ed_DOTS_BackfaceTint = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BackfaceTint);
            ed_DOTS_VariationColor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _VariationColor);
            ed_DOTS_OpacityIntensity = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _OpacityIntensity);
            ed_DOTS_Cutoff = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Cutoff);
            ed_DOTS_BumpScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _BumpScale);
            ed_DOTS_Roughness = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Roughness);
            ed_DOTS_OcclusionStrength = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _OcclusionStrength);
            ed_DOTS_TransmissionStrength = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _TransmissionStrength);
            ed_DOTS_ColorVariation = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _ColorVariation);
            ed_DOTS_WindAmplitude = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _WindAmplitude);
            ed_DOTS_WindFrequency = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _WindFrequency);
            ed_DOTS_WindHeight = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _WindHeight);
        }
        #undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
        #define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupElectricDreamsMaterialCaches()
        #define _BaseColor ed_DOTS_BaseColor
        #define _BackfaceTint ed_DOTS_BackfaceTint
        #define _VariationColor ed_DOTS_VariationColor
        #define _OpacityIntensity ed_DOTS_OpacityIntensity
        #define _Cutoff ed_DOTS_Cutoff
        #define _BumpScale ed_DOTS_BumpScale
        #define _Roughness ed_DOTS_Roughness
        #define _OcclusionStrength ed_DOTS_OcclusionStrength
        #define _TransmissionStrength ed_DOTS_TransmissionStrength
        #define _ColorVariation ed_DOTS_ColorVariation
        #define _WindAmplitude ed_DOTS_WindAmplitude
        #define _WindFrequency ed_DOTS_WindFrequency
        #define _WindHeight ed_DOTS_WindHeight
        #endif

        #include "ElectricDreamsHornbeamSignals.hlsl"
        #include "ElectricDreamsSourceSignals.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            half3 normalOS : NORMAL;
            half4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float2 uv : TEXCOORD1;
            half3 normalWS : TEXCOORD2;
            half4 tangentWS : TEXCOORD3;
            half fog : TEXCOORD4;
            half3 vertexLighting : TEXCOORD5;
            half4 sourceVertexColor : TEXCOORD6;
            float2 sourceVariation : TEXCOORD7;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };
        struct DepthVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        float3 Displaced(Attributes input)
        {
            float3 world = TransformObjectToWorld(input.positionOS.xyz);
            if (_WindEnabled < .5h || _WindAmplitude <= 0) return world;
            half mask = _WindMaskSource > .5h ? saturate(input.color.r)
                : saturate(input.positionOS.y * _WindHeight);
            mask = lerp(mask, 1.0h - mask, step(.5h, _WindInvertMask));
            // Explicit adapter only: MF_SimpleWind/PivotPainter are not ported.
            // Default amplitude is zero; importer must not guess UE amplitude.
            float time = _Time.y * _WindFrequency;
            float wave = sin(time * 1.63 + dot(world.xz, float2(.121,.178))) * .52
                + sin(time * .59 + dot(world.xz, float2(.058,-.041))) * .48;
            float2 direction = _WindDirection.xy * rsqrt(max(dot(_WindDirection.xy, _WindDirection.xy), 1e-6));
            world.xz += direction * (wave * _WindAmplitude * mask * mask);
            return world;
        }

        half ClipCoverageAtCenter(half mask, half baseAlpha, float3 boundsCenterWS)
        {
            half coverage = 1.0h;
            if (_AlphaClip > .5h)
            {
                half opacity = mask * _OpacityIntensity * lerp(1.0h, baseAlpha, step(.5h, _UseBaseAlpha));
                if (_SourceBranch < .5h && _DistanceOpacity > .5h)
                {
                    // Same source bounds-center distance in forward/shadow/depth.
                    float distanceToObject = distance(GetCameraPositionWS(),boundsCenterWS);
                    half factor = saturate(distanceToObject / max(_OpacityDistance,.001h) - .1h);
                    opacity *= lerp(1.0h,_FarOpacityStrength,factor);
                }
                coverage = AlphaClip(opacity, _Cutoff);
            }
            return coverage;
        }
        half ClipCoverage(half mask, half baseAlpha)
        {
            return ClipCoverageAtCenter(mask, baseAlpha,
                (unity_RendererBounds_Min.xyz+unity_RendererBounds_Max.xyz)*.5);
        }
        half OpacityMask(float2 uv, half4 packed)
        {
            // A single initialized result avoids undefined return temporaries
            // when the depth variants lower conditional texture samples.
            half mask = 1.0h;
            if (_OpacitySource > 2.5h)
                mask = SAMPLE_TEXTURE2D(_OpacityMap, sampler_OpacityMap, uv).r;
            else if (_OpacitySource > 1.5h)
                mask = packed.b;
            else if (_OpacitySource > .5h)
                mask = packed.r;
            return mask;
        }
        half DepthCoverage(float2 uv)
        {
            half4 packed = half4(1.0h, 1.0h, 1.0h, 1.0h);
            if (_OpacitySource > .5h && _OpacitySource < 2.5h)
                packed = SAMPLE_TEXTURE2D(_PackedMap, sampler_PackedMap, uv);
            half baseAlpha = 1.0h;
            if (_UseBaseAlpha > .5h) baseAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a;
            return ClipCoverage(OpacityMask(uv, packed), baseAlpha);
        }

        half3 SourceColorControls(half3 color, half3 controls, half3 tint)
        {
            // Source master: desaturate by (1-saturation), multiply brightness
            // and tint, then power by contrast. LuminanceFactors is configurable.
            half grey = dot(color, _LuminanceWeights.xyz);
            half3 value = lerp(grey.xxx, color, controls.x) * controls.y * tint;
            return pow(max(value, 0), max(controls.z, .0001h));
        }

        half3 LeafNormal(Varyings input, half faceSign)
        {
            half3 geometric = SafeNormalize(input.normalWS);
            half3 tangent = input.tangentWS.xyz - geometric * dot(input.tangentWS.xyz, geometric);
            half tangentLength2 = dot(tangent,tangent);
            half3 axis = abs(geometric.y) < .99h ? half3(0,1,0) : half3(1,0,0);
            tangent = tangentLength2 > .0001h ? tangent * rsqrt(max(tangentLength2,.0001h))
                : SafeNormalize(cross(axis,geometric));
            half3 bitangent = cross(geometric,tangent) * (input.tangentWS.w < 0 ? -1.0h : 1.0h);
            half3 normalTS = lerp(half3(0,0,1),UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap,sampler_BumpMap,input.uv)),_BumpScale);
            // UE tangent-space materials flip the complete transformed normal
            // with TwoSidedSign. A linear basis transform commutes with this sign.
            normalTS *= faceSign;
            return SafeNormalize(TransformTangentToWorld(normalTS,half3x3(tangent,bitangent,geometric)));
        }

        Varyings ForwardVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionWS = Displaced(input);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS,input.tangentOS);
            output.normalWS = normals.normalWS;
            output.tangentWS = half4(normals.tangentWS,input.tangentOS.w * GetOddNegativeScale());
            output.uv = TRANSFORM_TEX(input.uv,_BaseMap);
            output.fog = ComputeFogFactor(output.positionCS.z);
            output.sourceVertexColor = input.color;
            float3 center = (unity_RendererBounds_Min.xyz+unity_RendererBounds_Max.xyz)*.5;
            float3 sourceCenterCm = float3(-center.x,center.z,center.y)*100.0+_SourceWorldOriginCm.xyz;
            float instanceRandom = EDStablePositionRandom(sourceCenterCm*.01);
            output.sourceVariation = float2(EDSourceColorVariationAlpha(sourceCenterCm,instanceRandom,_SourceColorControls.x),instanceRandom);
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                output.vertexLighting = VertexLighting(output.positionWS, normals.normalWS);
            #endif
            return output;
        }

        half4 ForwardFragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC
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
            half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
            half4 packed = SAMPLE_TEXTURE2D(_PackedMap,sampler_PackedMap,input.uv);
            half coverage = ClipCoverage(OpacityMask(input.uv, packed), baseSample.a);
            half front = IS_FRONT_VFACE(face, 1.0h, -1.0h);
            float3 sourceAlbedo = baseSample.rgb;
            if (_SourceBranch < .5 && _SourceSeasons > .5)
            {
                EDSourceSeasonParameters season;
                season.healthOffset = _SourceHealth.x;
                season.healthOverlayAmount = _SourceHealth.y;
                season.healthOverlay = _SourceHealthTint.rgb;
                season.seasonOffset = _SourceSeason.x;
                season.seasonOverlayAmount = _SourceSeason.y;
                season.seasonBrightness = _SourceSeason.z;
                season.seasonSaturation = _SourceSeason.w;
                season.seasonOverlay = _SourceSeasonTint.rgb;
                season.perInstanceRandom = _SourceSeasonRandom;
                sourceAlbedo = EDSourceHealthSeason(sourceAlbedo,packed.b,input.sourceVertexColor,_Plant,
                    input.sourceVariation.y,_MpcHealthSeason,season);
            }
            float3 albedo = HornbeamAlbedo(sourceAlbedo,packed.b);
            // Source translucency is generated before light influence and
            // ColorVariation, so color variation must not tint it a second time.
            float3 transmissionSignal = HornbeamTranslucency(albedo,packed.b);
            if (_SourceBranch < .5)
            {
                float branchMask = EDSourceBranchMask(packed.b,_Plant);
                if (_SourceLightInfluence > .5 && _Plant < .5)
                    EDSourceLightInfluence(albedo,transmissionSignal,branchMask,input.sourceVertexColor.a,
                        _BasicControls,_AdvancedControls,_SourceLightControls.x,_SourceLightControls.y,
                        _SourceLightBaseTint.rgb,_SourceLightTransmissionTint.rgb);
                if (_SourceColorVariation > .5)
                    albedo = EDSourceColorVariation(albedo,branchMask,input.sourceVariation.x,_SourceColorControls.y,
                        _SourceColor1.rgb,_SourceColor2.rgb,_SourceColor3.rgb);
            }
            if (_BackfaceTintEnabled > .5h && front < 0) albedo *= _BackfaceTint.rgb;
            if (_ColorVariation > 0)
            {
                // Optional, opt-in calibration; not presented as MF_WorldspaceColorVariation parity.
                float2 origin = TransformObjectToWorld(float3(0,0,0)).xz;
                half random = frac(sin(dot(origin,float2(12.9898,78.233))) * 43758.5453);
                albedo *= lerp(half3(1,1,1), _VariationColor.rgb, random * _ColorVariation);
            }
            half roughness = HornbeamRoughness(packed,front);
            half occlusion = (_SourceBranch > .5h && _SourceBranch < 1.5h) ? packed.g : packed.b;

            InputData data = (InputData)0;
            data.positionWS = input.positionWS;
            data.positionCS = input.positionCS;
            data.normalWS = LeafNormal(input, front);
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
            SurfaceData surface = (SurfaceData)0;
            surface.albedo = albedo;
            surface.smoothness = 1.0h - saturate(roughness);
            surface.occlusion = lerp(1.0h, saturate(occlusion), _OcclusionStrength);
            surface.alpha = coverage;
            ApplyElectricDreamsDecal(input.positionCS,surface,data);
            half4 result = UniversalFragmentPBR(data, surface);

            // URP thin-leaf response using the source translucency signal. This
            // is explicitly not UE's Substrate/TwoSidedFoliage BRDF or thickness.
            if (_SourceBranch < .5h && _TransmissionEnabled > .5h && _TransmissionStrength > 0)
            {
                half3 signal = transmissionSignal;
                AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(data.normalizedScreenSpaceUV, surface.occlusion);
                Light sun = GetMainLight(data, data.shadowMask, ao);
                #ifdef _LIGHT_LAYERS
                if (IsMatchingLightLayer(sun.layerMask, GetMeshRenderingLayer()))
                #endif
                {
                    half backlit = saturate(-dot(data.normalWS,sun.direction));
                    result.rgb += signal * (backlit * _TransmissionStrength)
                        * sun.color * sun.distanceAttenuation * sun.shadowAttenuation;
                }
            }
            result.rgb = MixFog(result.rgb,input.fog);
            result.a = OutputAlpha(coverage,false);
            return result;
        }

        DepthVaryings DepthVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input,output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = TransformWorldToHClip(Displaced(input));
            output.uv = TRANSFORM_TEX(input.uv,_BaseMap);
            return output;
        }
        float3 _LightDirection, _LightPosition;
        DepthVaryings ShadowVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input,output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 world = Displaced(input);
            half3 normal = TransformObjectToWorldNormal(input.normalOS);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                half3 light = normalize(_LightPosition-world);
            #else
                half3 light = _LightDirection;
            #endif
            output.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(world,normal,light)));
            output.uv = TRANSFORM_TEX(input.uv,_BaseMap);
            return output;
        }
        half4 DepthFragment(DepthVaryings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            half4 output = half4(0.0h, 0.0h, 0.0h, 1.0h);
            output.a = DepthCoverage(input.uv);
            return output;
        }
        void DepthNormalsFragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC,
            out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            half coverage = DepthCoverage(input.uv);
            half3 normal = LeafNormal(input,IS_FRONT_VFACE(face,1.0h,-1.0h));
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormal = PackNormalOctQuadEncode(normal);
                outNormalWS = half4(PackFloat2To888(saturate(octNormal * .5 + .5)),coverage);
            #else
                outNormalWS = half4(normal,coverage);
            #endif
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
        }

        // Explicit offline capture only; regular URP passes never use these uniforms.
        // The baker disables wind and fixes distance opacity on a temporary material clone.
        // Source texture channels, vertex masks, seasonal controls and backface rules
        // match the source ForwardFragment material evaluation.
        float4 _VMImpostorCenter;
        float4 _VMImpostorDirectionDepth;
        struct VMImpostorCaptureOutput
        {
            half4 albedoCoverage : SV_Target0;
            half4 normalRoughness : SV_Target1;
            float linearDepth : SV_Target2;
        };
        VMImpostorCaptureOutput VMImpostorCaptureFragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
            half4 packed = SAMPLE_TEXTURE2D(_PackedMap,sampler_PackedMap,input.uv);
            // Direct command-buffer captures do not populate UnityPerDraw renderer
            // bounds. Use the explicitly supplied model center with the same source
            // alpha expression; ordinary rendering keeps its native bounds wrapper.
            half coverage = ClipCoverageAtCenter(OpacityMask(input.uv, packed), baseSample.a, _VMImpostorCenter.xyz);
            half front = IS_FRONT_VFACE(face, 1.0h, -1.0h);
            float3 sourceAlbedo = baseSample.rgb;
            if (_SourceBranch < .5 && _SourceSeasons > .5)
            {
                EDSourceSeasonParameters season;
                season.healthOffset = _SourceHealth.x;
                season.healthOverlayAmount = _SourceHealth.y;
                season.healthOverlay = _SourceHealthTint.rgb;
                season.seasonOffset = _SourceSeason.x;
                season.seasonOverlayAmount = _SourceSeason.y;
                season.seasonBrightness = _SourceSeason.z;
                season.seasonSaturation = _SourceSeason.w;
                season.seasonOverlay = _SourceSeasonTint.rgb;
                season.perInstanceRandom = _SourceSeasonRandom;
                sourceAlbedo = EDSourceHealthSeason(sourceAlbedo,packed.b,input.sourceVertexColor,_Plant,
                    input.sourceVariation.y,_MpcHealthSeason,season);
            }
            float3 albedo = HornbeamAlbedo(sourceAlbedo,packed.b);
            // Source translucency is generated before light influence and
            // ColorVariation, so color variation must not tint it a second time.
            float3 transmissionSignal = HornbeamTranslucency(albedo,packed.b);
            if (_SourceBranch < .5)
            {
                float branchMask = EDSourceBranchMask(packed.b,_Plant);
                if (_SourceLightInfluence > .5 && _Plant < .5)
                    EDSourceLightInfluence(albedo,transmissionSignal,branchMask,input.sourceVertexColor.a,
                        _BasicControls,_AdvancedControls,_SourceLightControls.x,_SourceLightControls.y,
                        _SourceLightBaseTint.rgb,_SourceLightTransmissionTint.rgb);
                if (_SourceColorVariation > .5)
                    albedo = EDSourceColorVariation(albedo,branchMask,input.sourceVariation.x,_SourceColorControls.y,
                        _SourceColor1.rgb,_SourceColor2.rgb,_SourceColor3.rgb);
            }
            if (_BackfaceTintEnabled > .5h && front < 0) albedo *= _BackfaceTint.rgb;
            if (_ColorVariation > 0)
            {
                // Optional, opt-in calibration; not presented as MF_WorldspaceColorVariation parity.
                float2 origin = TransformObjectToWorld(float3(0,0,0)).xz;
                half random = frac(sin(dot(origin,float2(12.9898,78.233))) * 43758.5453);
                albedo *= lerp(half3(1,1,1), _VariationColor.rgb, random * _ColorVariation);
            }
            half roughness = HornbeamRoughness(packed,front);
            half occlusion = (_SourceBranch > .5h && _SourceBranch < 1.5h) ? packed.g : packed.b;

            VMImpostorCaptureOutput output;
            // Material albedo, without lighting, fog, decals or baked shadows.
            output.albedoCoverage = half4(albedo, 1);
            float3 normalOS = TransformWorldToObjectNormal(LeafNormal(input, front));
            output.normalRoughness = half4(normalOS * .5 + .5, saturate(roughness));
            float3 positionOS = TransformWorldToObject(input.positionWS);
            output.linearDepth = dot(positionOS - _VMImpostorCenter.xyz,
                _VMImpostorDirectionDepth.xyz) / max(1e-8, 2 * _VMImpostorDirectionDepth.w) + .5;
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ForwardVertex
            #pragma fragment ForwardFragment
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
            #pragma vertex ShadowVertex
            #pragma fragment DepthFragment
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
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
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
            #pragma vertex ForwardVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
        Pass
        {
            Name "VMImpostorBake"
            Tags { "LightMode"="VMImpostorBake" }
            Cull Off
            ZWrite On
            ZTest LEqual
            AlphaToMask Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ForwardVertex
            #pragma fragment VMImpostorCaptureFragment
            ENDHLSL
        }
    }
    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
