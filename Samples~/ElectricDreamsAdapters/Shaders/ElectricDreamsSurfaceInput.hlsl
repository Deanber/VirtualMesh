#ifndef ELECTRIC_DREAMS_SOURCE_SURFACE_INPUT_INCLUDED
#define ELECTRIC_DREAMS_SOURCE_SURFACE_INPUT_INCLUDED
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

#include "ElectricDreamsSourceSignals.hlsl"
TEXTURE2D(_SourceGrowthMask);
TEXTURE2D(_SourceLichenFuzzMap);
TEXTURE2D(_BaseMap);
TEXTURE2D(_BaseNormalMap); 
TEXTURE2D(_BaseDataMap); 
TEXTURE2D(_Layer1Map); 
TEXTURE2D(_Layer1NormalMap); 
TEXTURE2D(_Layer1DataMap); 
TEXTURE2D(_Layer2Map); 
TEXTURE2D(_Layer2NormalMap); 
TEXTURE2D(_Layer2DataMap); 
TEXTURE2D(_Layer3Map); 
TEXTURE2D(_Layer3NormalMap); 
TEXTURE2D(_Layer3DataMap); 
TEXTURE2D(_LayerMaskMap); 
TEXTURE2D(_OcclusionMap); 
TEXTURE2D(_UniqueNormalMap); 

CBUFFER_START(UnityPerMaterial)
    float4 _Layer3Map_ST;
    half4 _Layer3Color, _Layer3ColorControls, _Layer3RoughnessChannel, _Layer3RoughnessControls;
    half4 _Layer3F0, _Layer3DataOcclusionChannel, _Layer3MaskChannel, _Layer3BlendControls;
    half _Layer3NormalScale, _Layer3Enabled;
    float4 _BaseMap_ST, _Layer1Map_ST, _Layer2Map_ST, _LayerMaskMap_ST, _OcclusionMap_ST;
    half4 _BaseColor, _Layer1Color, _Layer2Color;
    half4 _BaseColorControls, _Layer1ColorControls, _Layer2ColorControls, _LuminanceWeights;
    half4 _BaseRoughnessChannel, _Layer1RoughnessChannel, _Layer2RoughnessChannel;
    half4 _BaseRoughnessControls, _Layer1RoughnessControls, _Layer2RoughnessControls;
    half4 _BaseF0, _Layer1F0, _Layer2F0;
    half4 _Layer1MaskChannel, _Layer2MaskChannel, _OcclusionChannel;
    half4 _BaseDataOcclusionChannel, _Layer1DataOcclusionChannel, _Layer2DataOcclusionChannel;
    half4 _Layer1BlendControls, _Layer2BlendControls;
    half _BaseNormalScale, _Layer1NormalScale, _Layer2NormalScale;
    half _Layer1Enabled, _Layer2Enabled, _OcclusionEnabled, _Cull;
    half _SourceLayerMode, _SourceMaskMode, _UniqueNormalEnabled;
    half _BaseNormalMode, _SourceUvTransform, _BaseOcclusionStrength;
    float4 _SourceLichenControls, _SourceFuzzyControls, _SourceWorldOriginCm;
    float _SourceLichen, _SourceFuzzy;
CBUFFER_END

// GRD uses shared defaults. Keep binding, mode and UV selection material-wide.
#ifdef UNITY_DOTS_INSTANCING_ENABLED
UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _Layer1Color)
    UNITY_DOTS_INSTANCED_PROP(float4, _Layer2Color)
    UNITY_DOTS_INSTANCED_PROP(float4, _Layer3Color)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseRoughnessControls)
    UNITY_DOTS_INSTANCED_PROP(float4, _Layer1RoughnessControls)
    UNITY_DOTS_INSTANCED_PROP(float4, _Layer2RoughnessControls)
    UNITY_DOTS_INSTANCED_PROP(float4, _Layer3RoughnessControls)
    UNITY_DOTS_INSTANCED_PROP(float, _BaseNormalScale)
    UNITY_DOTS_INSTANCED_PROP(float, _Layer1NormalScale)
    UNITY_DOTS_INSTANCED_PROP(float, _Layer2NormalScale)
    UNITY_DOTS_INSTANCED_PROP(float, _Layer3NormalScale)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
static float4 edSurface_BaseColor, edSurface_Layer1Color, edSurface_Layer2Color;
static float4 edSurface_BaseRoughnessControls, edSurface_Layer1RoughnessControls, edSurface_Layer2RoughnessControls;
static float edSurface_BaseNormalScale, edSurface_Layer1NormalScale, edSurface_Layer2NormalScale;
static float4 edSurface_Layer3Color;
static float4 edSurface_Layer3RoughnessControls;
static float edSurface_Layer3NormalScale;
void SetupElectricDreamsSurfaceCaches()



