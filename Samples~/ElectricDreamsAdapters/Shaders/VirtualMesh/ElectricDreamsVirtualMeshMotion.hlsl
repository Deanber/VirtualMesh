#ifndef ELECTRIC_DREAMS_VIRTUAL_MESH_MOTION_INCLUDED
#define ELECTRIC_DREAMS_VIRTUAL_MESH_MOTION_INCLUDED

// Include after the original material functions and EDVMReadAttributes. The
// foliage branch deliberately reuses its exact depth displacement/alpha code.
#if EDVM_MOTION_DISTANCE_OPACITY
struct EDVMMotionVaryings
{
    float4 positionCS : SV_POSITION;
    float4 currentCS : TEXCOORD0;
    float4 previousCS : TEXCOORD1;
    float2 uv : TEXCOORD2;
    nointerpolation uint vmInstanceIndex : TEXCOORD3;
    nointerpolation half sourceDistanceOpacity : TEXCOORD4;
};
#else
#define EDVMMotionVaryings VMMotionVaryings
#endif

float3 EDVMMotionWorldPosition(Attributes input)
{
    #if EDVM_MOTION_FOLIAGE
        return Displaced(input);
    #else
        return EDVMTransformObjectToWorld(input.positionOS.xyz);
    #endif
}

EDVMMotionVaryings EDVMMotionVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    Attributes input = EDVMReadAttributes(vertexID, instanceID);
    EDVMMotionVaryings output = (EDVMMotionVaryings)0;
    output.vmInstanceIndex = input.vmInstanceIndex;
    float3 currentWS = EDVMMotionWorldPosition(input);
    float3 previousWS = currentWS;
    if (_VMGraphDraw != 0 && _VMHasPreviousFrame != 0)
    {
        EDVMSetupPreviousInstance(input.vmInstanceIndex);
        previousWS = EDVMMotionWorldPosition(input);
        EDVMSetupInstance(input.vmInstanceIndex);
    }
    #if EDVM_MOTION_DISTANCE_OPACITY
        // Current instance/camera, after restoring the previous-position context.
        output.sourceDistanceOpacity = SourceDistanceOpacity();
    #endif
    // Match the depth pass's CURRENT wind and jittered rasterization exactly.
    output.positionCS = TransformWorldToHClip(currentWS);
    output.currentCS = mul(_VMCurrentViewProjection, float4(currentWS, 1));
    output.previousCS = mul(_VMPreviousViewProjection, float4(previousWS, 1));
    #if EDVM_MOTION_FOLIAGE
        output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    #endif
    return output;
}

float4 EDVMMotionFragment(EDVMMotionVaryings input) : SV_Target
{
    // The legacy backend has no VM transform/time history. Preserve URP's
    // camera-motion result there instead of overwriting it with fabricated zero.
    if (_VMGraphDraw == 0) clip(-1);
    EDVMSetupInstance(input.vmInstanceIndex);
    half coverage = 1.0h;
    #if EDVM_MOTION_DISTANCE_OPACITY
        coverage = DepthCoverage(input.uv, input.sourceDistanceOpacity);
    #elif EDVM_MOTION_FOLIAGE
        coverage = DepthCoverage(input.uv);
    #endif
    return float4(VMEncodeMotion(input.currentCS, input.previousCS), 0, coverage);
}

#endif
