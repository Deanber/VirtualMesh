#ifndef VIRTUAL_MESH_MODEL_MOTION_INCLUDED
#define VIRTUAL_MESH_MODEL_MOTION_INCLUDED

// These are camera-specific GPU non-jittered view-projection matrices. The
// current jittered rasterization position still comes from TransformWorldToHClip.
StructuredBuffer<float4x4> _VMPreviousObjectToWorld;
float4x4 _VMCurrentViewProjection;
float4x4 _VMPreviousViewProjection;
float _VMCurrentTime;
float _VMPreviousTime;
int _VMHasPreviousFrame;

struct VMMotionVaryings
{
    float4 positionCS : SV_POSITION;
    float4 currentCS : TEXCOORD0;
    float4 previousCS : TEXCOORD1;
    float2 uv : TEXCOORD2;
    nointerpolation uint vmInstanceIndex : TEXCOORD3;
};

float2 VMEncodeMotion(float4 currentCS, float4 previousCS)
{
    // A new camera/history reset supplies no reprojection. Do not call URP's
    // object-motion helper: its unity_MotionVectorsParams flag belongs to Unity's
    // ordinary renderer and can forcibly zero a procedural draw's valid motion.
    if (_VMHasPreviousFrame == 0 || currentCS.w <= 1e-6 || previousCS.w <= 1e-6) return 0;
    float2 velocity = currentCS.xy / currentCS.w - previousCS.xy / previousCS.w;
    #if UNITY_UV_STARTS_AT_TOP
        velocity.y = -velocity.y;
    #endif
    return velocity * 0.5;
}

#endif