{
    edSurface_BaseColor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    edSurface_Layer1Color = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Layer1Color);
    edSurface_Layer2Color = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Layer2Color);
    edSurface_Layer3Color = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Layer3Color);
    edSurface_BaseRoughnessControls = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseRoughnessControls);
    edSurface_Layer1RoughnessControls = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Layer1RoughnessControls);
    edSurface_Layer2RoughnessControls = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Layer2RoughnessControls);
    edSurface_Layer3RoughnessControls = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Layer3RoughnessControls);
    edSurface_BaseNormalScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _BaseNormalScale);
    edSurface_Layer1NormalScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Layer1NormalScale);
    edSurface_Layer2NormalScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Layer2NormalScale);
    edSurface_Layer3NormalScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Layer3NormalScale);
}
#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupElectricDreamsSurfaceCaches()
#define _BaseColor edSurface_BaseColor
#define _Layer1Color edSurface_Layer1Color
#define _Layer2Color edSurface_Layer2Color
#define _Layer3Color edSurface_Layer3Color
#define _BaseRoughnessControls edSurface_BaseRoughnessControls
#define _Layer1RoughnessControls edSurface_Layer1RoughnessControls
#define _Layer2RoughnessControls edSurface_Layer2RoughnessControls
#define _Layer3RoughnessControls edSurface_Layer3RoughnessControls
#define _BaseNormalScale edSurface_BaseNormalScale
#define _Layer1NormalScale edSurface_Layer1NormalScale
#define _Layer2NormalScale edSurface_Layer2NormalScale
#define _Layer3NormalScale edSurface_Layer3NormalScale
#endif

