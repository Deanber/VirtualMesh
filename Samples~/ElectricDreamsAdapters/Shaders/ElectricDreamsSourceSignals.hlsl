#ifndef ELECTRIC_DREAMS_SOURCE_SIGNALS_INCLUDED
#define ELECTRIC_DREAMS_SOURCE_SIGNALS_INCLUDED

// Transcribed from exported UE graph connections. No resource declarations,
// renderer mutations or material instance data are hidden in these functions.
// Full float precision matters for Health's 1e9 scale and world coordinates.
float EDSourcePow(float x, float exponent)
{
    return x > 2.980233e-8 ? pow(max(x,1e-8),exponent) : 0.0;
}
float3 EDSourcePow3(float3 x,float exponent)
{
    return float3(EDSourcePow(x.x,exponent),EDSourcePow(x.y,exponent),EDSourcePow(x.z,exponent));
}
float EDSourceBranchMask(float translucency,float plant)
{
    return plant > .5 ? 1.0 : saturate(EDSourcePow(translucency,5.0)*25000.0);
}
float3 EDSourceOverlay(float3 baseColor,float3 tint)
{
    return lerp(2.0*baseColor*tint,1.0-2.0*(1.0-baseColor)*(1.0-tint),step(.5,baseColor));
}
float3 EDSourceLerp3(float3 a,float3 b,float3 c,float alpha)
{
    // Lerp_3Color, not the distinct 3ColorBlend function used by Health.
    return lerp(lerp(a,b,saturate(alpha*2.0)),c,saturate(alpha*2.0-1.0));
}
float3 EDSourceBlend3(float3 a,float3 b,float3 c,float alpha)
{
    return lerp(lerp(a,b,saturate(alpha*2.0)),lerp(b,c,saturate((alpha-.5)*2.0)),alpha);
}
float EDStablePositionRandom(float3 sourceBoundsCenterMetres)
{
    // Unity adaptation authorized for shared-material GRD. This is NOT UE's
    // FRandomStream/PerInstanceRandom sequence and is not advertised as such.
    float3 p = frac(sourceBoundsCenterMetres*.1031);
    p += dot(p,p.yzx+33.33);
    return frac((p.x+p.y)*p.z);
}
float EDSourceColorVariationAlpha(float3 sourceBoundsCenterCm,float instanceRandom,float amount)
{
    float3 f = frac(instanceRandom+sourceBoundsCenterCm*.01);
    return amount*((dot(f.xy,f.yz)-.5)*2.0);
}
float3 EDSourceColorVariation(float3 albedo,float branchMask,float alpha,float intensity,
    float3 color1,float3 color2,float3 color3)
{
    // Intensity may exceed one in source (Hornbeam uses 1.2). Do not clamp it.
    float3 varied = lerp(albedo,albedo*EDSourceLerp3(color1,color2,color3,alpha),intensity);
    return lerp(albedo,varied,branchMask);
}
struct EDSourceSeasonParameters
{
    float healthOffset,healthOverlayAmount;
    float3 healthOverlay;
    float seasonOffset,seasonOverlayAmount,seasonBrightness,seasonSaturation;
    float3 seasonOverlay;
    float perInstanceRandom;
};
float3 EDSourceHealthSeason(float3 albedo,float translucency,float4 vertexColor,float plant,
    float instanceRandom,float4 mpcHealthStrengthSaturationBrightness,EDSourceSeasonParameters p)
{
    // MF_HealthSeason, non-Impostor and Winter=false. Caller handles Seasons.
    float healthAlpha = saturate(EDSourcePow(albedo.g,p.healthOffset+mpcHealthStrengthSaturationBrightness.x)*1e9);
    float3 healthTint = lerp(float3(.117647,.003922,0),p.healthOverlay,p.healthOverlayAmount);
    float3 healthYellow = lerp(albedo,albedo+healthTint,.9);
    float3 health = EDSourceBlend3(albedo,healthYellow,healthYellow*.25,healthAlpha);
    float restore = saturate(plant > .5 ? vertexColor.g : 1.0-vertexColor.a*10.0);
    health = lerp(health,albedo,restore);
    float strength = mpcHealthStrengthSaturationBrightness.y;
    float seasonAmount = strength*(p.perInstanceRandom > .5 ? instanceRandom*.1+.1 : 1.0);
    float3 tint = lerp(float3(.223228,.074214,0),p.seasonOverlay,p.seasonOverlayAmount);
    float3 season = (health+tint)*(p.seasonBrightness*mpcHealthStrengthSaturationBrightness.w);
    float grey = dot(season,float3(.212639,.7151686549186707,.07219231873750687));
    season = lerp(grey.xxx,season,p.seasonSaturation*mpcHealthStrengthSaturationBrightness.z);
    season = lerp(health,season,saturate(p.seasonOffset+seasonAmount));
    float mask = saturate((plant > .5 ? 1.0-vertexColor.g : vertexColor.a*vertexColor.a)*strength);
    // Source Lerp_1.A is health, NOT original albedo. Health survives strength=0.
    season = lerp(health,season,mask);
    return lerp(albedo,season,EDSourceBranchMask(translucency,plant));
}
void EDSourceLightInfluence(inout float3 albedo,inout float3 transmission,float branchMask,
    float vertexAlpha,float basicControls,float advancedControls,float baseStrength,
    float transmissionStrength,float3 baseTint,float3 transmissionTint)
{
    // Source High/default material quality; non-Plant, Winter=false.
    float mask = vertexAlpha*branchMask;
    float3 tint = advancedControls > .5 ? baseTint : float3(1,1,1);
    albedo = lerp(albedo,lerp(albedo,EDSourceOverlay(albedo,tint),mask),basicControls > .5 ? 3.0 : baseStrength);
    if (advancedControls > .5)
        transmission = lerp(transmission,lerp(transmission,EDSourceOverlay(transmission,transmissionTint),mask),transmissionStrength);
}

