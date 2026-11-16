Shader "Hidden/VirtualMesh/GraphDepthCopy"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        Texture2D<float> _VMDepthSource;
        Texture2D<uint4> _VMVisibilityTexture;
        struct Varyings { float4 positionCS:SV_POSITION; };
        Varyings Vert(uint id:SV_VertexID)
        { Varyings o; o.positionCS=GetFullScreenTriangleVertexPosition(id); return o; }
        float CopyDepth(Varyings i):SV_Depth { return _VMDepthSource.Load(int3(int2(i.positionCS.xy),0)); }
        float CopyColor(Varyings i):SV_Target { return _VMDepthSource.Load(int3(int2(i.positionCS.xy),0)); }
        float CopyMaterialDepth(Varyings i):SV_Depth
        {
            int3 pixel = int3(int2(i.positionCS.xy), 0);
            uint4 visibility = _VMVisibilityTexture.Load(pixel);
            uint batch = (visibility.z >> 7) & 0xffffffu;
            float sceneDepth = _VMDepthSource.Load(pixel);
            // Zero is the sentinel. Match VMVisibilityMaterialDepth exactly.
            // Do not accept stale IDs occluded by a later non-resolve draw.
            return batch > 0 && batch <= 64 && asfloat(visibility.w) == sceneDepth
                ? batch * (1.0 / 128.0) : 0.0;
        }
        ENDHLSL
        Pass
        {
            Name "Depth" ZTest Always ZWrite On Cull Off ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment CopyDepth
            ENDHLSL
        }
        Pass
        {
            Name "Color" ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment CopyColor
            ENDHLSL
        }
        Pass
        {
            Name "MaterialDepth" ZTest Always ZWrite On Cull Off ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment CopyMaterialDepth
            ENDHLSL
        }
    }
}
