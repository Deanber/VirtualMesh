#ifndef VIRTUAL_MESH_MODEL_LIGHTING_INCLUDED
#define VIRTUAL_MESH_MODEL_LIGHTING_INCLUDED

// Include before Lighting/LitForwardPass. Runtime receive-shadows is a per-batch
// uniform, not a shader keyword that may be absent from a built Player's variants.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
float _VMReceiveShadows;

Light VMApplyReceiveShadows(Light light)
{
    if (_VMReceiveShadows < 0.5) light.shadowAttenuation = 1.0;
    return light;
}
Light VMGetMainLight() { return VMApplyReceiveShadows(GetMainLight()); }
Light VMGetMainLight(float4 shadowCoord) { return VMApplyReceiveShadows(GetMainLight(shadowCoord)); }
Light VMGetMainLight(float4 shadowCoord, float3 positionWS, half4 shadowMask)
{
    return VMApplyReceiveShadows(GetMainLight(shadowCoord, positionWS, shadowMask));
}
Light VMGetMainLight(InputData inputData, half4 shadowMask, AmbientOcclusionFactor aoFactor)
{
    return VMApplyReceiveShadows(GetMainLight(inputData, shadowMask, aoFactor));
}
Light VMGetAdditionalLight(uint index, float3 positionWS)
{
    return VMApplyReceiveShadows(GetAdditionalLight(index, positionWS));
}
Light VMGetAdditionalLight(uint index, float3 positionWS, half4 shadowMask)
{
    return VMApplyReceiveShadows(GetAdditionalLight(index, positionWS, shadowMask));
}
Light VMGetAdditionalLight(uint index, InputData inputData, half4 shadowMask, AmbientOcclusionFactor aoFactor)
{
    return VMApplyReceiveShadows(GetAdditionalLight(index, inputData, shadowMask, aoFactor));
}

// Cookie colors, distance attenuation, AO, light layers, BRDF and transmission
// remain the original URP/material code; only the received-shadow term changes.
#define GetMainLight VMGetMainLight
#define GetAdditionalLight VMGetAdditionalLight
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#endif