float SourceSurfacePositivePow(float value, float exponent)
{
    // UE 5.8 Common.ush PositiveClampedPow: preserve zero for tiny/negative
    // inputs and keep the authored exponent, including zero.
    float result = 0.0;
    if (value > 2.980233e-8)
        result = exponent == 1.0 ? value : pow(max(value,1e-8),exponent);
    return result;
}
half SourceSurfaceRoughness(half value, half4 controls)
{
    // MF_Roughness: Power -> Multiply -> Saturate -> Lerp(Min, Max).
    // Source texture channels are explicit bindings; never infer them from DpRF.
    half powered = SourceSurfacePositivePow(value,controls.x);
    return lerp(controls.z,controls.w,saturate(powered * controls.y));
}
half3 SourceSurfaceColor(half3 value, half4 controls, half3 tint)
{
    if (_SourceLayerMode > .5h)
    {
        // MLA_Master Blend_Overlay(BaseColor,Tint); neutral Tint is .5.
        return lerp(2.0h * value * tint,1.0h - 2.0h * (1.0h-value) * (1.0h-tint),step(.5h,value));
    }
    half grey = dot(value,_LuminanceWeights.xyz);
    value = max(lerp(grey.xxx,value,controls.x) * controls.y * tint,0.0h);
    return half3(SourceSurfacePositivePow(value.r,controls.z),
        SourceSurfacePositivePow(value.g,controls.z),SourceSurfacePositivePow(value.b,controls.z));
}
half3 SourceFlattenNormal(half3 normalTS, half intensity)
{
    if (_SourceLayerMode > .5h)
        return SafeNormalize(normalTS * half3(1,1,intensity));
    if (_BaseNormalMode > .5h)
        return normalTS * half3(intensity,intensity,1);
    // FlattenNormal(N,1-Intensity), including authored intensity above 1.
    // Normalize only after layer blending and tangent-to-world conversion.
    return lerp(half3(0,0,1),normalTS,intensity);
}
float2 SourceMapUV(float2 uv, float4 tilingOffset)
{
    if (_SourceUvTransform > .5h || _SourceLayerMode > .5h)
    {
        // Source UV -> source tiling/offset -> FBX/Unity UV convention.
        float2 sourceUV = float2(uv.x,1.0-uv.y) * tilingOffset.xy + tilingOffset.zw;
        return float2(sourceUV.x,1.0-sourceUV.y);
    }
    return uv * tilingOffset.xy + tilingOffset.zw;
}
half3 SourceAngleCorrectedNormals(half3 baseNormal, half3 additionalNormal)
{
    half3 a = baseNormal + half3(0,0,1);
    half3 b = additionalNormal * half3(-1,-1,1);
    return SafeNormalize(a * dot(a,b) - b * a.z);
}
float SourceBlendThroughInput(float height, float blend)
{
    // MF_Blend_Through_Input. Resolve the source's singular endpoint divides
    // by their one-sided limits; interior values use the original equation.
    float h = saturate(height), b = saturate(blend);
    float result = 0.0;
    if (b <= 0.0) result = h >= 1.0 ? 1.0 : 0.0;
    else if (b >= 1.0) result = h > 0.0 ? 1.0 : 0.0;
    else result = saturate(lerp((h-1.0+2.0*b)/(2.0*b),h/(2.0*(1.0-b)),b));
    return result;
}
half SourceMlbaWeight(half mask, half height, half4 controls)
{
    float blend = sqrt(max(0.0,(float)mask * (mask + controls.x)));
    float modulated = lerp(saturate(blend),SourceBlendThroughInput(height,blend),controls.y);
    return saturate(lerp(-controls.z,1.0+controls.z,modulated));
}
half DataOcclusion(half4 data, half4 channel)
{
    half value = 1.0h;
    if (dot(abs(channel),half4(1,1,1,1)) > 0.0h) value = saturate(dot(data,channel));
    return value;
}
half3 ReadSurfaceWeights(float2 uv, float2 materialUV)
{
    half3 weights = half3(0,0,0);
    if (_Layer1Enabled > .5h || _Layer2Enabled > .5h || _Layer3Enabled > .5h)
    {
        half4 mask = SAMPLE_TEXTURE2D(_LayerMaskMap,sampler_TrilinearRepeat,SourceMapUV(uv,_LayerMaskMap_ST));
        if (_Layer1Enabled > .5h) weights.x = saturate(dot(mask,_Layer1MaskChannel));
        if (_Layer2Enabled > .5h) weights.y = saturate(dot(mask,_Layer2MaskChannel));
        if (_Layer3Enabled > .5h) weights.z = saturate(dot(mask,_Layer3MaskChannel));
        if (_SourceMaskMode > .5h)
        {
            if (_Layer1Enabled > .5h)
            {
                half height = SAMPLE_TEXTURE2D(_Layer1DataMap,sampler_TrilinearRepeat,SourceMapUV(materialUV,_Layer1Map_ST)).b;
                weights.x = SourceMlbaWeight(weights.x,height,_Layer1BlendControls);
            }
            if (_Layer2Enabled > .5h)
            {
                half height = SAMPLE_TEXTURE2D(_Layer2DataMap,sampler_TrilinearRepeat,SourceMapUV(materialUV,_Layer2Map_ST)).b;
                weights.y = SourceMlbaWeight(weights.y,height,_Layer2BlendControls);
            }
            if (_Layer3Enabled > .5h)
            {
                half height = SAMPLE_TEXTURE2D(_Layer3DataMap,sampler_TrilinearRepeat,SourceMapUV(materialUV,_Layer3Map_ST)).b;
                weights.z = SourceMlbaWeight(weights.z,height,_Layer3BlendControls);
            }
        }
    }
    return weights;
}
half3 ReadSurfaceNormalTS(float2 uv, half3 weights)
{
    half3 normal = SourceFlattenNormal(UnpackNormal(SAMPLE_TEXTURE2D(_BaseNormalMap,sampler_TrilinearRepeat,
        SourceMapUV(uv,_BaseMap_ST))),_BaseNormalScale);
    // Branch on material-wide enables, not per-pixel weights: derivatives stay
    // valid at mask edges. Disabled layers issue no texture fetches.
    if (_Layer1Enabled > .5h)
    {
        half3 layer = SourceFlattenNormal(UnpackNormal(SAMPLE_TEXTURE2D(_Layer1NormalMap,sampler_TrilinearRepeat,
            SourceMapUV(uv,_Layer1Map_ST))),_Layer1NormalScale);
        normal = lerp(normal,layer,weights.x);
    }
    if (_Layer2Enabled > .5h)
    {
        half3 layer = SourceFlattenNormal(UnpackNormal(SAMPLE_TEXTURE2D(_Layer2NormalMap,sampler_TrilinearRepeat,
            SourceMapUV(uv,_Layer2Map_ST))),_Layer2NormalScale);
        normal = lerp(normal,layer,weights.y);
    }
    if (_Layer3Enabled > .5h)
    {
        half3 layer = SourceFlattenNormal(UnpackNormal(SAMPLE_TEXTURE2D(_Layer3NormalMap,sampler_TrilinearRepeat,
            SourceMapUV(uv,_Layer3Map_ST))),_Layer3NormalScale);
        normal = lerp(normal,layer,weights.z);
    }
    return normal;
}
SurfaceData ReadSurfaceData(float2 uv, float2 materialUV, half3 weights)
{
    SurfaceData surface = (SurfaceData)0;
    float2 baseUV = SourceMapUV(materialUV,_BaseMap_ST);
    surface.albedo = SourceSurfaceColor(SAMPLE_TEXTURE2D(_BaseMap,sampler_TrilinearRepeat,baseUV).rgb,
        _BaseColorControls,_BaseColor.rgb);
    half4 baseData = SAMPLE_TEXTURE2D(_BaseDataMap,sampler_TrilinearRepeat,baseUV);
    half roughness = SourceSurfaceRoughness(dot(baseData,_BaseRoughnessChannel),_BaseRoughnessControls);
    surface.occlusion = DataOcclusion(baseData,_BaseDataOcclusionChannel);
    if (dot(abs(_BaseDataOcclusionChannel),half4(1,1,1,1)) > 0.0h)
        surface.occlusion *= _BaseOcclusionStrength;
    surface.specular = _BaseF0.rgb;
    if (_Layer1Enabled > .5h)
    {
        float2 layerUV = SourceMapUV(materialUV,_Layer1Map_ST);
        half3 albedo = SourceSurfaceColor(SAMPLE_TEXTURE2D(_Layer1Map,sampler_TrilinearRepeat,layerUV).rgb,
            _Layer1ColorControls,_Layer1Color.rgb);
        half4 layerData = SAMPLE_TEXTURE2D(_Layer1DataMap,sampler_TrilinearRepeat,layerUV);
        half layerRoughness = SourceSurfaceRoughness(dot(layerData,_Layer1RoughnessChannel),_Layer1RoughnessControls);
        surface.albedo = lerp(surface.albedo,albedo,weights.x);
        roughness = lerp(roughness,layerRoughness,weights.x);
        surface.specular = lerp(surface.specular,_Layer1F0.rgb,weights.x);
        surface.occlusion = lerp(surface.occlusion,DataOcclusion(layerData,_Layer1DataOcclusionChannel),weights.x);
    }
    if (_Layer2Enabled > .5h)
    {
        float2 layerUV = SourceMapUV(materialUV,_Layer2Map_ST);
        half3 albedo = SourceSurfaceColor(SAMPLE_TEXTURE2D(_Layer2Map,sampler_TrilinearRepeat,layerUV).rgb,
            _Layer2ColorControls,_Layer2Color.rgb);
        half4 layerData = SAMPLE_TEXTURE2D(_Layer2DataMap,sampler_TrilinearRepeat,layerUV);
        half layerRoughness = SourceSurfaceRoughness(dot(layerData,_Layer2RoughnessChannel),_Layer2RoughnessControls);
        surface.albedo = lerp(surface.albedo,albedo,weights.y);
        roughness = lerp(roughness,layerRoughness,weights.y);
        surface.specular = lerp(surface.specular,_Layer2F0.rgb,weights.y);
        surface.occlusion = lerp(surface.occlusion,DataOcclusion(layerData,_Layer2DataOcclusionChannel),weights.y);
    }
    if (_Layer3Enabled > .5h)
    {
        float2 layerUV = SourceMapUV(materialUV,_Layer3Map_ST);
        half3 albedo = SourceSurfaceColor(SAMPLE_TEXTURE2D(_Layer3Map,sampler_TrilinearRepeat,layerUV).rgb,
            _Layer3ColorControls,_Layer3Color.rgb);
        half4 layerData = SAMPLE_TEXTURE2D(_Layer3DataMap,sampler_TrilinearRepeat,layerUV);
        half layerRoughness = SourceSurfaceRoughness(dot(layerData,_Layer3RoughnessChannel),_Layer3RoughnessControls);
        surface.albedo = lerp(surface.albedo,albedo,weights.z);
        roughness = lerp(roughness,layerRoughness,weights.z);
        surface.specular = lerp(surface.specular,_Layer3F0.rgb,weights.z);
        surface.occlusion = lerp(surface.occlusion,DataOcclusion(layerData,_Layer3DataOcclusionChannel),weights.z);
    }
    surface.smoothness = 1.0h - saturate(roughness);
    if (_OcclusionEnabled > .5h)
        surface.occlusion *= saturate(dot(SAMPLE_TEXTURE2D(_OcclusionMap,sampler_TrilinearRepeat,
            SourceMapUV(uv,_OcclusionMap_ST)),_OcclusionChannel));
    surface.alpha = 1.0h;
    return surface;
}
#endif
