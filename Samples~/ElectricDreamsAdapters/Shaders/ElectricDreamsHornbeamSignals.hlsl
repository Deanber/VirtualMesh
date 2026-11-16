#ifndef ELECTRIC_DREAMS_HORNBEAM_SIGNALS_INCLUDED
#define ELECTRIC_DREAMS_HORNBEAM_SIGNALS_INCLUDED

float HornbeamPow(float value, float exponent)
{
    float result = 0.0;
    if (value > 2.980233e-8)
        result = exponent == 1.0 ? value : pow(max(value,1e-8),exponent);
    return result;
}
half3 HornbeamPow3(half3 value, half exponent)
{
    return half3(HornbeamPow(value.r,exponent),HornbeamPow(value.g,exponent),HornbeamPow(value.b,exponent));
}
half3 HornbeamOverlay(half3 baseColor, half3 tint)
{
    return lerp(2.0h*baseColor*tint,1.0h-2.0h*(1.0h-baseColor)*(1.0h-tint),step(.5h,baseColor));
}
half HornbeamLeafMask(half translucency)
{
    // MF_GenerateBranchMask: this is a texture signal, not vertex COLOR.r.
    return _Plant > .5h ? 1.0h : saturate(HornbeamPow(translucency,5.0) * 25000.0);
}
half3 HornbeamAdjustedColor(half3 color, half3 controls, half4 tint, half tintEnabled)
{
    half grey = dot(color,_LuminanceWeights.xyz);
    half3 adjusted = HornbeamPow3(lerp(grey.xxx,color,controls.x)*controls.y,controls.z);
    if (tintEnabled > .5h)
        adjusted = lerp(adjusted,HornbeamOverlay(adjusted,tint.rgb),tint.a);
    return adjusted;
}
half3 HornbeamAlbedo(half3 color, half translucency)
{
    half3 adjusted = HornbeamAdjustedColor(color,_AlbedoControls.xyz,_BaseColor,_TintBaseColor);
    if (_SourceBranch < .5h && _SplitAlbedoControls > .5h)
    {
        // Split controls bypass Tint BaseColor and always use each tint alpha.
        half3 branches = HornbeamAdjustedColor(color,_BranchColorControls.xyz,_BranchTint,1.0h);
        half3 leaves = HornbeamAdjustedColor(color,_LeafColorControls.xyz,_LeafTint,1.0h);
        adjusted = lerp(branches,leaves,HornbeamLeafMask(translucency));
    }
    return adjusted;
}
half HornbeamRoughness(half4 mask, half front)
{
    half roughness = 1.0h;
    if (_SourceBranch < .5h)
    {
        half leafMask = HornbeamLeafMask(mask.b);
        if (_BasicControls > .5h)
            roughness = lerp(_BranchRoughness.z,_LeafRoughness.z,leafMask);
        else
        {
            half minimum = _AdvancedControls > .5h ? _LeafRoughnessRange.x : 0.0h;
            half maximum = _AdvancedControls > .5h ? _LeafRoughnessRange.y : 1.0h;
            half branchContrast = _AdvancedControls > .5h ? _BranchRoughness.y : 1.0h;
            half leafContrast = _AdvancedControls > .5h ? _LeafRoughness.y : 1.0h;
            half branches = lerp(minimum,maximum,HornbeamPow(mask.g*_BranchRoughness.x,branchContrast));
            half leaves = lerp(minimum,maximum,HornbeamPow(mask.g*_LeafRoughness.x,leafContrast));
            roughness = lerp(branches,leaves,leafMask);
            if (_LeafBacksideRoughness > .5h && front < 0.0h)
                roughness = _AdvancedControls > .5h ? saturate(roughness*_LeafRoughnessRange.z) : .75h;
        }
    }
    else
    {
        // Trunk Mask.R and Decoration Mask.G are distinct source graphs.
        half sourceRoughness = _SourceBranch < 1.5h ? mask.r : mask.g;
        roughness = _SurfaceConstantRoughness;
        if (_BasicControls < .5h)
        {
            half exponent = _AdvancedControls > .5h ? _SurfaceRoughnessControls.y : 1.0h;
            half minimum = _AdvancedControls > .5h ? _SurfaceRoughnessControls.z : 0.0h;
            half maximum = _AdvancedControls > .5h ? _SurfaceRoughnessControls.w : 1.0h;
            half adjusted = saturate(2.0h*HornbeamPow(sourceRoughness*_SurfaceRoughnessControls.x,exponent));
            roughness = lerp(minimum,maximum,adjusted);
        }
    }
    return roughness;
}
half3 HornbeamTranslucency(half3 adjustedAlbedo, half translucency)
{
    // MF_generateTranslucency: brightness multiplies Albedo*Mask.B before
    // desaturation, then contrast and optional Overlay Tint with tint alpha.
    half3 signal = adjustedAlbedo * (translucency*_TransmissionControls.y);
    half grey = dot(signal,_LuminanceWeights.xyz);
    signal = HornbeamPow3(lerp(grey.xxx,signal,_TransmissionControls.x),_TransmissionControls.z);
    if (_TintTranslucency > .5h)
        signal = lerp(signal,HornbeamOverlay(signal,_TranslucencyTint.rgb),_TranslucencyTint.a);
    return signal;
}
#endif
