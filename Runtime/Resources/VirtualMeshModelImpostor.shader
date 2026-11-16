Shader "Virtual Mesh/Model Impostor"
{
    Properties { [HideInInspector] _VMReceiveShadows("Receive Shadows", Float) = 1 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Blend Off
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "VirtualMeshModelLighting.hlsl"
        #include "VirtualMeshModelTypes.hlsl"
        #include "VirtualMeshModelMotion.hlsl"
        #include "VirtualMeshModelImpostorReconstruction.hlsl"
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
        #include "UnityIndirect.cginc"
        StructuredBuffer<VMInstance> _VMInstances;
        StructuredBuffer<uint> _VMImpostorVisible;
        int _VMGraphDraw, _VMProxyOrthographic;
        float4 _VMProxyObserverPosition, _VMProxyObserverDirection;
        float3 _LightPosition, _LightDirection;

        struct VMProxyVaryings
        {
            float4 positionCS : SV_POSITION;
            float3 planePositionWS : TEXCOORD0;
            nointerpolation uint instanceId : TEXCOORD1;
            nointerpolation uint viewIndex : TEXCOORD2;
            nointerpolation uint valid : TEXCOORD3;
            nointerpolation uint insideLight : TEXCOORD4;
        };
        void VMProxyObserver(float3 centerWS, bool shadow, out float3 observer, out bool orthographic)
        {
            orthographic = _VMProxyOrthographic != 0;
            observer = orthographic ? _VMProxyObserverDirection.xyz : _VMProxyObserverPosition.xyz - centerWS;
            if (shadow)
            {
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    orthographic = false; observer = _LightPosition - centerWS;
                #else
                    orthographic = true; observer = _LightDirection;
                #endif
            }
        }
        VMProxyVaryings VMProxyVertexCommon(uint vertexID, uint instanceID, bool shadow)
        {
            if (_VMGraphDraw == 0) { InitIndirectDrawArgs(0); instanceID = GetIndirectInstanceID(instanceID); vertexID = GetIndirectVertexID(vertexID); }
            VMProxyVaryings output = (VMProxyVaryings)0;
            output.instanceId = _VMImpostorVisible[instanceID];
            VMInstance instance = _VMInstances[output.instanceId];
            float3 center = mul(instance.objectToWorld, float4(_VMImpostorBoundsCenter.xyz, 1)).xyz;
            float radius = max(1e-5, length(_VMImpostorBoundsExtents.xyz) * instance.parameters.x);
            float3 observer; bool orthographic; VMProxyObserver(center, shadow, observer, orthographic);
            float distanceToObserver = length(observer);
            output.valid = orthographic || distanceToObserver > radius * 1.001;
            const float2 corners[6] = { float2(-1,-1),float2(-1,1),float2(1,1),float2(-1,-1),float2(1,1),float2(1,-1) };
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                if (shadow && !output.valid)
                {
                    // A light inside the bound sees multiple hemispheres. A center-plane
                    // billboard cannot cover its cube/spot viewport. Generate one full light
                    // viewport quad and choose each ray's external capture independently.
                    output.positionCS = float4(corners[vertexID % 6], .5, 1);
                    float4 rayPlane = mul(Inverse(UNITY_MATRIX_VP),output.positionCS);
                    output.planePositionWS = rayPlane.xyz / rayPlane.w;
                    output.valid = all(isfinite(rayPlane)) && abs(rayPlane.w) > 1e-8;
                    output.insideLight = 1; return output;
                }
            #endif
            observer = distanceToObserver > 1e-8 ? observer / distanceToObserver : float3(0,0,1);
            output.viewIndex = VMImpostorSelectView(mul((float3x3)instance.worldToObject, observer));
            float3 upReference = abs(observer.y) > .98 ? float3(0,0,1) : float3(0,1,0);
            float3 right = normalize(cross(upReference, observer)); float3 up = cross(observer, right);
            // The camera-facing rectangle encloses the sphere's perspective silhouette. It
            // supplies rays only; its plane is never published as surface depth.
            float extent = radius * (orthographic ? 1 : distanceToObserver / sqrt(max(radius * radius * .001,
                distanceToObserver * distanceToObserver - radius * radius)));
            output.planePositionWS = center + (right * corners[vertexID % 6].x + up * corners[vertexID % 6].y) * extent;
            output.positionCS = TransformWorldToHClip(output.planePositionWS);
            // Do not clip a proxy because its ray-generating center plane lies outside a
            // shadow cascade. Each fragment tests the reconstructed surface's real device depth.
            output.positionCS.z = output.positionCS.w * .5;
            return output;
        }
        VMProxyVaryings VMProxyVertex(uint vertexID:SV_VertexID,uint instanceID:SV_InstanceID)
        { return VMProxyVertexCommon(vertexID,instanceID,false); }
        VMProxyVaryings VMProxyShadowVertex(uint vertexID:SV_VertexID,uint instanceID:SV_InstanceID)
        { return VMProxyVertexCommon(vertexID,instanceID,true); }

        VMImpostorHit VMProxyHit(VMProxyVaryings input, bool shadow, out float3 positionWS, out float3 normalWS)
        {
            clip((float)input.valid - .5);
            VMInstance instance = _VMInstances[input.instanceId];
            float3 center = mul(instance.objectToWorld, float4(_VMImpostorBoundsCenter.xyz, 1)).xyz;
            float3 observer; bool orthographic; VMProxyObserver(center, shadow, observer, orthographic);
            float radius = max(1e-5, length(_VMImpostorBoundsExtents.xyz) * instance.parameters.x);
            float3 originWS = _VMProxyObserverPosition.xyz;
            if (shadow)
            {
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    originWS = _LightPosition;
                #endif
            }
            float3 directionWS = input.planePositionWS - originWS;
            if (orthographic)
            {
                directionWS = -normalize(observer);
                originWS = input.planePositionWS - directionWS * (radius * 2 + .001);
            }
            VMImpostorHit hit;
            uint viewIndex = input.viewIndex;
            if (input.insideLight != 0)
                viewIndex = VMImpostorSelectView(mul((float3x3)instance.worldToObject,directionWS));
            bool found = VMImpostorIntersect(mul(instance.worldToObject,float4(originWS,1)).xyz,
                mul((float3x3)instance.worldToObject,directionWS), viewIndex, hit);
            clip(found ? 1 : -1);
            positionWS = mul(instance.objectToWorld,float4(hit.positionOS,1)).xyz;
            normalWS = normalize(mul(hit.normalOS,(float3x3)instance.worldToObject));
            return hit;
        }
        float VMProxyDeviceDepth(float4 positionCS)
        {
            clip(positionCS.w - 1e-7);
            float depth = positionCS.z / positionCS.w;
            #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
                depth = depth * .5 + .5;
            #endif
            clip(depth); clip(1-depth);
            return depth;
        }
        struct VMProxyColorOutput { half4 color : SV_Target; float depth : SV_Depth; };
        VMProxyColorOutput VMProxyForward(VMProxyVaryings input)
        {
            float3 positionWS, normalWS; VMImpostorHit hit = VMProxyHit(input,false,positionWS,normalWS);
            InputData data = (InputData)0;
            data.positionWS = positionWS; data.normalWS = normalWS;
            data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
            data.shadowCoord = TransformWorldToShadowCoord(positionWS);
            data.bakedGI = SampleSH(normalWS); data.shadowMask = half4(1,1,1,1);
            data.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            SurfaceData surface = (SurfaceData)0;
            surface.albedo = hit.albedo; surface.alpha = 1; surface.occlusion = 1;
            surface.smoothness = 1-hit.roughness; surface.normalTS = half3(0,0,1);
            VMProxyColorOutput output;
            output.color = UniversalFragmentPBR(data,surface);
            output.color.rgb = MixFog(output.color.rgb,ComputeFogFactor(TransformWorldToHClip(positionWS).z));
            output.depth = VMProxyDeviceDepth(TransformWorldToHClip(positionWS)); return output;
        }
        float VMProxyDepth(VMProxyVaryings input) : SV_Depth
        { float3 p,n; VMProxyHit(input,false,p,n); return VMProxyDeviceDepth(TransformWorldToHClip(p)); }
        void VMProxyNormals(VMProxyVaryings input, out half4 normal : SV_Target0, out float depth : SV_Depth
            #if defined(_WRITE_RENDERING_LAYERS)
                ,out uint renderingLayers : SV_Target1
            #endif
        )
        {
            float3 p,n; VMProxyHit(input,false,p,n); depth = VMProxyDeviceDepth(TransformWorldToHClip(p));
            #if defined(_GBUFFER_NORMALS_OCT)
                normal = half4(PackFloat2To888(saturate(PackNormalOctQuadEncode(n)*.5+.5)),0);
            #else
                normal = half4(n,0);
            #endif
            #if defined(_WRITE_RENDERING_LAYERS)
                renderingLayers = EncodeMeshRenderingLayer();
            #endif
        }
        VMProxyColorOutput VMProxyMotion(VMProxyVaryings input)
        {
            float3 p,n; VMImpostorHit hit = VMProxyHit(input,false,p,n);
            float3 previous = mul(_VMPreviousObjectToWorld[input.instanceId],float4(hit.positionOS,1)).xyz;
            VMProxyColorOutput output;
            output.color = half4(VMEncodeMotion(mul(_VMCurrentViewProjection,float4(p,1)),
                mul(_VMPreviousViewProjection,float4(previous,1))),0,0);
            output.depth = VMProxyDeviceDepth(TransformWorldToHClip(p)); return output;
        }
        float VMProxyShadow(VMProxyVaryings input) : SV_Depth
        {
            float3 p,n; VMProxyHit(input,true,p,n);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirection = normalize(_LightPosition-p);
            #else
                float3 lightDirection = _LightDirection;
            #endif
            return VMProxyDeviceDepth(ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(p,n,lightDirection))));
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit" Tags { "LightMode"="UniversalForwardOnly" }
            ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VMProxyVertex
            #pragma fragment VMProxyForward
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile_fog
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly" Tags { "LightMode"="DepthOnly" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VMProxyVertex
            #pragma fragment VMProxyDepth
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals" Tags { "LightMode"="DepthNormalsOnly" }
            ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VMProxyVertex
            #pragma fragment VMProxyNormals
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "MotionVectors" Tags { "LightMode"="MotionVectors" }
            ZWrite Off ZTest LEqual ColorMask RG
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VMProxyVertex
            #pragma fragment VMProxyMotion
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster" Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VMProxyShadowVertex
            #pragma fragment VMProxyShadow
            #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
    }
}