float EDSourceCheapContrast(float value,float contrast)
{
    return saturate(lerp(-contrast,1.0+contrast,value));
}
float EDSourceHeightLerp(float a,float b,float transition,float height,float contrast)
{
    return lerp(a,b,EDSourceCheapContrast(saturate(height-1.0+transition*2.0),contrast));
}
float3 EDSourceWorldAlignedBlend(float3 xzSample,float3 yzSample,float3 xySample,
    float3 sourceVertexNormalWS,float contrast)
{
    // UE WorldAlignedTexture uses nested CheapContrast blends, not normalized
    // power weights. Sample UVs: sourcePosition / -abs(textureSize), then xz/yz/xy.
    return lerp(lerp(xzSample,yzSample,EDSourceCheapContrast(abs(sourceVertexNormalWS.x),contrast)),
        xySample,EDSourceCheapContrast(abs(sourceVertexNormalWS.z),contrast));
}
float3 EDSourceFuzzy(float3 albedo,float viewDotNormal,float coreDarkness,float power,float edgeBrightness)
{
    float facing = saturate(viewDotNormal);
    return albedo*(1.0-facing*coreDarkness+EDSourcePow(1.0-facing,power)*edgeBrightness);
}
float3 EDSourceLichen(float3 albedo,float worldAlignedMaskB,float hardcodedBoulderDpRF_B,
    float intensity,float contrast)
{
    float alpha = saturate(EDSourcePow(worldAlignedMaskB,contrast)*intensity)*(1.0-hardcodedBoulderDpRF_B);
    return lerp(albedo,float3(.424485,.442708,.362006),alpha);
}
struct EDSourceGrowthParameters
{
    float2 oxidationDF;           // Contrast, Intensity
    float2 oxidationDisplacement;// Contrast, Intensity
    float2 oxidationAlbedo;       // Contrast, Intensity
    float2 oxidationGlobal;       // Contrast, Intensity
    float2 oxidationMask;         // Contrast, Intensity
    float2 oxidationCrevice;      // Contrast, Intensity
    float2 mossDF,mossDisplacement,mossAlbedo,mossGlobal,mossMask,mossCrevice;
};
float2 EDSourceGrowthWeights(float distanceFieldApproxAO,float displacement,float albedoR,
    float3 worldAlignedMask,EDSourceGrowthParameters p)
{
    // Caller MUST supply a real/reconstructed source contact field. Do not
    // silently pass texture AO or screen-space AO; those are different inputs.
    float crevice = 1.0-distanceFieldApproxAO;
    float oxHeight = saturate(saturate(EDSourcePow(1.0-displacement,p.oxidationDisplacement.x))*p.oxidationDisplacement.y)
        * EDSourcePow(albedoR,p.oxidationAlbedo.x)*p.oxidationAlbedo.y;
    float oxBase = saturate(EDSourceCheapContrast(crevice,p.oxidationDF.x)*p.oxidationDF.y);
    float oxGlobal = saturate(EDSourcePow(EDSourceHeightLerp(oxBase,0.0,.5,saturate(oxHeight),1.0),p.oxidationGlobal.x)*p.oxidationGlobal.y);
    float oxidation = saturate((EDSourcePow(worldAlignedMask.r,p.oxidationMask.x)*p.oxidationMask.y
        + saturate(EDSourcePow(crevice,p.oxidationCrevice.x)*p.oxidationCrevice.y))*oxGlobal);
    float mossHeight = saturate(saturate(EDSourcePow(1.0-displacement,p.mossDisplacement.x))*p.mossDisplacement.y)
        * EDSourcePow(albedoR,p.mossAlbedo.x)*p.mossAlbedo.y;
    float mossBase = saturate(EDSourceCheapContrast(crevice,p.mossDF.x)*p.mossDF.y);
    float mossGlobal = saturate(EDSourcePow(EDSourceHeightLerp(mossBase,0.0,.2,saturate(mossHeight),1.0),p.mossGlobal.x)*p.mossGlobal.y);
    float moss = saturate((saturate(EDSourcePow(worldAlignedMask.g,p.mossMask.x)*p.mossMask.y)
        + saturate(EDSourcePow(crevice,p.mossCrevice.x)*p.mossCrevice.y))*mossGlobal);
    return float2(oxidation,moss);
}
float3 EDSourceGrowthColor(float3 sampleColor,float3 tint,float3 saturationBrightnessContrast)
{
    float grey = dot(sampleColor,float3(.212639,.7151686549186707,.07219231873750687));
    return EDSourcePow3(lerp(grey.xxx,sampleColor,saturationBrightnessContrast.x)
        * saturationBrightnessContrast.y*tint,saturationBrightnessContrast.z);
}
float EDSourceGrowthRoughness(float sampleG,float minimum,float maximum,float contrast,float intensity)
{
    return lerp(minimum,maximum,saturate(EDSourcePow(sampleG,contrast)*intensity));
}
// MF_WaterEffect outputs in source order:
// color = lerp(lerp(lichenColor,oxidationColor,weights.x),
//              EDSourceFuzzy(mossColor,mossViewDot,.3,.5,1),weights.y)
// rough = lerp(lerp(baseRough,oxidationRough,weights.x),mossRough,weights.y)
// normalTS = lerp(baseNormalTS,lerp(float3(0,0,1),mossNormalTS,mossNormalIntensity),weights.y)
// Parent's optional FuzzyShading is a SECOND operation, blended with own DR.B.
#endif
