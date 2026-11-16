#ifndef ELECTRIC_DREAMS_DECAL_RECEIVER_INCLUDED
#define ELECTRIC_DREAMS_DECAL_RECEIVER_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

void ApplyElectricDreamsDecal(float4 positionCS, inout SurfaceData surface, inout InputData lighting)
{
    #if defined(_DBUFFER)
        // The two verified source decals have no AO output. URP DBuffer shares
        // one blend weight for metal/AO/smoothness; retain receiver AO while
        // applying their authored albedo, metal and roughness outputs.
        half receiverAO = surface.occlusion;
        ApplyDecalToSurfaceData(positionCS,surface,lighting);
        surface.occlusion = receiverAO;
    #endif
}
#endif
