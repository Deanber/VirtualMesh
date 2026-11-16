// Maintained VM adapter. The legacy generator predates later fixes; do not overwrite with it.
// Audited M_Foliage branch adapter for Electric Dreams Hornbeam.
// Binding rules and known differences: ArtSource/ElectricDreamsPcg/hornbeam-material-contract.json
Shader "Virtual Mesh/ElectricDreams Hornbeam"
{
    Properties
    {
        [HideInInspector] _VMShadowCull("Virtual Mesh Shadow Cull", Float) = 0
        [HideInInspector] _VMReceiveShadows("Receive Shadows For This Draw", Float) = 1
        [ToggleUI] _VMAllowFarSimplifiedShading("Allow Simplified Distant VM Shading", Float) = 1
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
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" "RenderType"="TransparentCutout" "DisableBatching"="True" "VirtualMeshShading"="Hornbeam" }
        Cull Off
        ZWrite On
        AlphaToMask On

        HLSLINCLUDE
        #define _ALPHATEST_ON 1
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.virtualmesh/Runtime/Resources/VirtualMeshModelLighting.hlsl"
        #include "ElectricDreamsVirtualMesh.hlsl"
        #include "Packages/com.unity.virtualmesh/Runtime/Resources/VirtualMeshModelVisibility.hlsl"
        #include "../ElectricDreamsDecalReceiver.hlsl"

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_PackedMap); SAMPLER(sampler_PackedMap);
        TEXTURE2D(_OpacityMap); SAMPLER(sampler_OpacityMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_RoughnessMap); SAMPLER(sampler_RoughnessMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        TEXTURE2D(_TransmissionMap); SAMPLER(sampler_TransmissionMap);

        // Global test controls. The ordinary shader variant has no diagnostic
        // uniform or branch, and source material properties remain unchanged.
        #if defined(VM_RESOLVE_DIAGNOSTICS)
            int _VMResolveDiagnosticMode;
            half4 VMResolvePrimitiveColor(uint3 primitive)
            {
                uint hash = primitive.x * 1664525u ^ primitive.y * 1013904223u ^ primitive.z * 747796405u;
                hash ^= hash >> 16;
                hash *= 2246822519u;
                hash ^= hash >> 13;
                return half4(float3(hash & 255u, (hash >> 8) & 255u, (hash >> 16) & 255u) / 255.0, 1);
            }
        #endif

        // Preserve the original material layout identically in every VM pass.
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
            float _VMAllowFarSimplifiedShading;
        CBUFFER_END

        #include "../ElectricDreamsHornbeamSignals.hlsl"
        #include "../ElectricDreamsSourceSignals.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            half3 normalOS : NORMAL;
            half4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
            half4 color : COLOR;
            nointerpolation uint vmInstanceIndex : TEXCOORD15;
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
            nointerpolation half sourceDistanceOpacity : TEXCOORD8;
            #if defined(VM_RESOLVE_DIAGNOSTICS)
                nointerpolation uint3 diagnosticPrimitive : TEXCOORD9;
                float3 diagnosticBarycentrics : TEXCOORD10;
            #endif
            nointerpolation uint vmInstanceIndex : TEXCOORD15;
        };
        struct DepthVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            nointerpolation half sourceDistanceOpacity : TEXCOORD1;
            nointerpolation uint vmInstanceIndex : TEXCOORD15;
        };
        struct DepthNormalsVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD1;
            half3 normalWS : TEXCOORD2;
            half4 tangentWS : TEXCOORD3;
            nointerpolation half sourceDistanceOpacity : TEXCOORD8;
        };

        float3 Displaced(Attributes input)
        {
            float3 world = EDVMTransformObjectToWorld(input.positionOS.xyz);
            if (_WindEnabled < .5h || _WindAmplitude <= 0) return world;
            half mask = _WindMaskSource > .5h ? saturate(input.color.r)
                : saturate(input.positionOS.y * _WindHeight);
            mask = lerp(mask, 1.0h - mask, step(.5h, _WindInvertMask));
            // Explicit adapter only: MF_SimpleWind/PivotPainter are not ported.
            // Default amplitude is zero; importer must not guess UE amplitude.
            float time = EDVMGetTime() * _WindFrequency;
            float wave = sin(time * 1.63 + dot(world.xz, float2(.121,.178))) * .52
                + sin(time * .59 + dot(world.xz, float2(.058,-.041))) * .48;
            float2 direction = _WindDirection.xy * rsqrt(max(dot(_WindDirection.xy, _WindDirection.xy), 1e-6));
            world.xz += direction * (wave * _WindAmplitude * mask * mask);
            return world;
        }

        half SourceDistanceOpacity()
        {
            // The original source expression depends only on the current model
            // instance and camera, including in shadow and motion passes. Evaluate
            // it at vertices and pass it without interpolation instead of loading
            // the instance/model buffers and computing distance for every fragment.
            if (_AlphaClip > .5h && _SourceBranch < .5h && _DistanceOpacity > .5h)
            {
                float3 center = EDVMRendererBoundsCenter();
                float distanceToObject = distance(GetCameraPositionWS(),center);
                half factor = saturate(distanceToObject / max(_OpacityDistance,.001h) - .1h);
                return lerp(1.0h,_FarOpacityStrength,factor);
            }
            return 1.0h;
        }
        half ClipCoverage(half mask, half baseAlpha, half distanceOpacity, bool forceHardClip)
        {
            half coverage = 1.0h;
            if (_AlphaClip > .5h)
            {
                half opacity = mask * _OpacityIntensity * lerp(1.0h, baseAlpha, step(.5h, _UseBaseAlpha));
                opacity *= distanceOpacity;
                // The Graph path is admitted only for MSAA1. URP's ordinary
                // DrawObjects pass sets the global A2C availability later than
                // our early depth, so a stale global must not alter its holes.
                if (forceHardClip || _VMGraphDraw != 0)
                {
                    clip(opacity - _Cutoff);
                    // AlphaToMask remains enabled for the native MSAA path at
                    // SubShader scope. A surviving single-sample hard mask must
                    // export 1 in every pass, not a fractional sample mask.
                    coverage = 1.0h;
                }
                else coverage = AlphaClip(opacity, _Cutoff);
            }
            return coverage;
        }
        half ClipCoverage(half mask, half baseAlpha, half distanceOpacity)
        {
            return ClipCoverage(mask, baseAlpha, distanceOpacity, false);
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
        half DepthCoverage(float2 uv, half distanceOpacity, bool forceHardClip)
        {
            half4 packed = half4(1.0h, 1.0h, 1.0h, 1.0h);
            if (_OpacitySource > .5h && _OpacitySource < 2.5h)
                packed = SAMPLE_TEXTURE2D(_PackedMap, sampler_PackedMap, uv);
            half baseAlpha = 1.0h;
            if (_UseBaseAlpha > .5h) baseAlpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a;
            return ClipCoverage(OpacityMask(uv, packed), baseAlpha, distanceOpacity, forceHardClip);
        }
        half DepthCoverage(float2 uv, half distanceOpacity)
        {
            return DepthCoverage(uv, distanceOpacity, false);
        }

        half3 SourceColorControls(half3 color, half3 controls, half3 tint)
        {
            // Source master: desaturate by (1-saturation), multiply brightness
            // and tint, then power by contrast. LuminanceFactors is configurable.
            half grey = dot(color, _LuminanceWeights.xyz);
            half3 value = lerp(grey.xxx, color, controls.x) * controls.y * tint;
            return pow(max(value, 0), max(controls.z, .0001h));
        }

        half3 LeafNormal(half3 normalWS, half4 tangentWS, float2 uv, half faceSign, bool resolved, float2 uvDx, float2 uvDy)
        {
            half3 geometric = SafeNormalize(normalWS);
            half3 tangent = tangentWS.xyz - geometric * dot(tangentWS.xyz, geometric);
            half tangentLength2 = dot(tangent,tangent);
            half3 axis = abs(geometric.y) < .99h ? half3(0,1,0) : half3(1,0,0);
            tangent = tangentLength2 > .0001h ? tangent * rsqrt(max(tangentLength2,.0001h))
                : SafeNormalize(cross(axis,geometric));
            half3 bitangent = cross(geometric,tangent) * (tangentWS.w < 0 ? -1.0h : 1.0h);
            half4 bump = resolved ? SAMPLE_TEXTURE2D_GRAD(_BumpMap,sampler_BumpMap,uv,uvDx,uvDy) :
                SAMPLE_TEXTURE2D(_BumpMap,sampler_BumpMap,uv);
            half3 normalTS = lerp(half3(0,0,1),UnpackNormal(bump),_BumpScale);
            // UE tangent-space materials flip the complete transformed normal
            // with TwoSidedSign. A linear basis transform commutes with this sign.
            normalTS *= faceSign;
            return SafeNormalize(TransformTangentToWorld(normalTS,half3x3(tangent,bitangent,geometric)));
        }

        Varyings ForwardVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            EDVMSetupInstance(input.vmInstanceIndex);
            output.vmInstanceIndex = input.vmInstanceIndex;
            output.sourceDistanceOpacity = SourceDistanceOpacity();
            output.positionWS = Displaced(input);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            VertexNormalInputs normals = EDVMGetVertexNormalInputs(input.normalOS,input.tangentOS);
            output.normalWS = normals.normalWS;
            output.tangentWS = half4(normals.tangentWS,input.tangentOS.w * EDVMGetOddNegativeScale());
            output.uv = TRANSFORM_TEX(input.uv,_BaseMap);
            output.fog = ComputeFogFactor(output.positionCS.z);
            output.sourceVertexColor = input.color;
            float3 center = EDVMRendererBoundsCenter();
            float3 sourceCenterCm = float3(-center.x,center.z,center.y)*100.0+_SourceWorldOriginCm.xyz;
            float instanceRandom = EDStablePositionRandom(sourceCenterCm*.01);
            output.sourceVariation = float2(EDSourceColorVariationAlpha(sourceCenterCm,instanceRandom,_SourceColorControls.x),instanceRandom);
            #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                output.vertexLighting = VertexLighting(output.positionWS, normals.normalWS);
            #endif
            return output;
        }

        half4 ShadeHornbeam(Varyings input, half front, bool resolved, bool simplified, float2 uvDx, float2 uvDy)
        {
            EDVMSetupInstance(input.vmInstanceIndex);
            half4 baseSample = resolved ? SAMPLE_TEXTURE2D_GRAD(_BaseMap,sampler_BaseMap,input.uv,uvDx,uvDy) :
                SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,input.uv);
            half4 packed = resolved ? SAMPLE_TEXTURE2D_GRAD(_PackedMap,sampler_PackedMap,input.uv,uvDx,uvDy) :
                SAMPLE_TEXTURE2D(_PackedMap,sampler_PackedMap,input.uv);
            half coverage = 1.0h;
            if (!resolved)
                coverage = ClipCoverage(OpacityMask(input.uv, packed), baseSample.a, input.sourceDistanceOpacity);
            #if defined(VM_RESOLVE_DIAGNOSTICS)
                // Run the original raster alpha test first. Visibility already
                // established coverage for resolve; do not clip it a second time.
                if (_VMResolveDiagnosticMode == 1) return half4(1, 1, 1, 1);
                if (_VMResolveDiagnosticMode == 3) return half4(baseSample.rgb, 1);
                if (_VMResolveDiagnosticMode == 7) return VMResolvePrimitiveColor(input.diagnosticPrimitive);
                if (_VMResolveDiagnosticMode == 9) return half4(input.diagnosticBarycentrics, 1);
                if (_VMResolveDiagnosticMode == 10)
                    return half4(saturate(length(input.normalWS)).xxx, 1);
                if (_VMResolveDiagnosticMode == 8)
                    return front > 0 ? half4(0, 1, 0, 1) : half4(1, 0, 1, 1);
                if (_VMResolveDiagnosticMode == 5)
                    return half4(SafeNormalize(input.normalWS) * 0.5h + 0.5h, 1);
                if (_VMResolveDiagnosticMode == 6)
                    return half4(saturate((input.positionWS - GetCameraPositionWS()) / 256.0 + 0.5), 1);
            #endif
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
                float2 origin = EDVMTransformObjectToWorld(float3(0,0,0)).xz;
                half random = frac(sin(dot(origin,float2(12.9898,78.233))) * 43758.5453);
                albedo *= lerp(half3(1,1,1), _VariationColor.rgb, random * _ColorVariation);
            }
            half roughness = HornbeamRoughness(packed,front);
            half occlusion = (_SourceBranch > .5h && _SourceBranch < 1.5h) ? packed.g : packed.b;

            InputData data = (InputData)0;
            data.positionWS = input.positionWS;
            data.positionCS = input.positionCS;
            data.normalWS = LeafNormal(input.normalWS, input.tangentWS, input.uv, front, resolved, uvDx, uvDy);
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
            #if defined(VM_RESOLVE_DIAGNOSTICS)
                if (_VMResolveDiagnosticMode == 2)
                    return half4(data.normalWS * 0.5h + 0.5h, 1);
                if (_VMResolveDiagnosticMode == 4) return half4(surface.albedo, 1);
            #endif
            half4 result;
            [branch] if (simplified)
                result = UniversalFragmentBlinnPhong(data, surface);
            else
                result = UniversalFragmentPBR(data, surface);

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
            result.a = _VMGraphDraw != 0 ? 1.0h : OutputAlpha(coverage,false);
            return result;
        }

        half4 ForwardFragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            ) : SV_Target0
        {
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            return ShadeHornbeam(input, IS_FRONT_VFACE(face, 1.0h, -1.0h), false, false, 0, 0);
        }

        DepthVaryings DepthVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            EDVMSetupInstance(input.vmInstanceIndex);
            output.vmInstanceIndex = input.vmInstanceIndex;
            output.sourceDistanceOpacity = SourceDistanceOpacity();
            output.positionCS = TransformWorldToHClip(Displaced(input));
            output.uv = TRANSFORM_TEX(input.uv,_BaseMap);
            return output;
        }
        float3 _LightDirection, _LightPosition;
        DepthVaryings ShadowVertex(Attributes input)
        {
            DepthVaryings output = (DepthVaryings)0;
            EDVMSetupInstance(input.vmInstanceIndex);
            output.vmInstanceIndex = input.vmInstanceIndex;
            output.sourceDistanceOpacity = SourceDistanceOpacity();
            float3 world = Displaced(input);
            half3 normal = EDVMTransformObjectToWorldNormal(input.normalOS);
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
            EDVMSetupInstance(input.vmInstanceIndex);
            half4 output = half4(0.0h, 0.0h, 0.0h, 1.0h);
            output.a = DepthCoverage(input.uv, input.sourceDistanceOpacity);
            return output;
        }
        half4 ShadowFragment(DepthVaryings input) : SV_Target
        {
            // Native indirect shadow maps are single-sample as well; their ID
            // fetch remains native, but stale camera A2C state must not fill holes.
            return half4(0, 0, 0, DepthCoverage(input.uv, input.sourceDistanceOpacity, true));
        }
        half4 EncodeHornbeamDepthNormal(half3 normalWS, half4 tangentWS, float2 uv, half front, half coverage, bool resolved, float2 uvDx, float2 uvDy)
        {
            half3 normal = LeafNormal(normalWS, tangentWS, uv, front, resolved, uvDx, uvDy);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormal = PackNormalOctQuadEncode(normal);
                return half4(PackFloat2To888(saturate(octNormal * .5 + .5)), coverage);
            #else
                return half4(normal, coverage);
            #endif
        }

        half4 EncodeHornbeamDepthNormal(half3 normalWS, half4 tangentWS, float2 uv, half front, half coverage)
        {
            return EncodeHornbeamDepthNormal(normalWS, tangentWS, uv, front, coverage, false, 0, 0);
        }

        void DepthNormalsFragment(DepthNormalsVaryings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC,
            out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
        {
            half coverage = DepthCoverage(input.uv, input.sourceDistanceOpacity);
            outNormalWS = EncodeHornbeamDepthNormal(input.normalWS, input.tangentWS, input.uv,
                IS_FRONT_VFACE(face,1.0h,-1.0h), coverage);
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
        }

        // Fetch model-local attributes. Original wind, UV scaling, normal and
        // source signal functions below/above apply the explicit VM transform.
        Attributes EDVMAttributesFromVertex(VMVertex vertex, uint instanceIndex)
        {
            Attributes input = (Attributes)0;
            input.positionOS = float4(vertex.position, 1.0);
            input.normalOS = vertex.normal;
            input.tangentOS = vertex.tangent;
            input.uv = vertex.uv0;
            input.color = vertex.color;
            input.vmInstanceIndex = instanceIndex;
            return input;
        }
        Attributes EDVMReadAttributes(uint vertexID, uint instanceID)
        {
            uint instanceIndex;
            VMVertex vertex = EDVMFetchLocalVertex(vertexID, instanceID, instanceIndex);
            return EDVMAttributesFromVertex(vertex, instanceIndex);
        }
        Varyings EDVMForwardVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
        {
            Varyings output = ForwardVertex(EDVMReadAttributes(vertexID, instanceID));
            #if defined(VM_RESOLVE_DIAGNOSTICS)
                uint2 cluster;
                uint corner = vertexID;
                if (_VMGraphDraw != 0)
                    cluster = VMVisibilityGetDrawCluster(instanceID);
                else
                {
                    InitIndirectDrawArgs(0);
                    cluster = _VMVisibleClusters[GetIndirectInstanceID(instanceID)];
                    corner = GetIndirectVertexID(vertexID);
                }
                output.diagnosticPrimitive = uint3(cluster, corner / 3);
                // Rasterize unit basis values: the interpolator produces the
                // actual perspective-correct hardware barycentric coordinates.
                uint cornerInTriangle = corner % 3;
                output.diagnosticBarycentrics = float3(cornerInTriangle == 0 ? 1.0 : 0.0,
                    cornerInTriangle == 1 ? 1.0 : 0.0, cornerInTriangle == 2 ? 1.0 : 0.0);
            #endif
            return output;
        }
        DepthVaryings EDVMDepthVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
        {
            return DepthVertex(EDVMReadAttributes(vertexID, instanceID));
        }
        DepthNormalsVaryings EDVMDepthNormalsVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
        {
            Attributes input = EDVMReadAttributes(vertexID, instanceID);
            DepthNormalsVaryings output;
            output.sourceDistanceOpacity = SourceDistanceOpacity();
            output.positionCS = TransformWorldToHClip(Displaced(input));
            VertexNormalInputs normals = EDVMGetVertexNormalInputs(input.normalOS,input.tangentOS);
            output.normalWS = normals.normalWS;
            output.tangentWS = half4(normals.tangentWS,input.tangentOS.w * EDVMGetOddNegativeScale());
            output.uv = TRANSFORM_TEX(input.uv,_BaseMap);
            return output;
        }
        DepthVaryings EDVMShadowVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
        {
            return ShadowVertex(EDVMReadAttributes(vertexID, instanceID));
        }

        Texture2D<uint4> _VMVisibilityTexture;
        StructuredBuffer<uint2> _VMVisibilityTileMasks;
        float4 _VMVisibilitySize;
        uint _VMVisibilityBatchId;
        float _VMFarShadingMaxPixels;

        struct VisibilityVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            nointerpolation half sourceDistanceOpacity : TEXCOORD1;
            nointerpolation uint3 primitive : TEXCOORD2;
            half3 normalWS : TEXCOORD3;
            half4 tangentWS : TEXCOORD4;
        };

        VisibilityVaryings HornbeamVisibilityAttributes(VMVertex vertex, uint2 cluster, uint primitiveCorner, bool normals)
        {
            Attributes input = EDVMAttributesFromVertex(vertex, cluster.y);
            DepthVaryings depth = DepthVertex(input);
            VisibilityVaryings output = (VisibilityVaryings)0;
            output.positionCS = depth.positionCS;
            output.uv = depth.uv;
            output.sourceDistanceOpacity = depth.sourceDistanceOpacity;
            output.primitive = uint3(cluster, primitiveCorner / 3);
            if (normals)
            {
                VertexNormalInputs basis = EDVMGetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.normalWS = basis.normalWS;
                output.tangentWS = half4(basis.tangentWS, input.tangentOS.w * EDVMGetOddNegativeScale());
            }
            return output;
        }

        VisibilityVaryings HornbeamVisibilityVertex(uint vertexID, uint instanceID, bool normals)
        {
            uint2 cluster = VMVisibilityGetDrawCluster(instanceID);
            return HornbeamVisibilityAttributes(VMVisibilityFetchVertex(cluster.x, cluster.y, vertexID),
                cluster, vertexID, normals);
        }

        VisibilityVaryings HornbeamVisibilityIndexedVertex(uint vertexID, bool normals)
        {
            uint2 cluster = VMVisibilityGetIndexedDrawCluster(vertexID);
            // A cached vertex can belong to several triangles. The fragment
            // entry supplies the triangle ID from the rasterizer instead.
            return HornbeamVisibilityAttributes(VMVisibilityFetchIndexedVertex(cluster.x, cluster.y, vertexID & 255u),
                cluster, 0, normals);
        }

        VisibilityVaryings EDVMVisibilityVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
        {
            return HornbeamVisibilityVertex(vertexID, instanceID, false);
        }

        VisibilityVaryings EDVMVisibilityNormalsVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
        {
            return HornbeamVisibilityVertex(vertexID, instanceID, true);
        }

        VisibilityVaryings EDVMVisibilityIndexedVertex(uint vertexID : SV_VertexID)
        {
            return HornbeamVisibilityIndexedVertex(vertexID, false);
        }

        VisibilityVaryings EDVMVisibilityNormalsIndexedVertex(uint vertexID : SV_VertexID)
        {
            return HornbeamVisibilityIndexedVertex(vertexID, true);
        }

        uint4 EncodeHornbeamVisibility(VisibilityVaryings input, FRONT_FACE_TYPE face)
        {
            uint data = input.primitive.z | VMVisibilityPhaseBits() | (_VMVisibilityBatchId << 7);
            data |= IS_FRONT_VFACE(face, 0x80000000u, 0u);
            return uint4(input.primitive.xy, data, asuint(input.positionCS.z));
        }

        uint4 HornbeamVisibilityFragment(VisibilityVaryings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target0
        {
            DepthCoverage(input.uv, input.sourceDistanceOpacity);
            return EncodeHornbeamVisibility(input, face);
        }

        void HornbeamVisibilityNormalsFragment(VisibilityVaryings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC,
            out uint4 visibility : SV_Target0, out half4 normal : SV_Target1
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target2
            #endif
            )
        {
            half coverage = DepthCoverage(input.uv, input.sourceDistanceOpacity);
            visibility = EncodeHornbeamVisibility(input, face);
            normal = EncodeHornbeamDepthNormal(input.normalWS, input.tangentWS, input.uv,
                IS_FRONT_VFACE(face,1.0h,-1.0h), coverage);
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
        }

        uint4 HornbeamVisibilityIndexedFragment(VisibilityVaryings input, uint primitiveID : SV_PrimitiveID,
            FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target0
        {
            // Each single-instance indexed draw keeps 64 triangles per cluster,
            // including degenerate padding, so the low six bits stay local.
            input.primitive.z = primitiveID & 63u;
            return HornbeamVisibilityFragment(input, face);
        }

        void HornbeamVisibilityNormalsIndexedFragment(VisibilityVaryings input, uint primitiveID : SV_PrimitiveID,
            FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC,
            out uint4 visibility : SV_Target0, out half4 normal : SV_Target1
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target2
            #endif
            )
        {
            input.primitive.z = primitiveID & 63u;
            HornbeamVisibilityNormalsFragment(input, face, visibility, normal
                #ifdef _WRITE_RENDERING_LAYERS
                    , outRenderingLayers
                #endif
                );
        }

        float4 HornbeamResolveVertex(uint vertexID : SV_VertexID, uint tile : SV_InstanceID) : SV_POSITION
        {
            if (_VMVisibilityBatchId > 0 && _VMVisibilityBatchId <= 64)
            {
                uint bit = _VMVisibilityBatchId - 1;
                uint2 mask = _VMVisibilityTileMasks[tile];
                if ((mask[bit >> 5] & (1u << (bit & 31u))) == 0)
                    return float4(-2, -2, 0, 1);
            }
            uint widthInTiles = (uint)_VMVisibilitySize.z;
            float2 origin = float2(tile % widthInTiles, tile / widthInTiles) * 16;
            float2 limit = min(origin + 16, _VMVisibilitySize.xy);
            const uint corners[6] = { 0, 1, 2, 2, 1, 3 };
            uint corner = corners[vertexID];
            float2 pixel = lerp(origin, limit, float2(corner & 1, corner >> 1));
            float2 position = pixel * (2 / _VMVisibilitySize.xy) - 1;
            #if UNITY_UV_STARTS_AT_TOP
                position.y = -position.y;
            #endif
            return float4(position, VMVisibilityMaterialDepth(_VMVisibilityBatchId), 1);
        }

        #if defined(SHADER_API_D3D11) || defined(SHADER_API_D3D12)
            [earlydepthstencil]
        #endif
        void HornbeamNormalResolveFragment(float4 positionCS : SV_POSITION,
            out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
        {
            uint4 visibility = _VMVisibilityTexture.Load(int3(int2(positionCS.xy), 0));
            if (((visibility.z >> 7) & 0xffffffu) != _VMVisibilityBatchId ||
                !VMVisibilityMatchesNormalPhase(visibility))
                discard;

            // Reuse the raster normal/UV transforms, including current wind and
            // per-corner normalization. The stored winding/front face is final.
            uint corner = (visibility.z & 63u) * 3;
            VisibilityVaryings a = HornbeamVisibilityAttributes(
                VMVisibilityFetchVertex(visibility.x, visibility.y, corner), visibility.xy, corner, true);
            VisibilityVaryings b = HornbeamVisibilityAttributes(
                VMVisibilityFetchVertex(visibility.x, visibility.y, corner + 1), visibility.xy, corner + 1, true);
            VisibilityVaryings c = HornbeamVisibilityAttributes(
                VMVisibilityFetchVertex(visibility.x, visibility.y, corner + 2), visibility.xy, corner + 2, true);
            float3 weights, weightsDx, weightsDy;
            if (!VMVisibilityGetBarycentrics(positionCS.xy, _VMVisibilitySize.xy,
                a.positionCS, b.positionCS, c.positionCS, weights, weightsDx, weightsDy))
                discard;

            float2 uv = a.uv * weights.x + b.uv * weights.y + c.uv * weights.z;
            float2 uvDx = a.uv * weightsDx.x + b.uv * weightsDx.y + c.uv * weightsDx.z;
            float2 uvDy = a.uv * weightsDy.x + b.uv * weightsDy.y + c.uv * weightsDy.z;
            half front = (visibility.z & 0x80000000u) != 0 ? 1.0h : -1.0h;
            // Coverage was decided by the matching visibility sample. Never
            // repeat alpha clipping with reconstructed UVs/derivatives here.
            half3 normalWS = a.normalWS * weights.x + b.normalWS * weights.y + c.normalWS * weights.z;
            half4 tangentWS = a.tangentWS * weights.x + b.tangentWS * weights.y + c.tangentWS * weights.z;
            outNormalWS = EncodeHornbeamDepthNormal(normalWS, tangentWS, uv, front, 1.0h, true, uvDx, uvDy);
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
        }

        bool UseSimplifiedHornbeamShading(uint clusterId, uint instanceId, float viewportHeight)
        {
            if (_VMFarShadingMaxPixels <= 0 || _VMAllowFarSimplifiedShading < 0.5)
                return false;
            VMCluster cluster = _VMClusters[clusterId];
            if (cluster.type < 2 || cluster.error <= 0)
                return false;
            VMInstance instance = _VMInstances[instanceId];
            float radius = cluster.radius * instance.parameters.x + max(0, _WindAmplitude);
            float pixelDiameter = radius * abs(UNITY_MATRIX_P._m11) * viewportHeight;
            if (IsPerspectiveProjection())
            {
                float3 center = mul(instance.objectToWorld, float4(cluster.center, 1)).xyz;
                float nearest = -TransformWorldToView(center).z - radius;
                if (nearest <= _ProjectionParams.y) return false;
                pixelDiameter /= nearest;
            }
            return pixelDiameter <= _VMFarShadingMaxPixels;
        }

        #if defined(SHADER_API_D3D11) || defined(SHADER_API_D3D12)
            [earlydepthstencil]
        #endif
        half4 HornbeamResolveFragment(float4 positionCS : SV_POSITION) : SV_Target0
        {
            uint4 visibility = _VMVisibilityTexture.Load(int3(int2(positionCS.xy), 0));
            if (((visibility.z >> 7) & 0xffffffu) != _VMVisibilityBatchId)
                discard;
            // MaterialDepth owns the raster depth test. The original scene depth
            // remains available for screen-space shading without writing SV_Depth.
            float depth = asfloat(visibility.w);
            uint corner = (visibility.z & 63u) * 3;
            Varyings a = ForwardVertex(EDVMAttributesFromVertex(VMVisibilityFetchVertex(visibility.x, visibility.y, corner), visibility.y));
            Varyings b = ForwardVertex(EDVMAttributesFromVertex(VMVisibilityFetchVertex(visibility.x, visibility.y, corner + 1), visibility.y));
            Varyings c = ForwardVertex(EDVMAttributesFromVertex(VMVisibilityFetchVertex(visibility.x, visibility.y, corner + 2), visibility.y));
            float3 weights, weightsDx, weightsDy;
            if (!VMVisibilityGetBarycentrics(positionCS.xy, _VMVisibilitySize.xy, a.positionCS, b.positionCS, c.positionCS,
                weights, weightsDx, weightsDy))
                discard;

            Varyings input = (Varyings)0;
            input.positionCS = positionCS;
            input.positionCS.z = depth;
            input.positionWS = a.positionWS * weights.x + b.positionWS * weights.y + c.positionWS * weights.z;
            input.uv = a.uv * weights.x + b.uv * weights.y + c.uv * weights.z;
            input.normalWS = a.normalWS * weights.x + b.normalWS * weights.y + c.normalWS * weights.z;
            input.tangentWS = a.tangentWS * weights.x + b.tangentWS * weights.y + c.tangentWS * weights.z;
            input.fog = a.fog * weights.x + b.fog * weights.y + c.fog * weights.z;
            input.vertexLighting = a.vertexLighting * weights.x + b.vertexLighting * weights.y + c.vertexLighting * weights.z;
            input.sourceVertexColor = a.sourceVertexColor * weights.x + b.sourceVertexColor * weights.y + c.sourceVertexColor * weights.z;
            input.sourceVariation = a.sourceVariation;
            input.vmInstanceIndex = visibility.y;
            #if defined(VM_RESOLVE_DIAGNOSTICS)
                input.diagnosticPrimitive = uint3(visibility.xy, visibility.z & 63u);
                input.diagnosticBarycentrics = weights;
            #endif
            float2 uvDx = a.uv * weightsDx.x + b.uv * weightsDx.y + c.uv * weightsDx.z;
            float2 uvDy = a.uv * weightsDy.x + b.uv * weightsDy.y + c.uv * weightsDy.z;
            half front = (visibility.z & 0x80000000u) != 0 ? 1.0h : -1.0h;
            bool simplified = UseSimplifiedHornbeamShading(visibility.x, visibility.y, _VMVisibilitySize.y);
            return ShadeHornbeam(input, front, true, simplified, uvDx, uvDy);
        }

        #define EDVM_MOTION_FOLIAGE 1
        #define EDVM_MOTION_DISTANCE_OPACITY 1
        #include "ElectricDreamsVirtualMeshMotion.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMForwardVertex
            #pragma fragment ForwardFragment
            #pragma multi_compile _ VM_RESOLVE_DIAGNOSTICS
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
            // Explicit Graph opt-in after the matching selected-cluster depth
            // pass. Native ForwardLit retains its original render state.
            Name "GraphForwardLit"
            Tags { "LightMode"="VirtualMeshGraphForward" }
            ZWrite Off
            ZTest Equal
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMForwardVertex
            #pragma fragment ForwardFragment
            #pragma multi_compile _ VM_RESOLVE_DIAGNOSTICS
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
            Name "VMVisibility"
            Tags { "LightMode"="VirtualMeshVisibility" }
            AlphaToMask Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMVisibilityVertex
            #pragma fragment HornbeamVisibilityFragment
            ENDHLSL
        }
        Pass
        {
            Name "VMVisibilityNormals"
            Tags { "LightMode"="VirtualMeshVisibilityNormals" }
            AlphaToMask Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMVisibilityNormalsVertex
            #pragma fragment HornbeamVisibilityNormalsFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
        Pass
        {
            Name "VMVisibilityIndexed"
            Tags { "LightMode"="VirtualMeshVisibilityIndexed" }
            AlphaToMask Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMVisibilityIndexedVertex
            #pragma fragment HornbeamVisibilityIndexedFragment
            ENDHLSL
        }
        Pass
        {
            Name "VMVisibilityNormalsIndexed"
            Tags { "LightMode"="VirtualMeshVisibilityNormalsIndexed" }
            AlphaToMask Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMVisibilityNormalsIndexedVertex
            #pragma fragment HornbeamVisibilityNormalsIndexedFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
        Pass
        {
            Name "VMNormalResolve"
            Tags { "LightMode"="VirtualMeshNormalResolve" }
            Cull Off
            ZWrite Off
            ZTest Equal
            AlphaToMask Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex HornbeamResolveVertex
            #pragma fragment HornbeamNormalResolveFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
        Pass
        {
            Name "VMResolve"
            Tags { "LightMode"="VirtualMeshResolve" }
            ZWrite Off
            ZTest Equal
            AlphaToMask Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex HornbeamResolveVertex
            #pragma fragment HornbeamResolveFragment
            #pragma multi_compile _ VM_RESOLVE_DIAGNOSTICS
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
            Cull [_VMShadowCull]
            ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMDepthVertex
            #pragma fragment DepthFragment
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMDepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode"="MotionVectors" }
            ZWrite Off
            ZTest LEqual
            ColorMask RG
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ VM_SPLIT_VERTEX_STREAMS VM_DYNAMIC_VERTEX_STREAMS
            #pragma vertex EDVMMotionVertex
            #pragma fragment EDVMMotionFragment
            ENDHLSL
        }
    }
    Fallback Off
}
