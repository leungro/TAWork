Shader "MassiveCharacters/GPU Skinning Instanced"
{
    Properties
    {
        _BaseColor("基础颜色", Color) = (0.8, 0.86, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float4 boneIndicesUv : TEXCOORD2;
                float4 boneWeightsUv : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct MassiveInstanceAnimData
            {
                float4 anim;
                float4 extra;
            };

            TEXTURE2D(_MassiveBoneTexture);
            SAMPLER(sampler_MassiveBoneTexture);

            StructuredBuffer<MassiveInstanceAnimData> _MassiveInstanceAnimData;
            float4 _BaseColor;
            float4 _MassiveBoneTextureSize;
            float4 _MassiveClipStartFrames;
            float4 _MassiveClipFrameCounts;
            float _MassiveSampleRate;
            int _MassiveBoneCount;
            int _MassiveInstanceBase;
            int _MassiveIsAccessoryPass;
            int _MassiveUseVertexAnimation;
            int _MassiveShowAccessory;

            /// <summary>
            /// 在非 instancing 变体编译时给实例 ID 一个安全兜底。
            /// </summary>
            uint GetMassiveInstanceId(Attributes input)
            {
            #if UNITY_ANY_INSTANCING_ENABLED
                return UNITY_GET_INSTANCE_ID(input);
            #else
                return 0;
            #endif
            }

            /// <summary>
            /// 根据骨骼下标和全局动画帧采样 3x4 蒙皮矩阵。
            /// </summary>
            float4x4 LoadBoneMatrix(int boneIndex, int frameIndex)
            {
                int rowBase = frameIndex * max(1, _MassiveBoneCount) * 3 + boneIndex * 3;
                float width = max(1.0, _MassiveBoneTextureSize.x);
                float height = max(1.0, _MassiveBoneTextureSize.y);

                int x0 = rowBase % (int)width;
                int y0 = rowBase / (int)width;
                int x1 = (rowBase + 1) % (int)width;
                int y1 = (rowBase + 1) / (int)width;
                int x2 = (rowBase + 2) % (int)width;
                int y2 = (rowBase + 2) / (int)width;

                float2 uv0 = float2((x0 + 0.5) / width, (y0 + 0.5) / height);
                float2 uv1 = float2((x1 + 0.5) / width, (y1 + 0.5) / height);
                float2 uv2 = float2((x2 + 0.5) / width, (y2 + 0.5) / height);

                float4 r0 = SAMPLE_TEXTURE2D_LOD(_MassiveBoneTexture, sampler_MassiveBoneTexture, uv0, 0);
                float4 r1 = SAMPLE_TEXTURE2D_LOD(_MassiveBoneTexture, sampler_MassiveBoneTexture, uv1, 0);
                float4 r2 = SAMPLE_TEXTURE2D_LOD(_MassiveBoneTexture, sampler_MassiveBoneTexture, uv2, 0);
                return float4x4(r0, r1, r2, float4(0, 0, 0, 1));
            }

            /// <summary>
            /// 根据动画片段下标和时间换算全局帧下标。
            /// 当前 Demo 烘焙顺序固定为 Ami、Idle、Run。
            /// </summary>
            void ResolveSkinningFrames(float clipIndex, float clipTime, out int frame0, out int frame1, out float lerpValue)
            {
                int clip = clamp((int)round(clipIndex), 0, 3);
                int startFrame = (int)round(_MassiveClipStartFrames[clip]);
                int frameCount = max(1, (int)round(_MassiveClipFrameCounts[clip]));
                float frame = frac(clipTime * max(1.0, _MassiveSampleRate) / max(1.0, frameCount - 1)) * max(1.0, frameCount - 1);
                int local0 = (int)floor(frame) % frameCount;
                int local1 = (local0 + 1) % frameCount;
                frame0 = startFrame + local0;
                frame1 = startFrame + local1;
                lerpValue = frac(frame);
            }

            /// <summary>
            /// 用四骨骼权重执行一次 GPU 蒙皮。
            /// </summary>
            void SkinVertex(float3 positionOS, float3 normalOS, uint4 boneIndices, float4 boneWeights, int frameIndex, out float3 skinnedPosition, out float3 skinnedNormal)
            {
                float4 position = float4(positionOS, 1.0);
                float4 normal = float4(normalOS, 0.0);
                float4 skinnedPos4 = 0;
                float4 skinnedNrm4 = 0;

                float4x4 m0 = LoadBoneMatrix((int)boneIndices.x, frameIndex);
                float4x4 m1 = LoadBoneMatrix((int)boneIndices.y, frameIndex);
                float4x4 m2 = LoadBoneMatrix((int)boneIndices.z, frameIndex);
                float4x4 m3 = LoadBoneMatrix((int)boneIndices.w, frameIndex);

                skinnedPos4 += mul(m0, position) * boneWeights.x;
                skinnedPos4 += mul(m1, position) * boneWeights.y;
                skinnedPos4 += mul(m2, position) * boneWeights.z;
                skinnedPos4 += mul(m3, position) * boneWeights.w;

                skinnedNrm4 += mul(m0, normal) * boneWeights.x;
                skinnedNrm4 += mul(m1, normal) * boneWeights.y;
                skinnedNrm4 += mul(m2, normal) * boneWeights.z;
                skinnedNrm4 += mul(m3, normal) * boneWeights.w;

                skinnedPosition = skinnedPos4.xyz;
                skinnedNormal = normalize(skinnedNrm4.xyz);
            }

            /// <summary>
            /// 顶点阶段从骨骼矩阵贴图恢复蒙皮姿态。
            /// </summary>
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                uint instanceIndex = (uint)_MassiveInstanceBase + GetMassiveInstanceId(input);
                MassiveInstanceAnimData instanceData = _MassiveInstanceAnimData[instanceIndex];

                float uvPartValue = floor(max(0.0, input.uv.x));
                bool isAccessoryVertex = uvPartValue >= 1.0 || _MassiveIsAccessoryPass != 0;
                bool selectedAccessoryPart = abs(uvPartValue - round(instanceData.anim.w)) < 0.5;
                bool hideAccessory = isAccessoryVertex && (_MassiveShowAccessory == 0 || !selectedAccessoryPart);
                if (hideAccessory)
                {
                    output.positionCS = float4(0, 0, 0, 0);
                    output.normalWS = float3(0, 1, 0);
                    output.positionWS = 0;
                    return output;
                }

                // UV0.x >= 1 的区间表示当前第一版未蒙皮配件，跟随实例矩阵移动，不参与身体骨骼蒙皮。
                if (_MassiveUseVertexAnimation == 0 || uvPartValue >= 1.0)
                {
                    float3 rigidPositionWS = TransformObjectToWorld(input.positionOS);
                    output.positionWS = rigidPositionWS;
                    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                    output.positionCS = TransformWorldToHClip(rigidPositionWS);
                    return output;
                }

                int frame0;
                int frame1;
                float frameLerp;
                ResolveSkinningFrames(instanceData.anim.x, instanceData.anim.y, frame0, frame1, frameLerp);

                float3 position0;
                float3 normal0;
                float3 position1;
                float3 normal1;
                uint4 boneIndices = (uint4)round(input.boneIndicesUv);
                float4 boneWeights = input.boneWeightsUv;
                SkinVertex(input.positionOS, input.normalOS, boneIndices, boneWeights, frame0, position0, normal0);
                SkinVertex(input.positionOS, input.normalOS, boneIndices, boneWeights, frame1, position1, normal1);

                float3 positionOS = lerp(position0, position1, frameLerp);
                float3 normalOS = normalize(lerp(normal0, normal1, frameLerp));
                float3 positionWS = TransformObjectToWorld(positionOS);

                output.positionWS = positionWS;
                output.normalWS = TransformObjectToWorldNormal(normalOS);
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            /// <summary>
            /// 简单半兰伯特光照，避免 Demo 依赖复杂材质。
            /// </summary>
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Light mainLight = GetMainLight();
                float ndl = saturate(dot(normalize(input.normalWS), normalize(mainLight.direction)));
                float lighting = 0.25 + ndl * 0.75;
                return half4(_BaseColor.rgb * lighting, _BaseColor.a);
            }
            ENDHLSL
        }
    }
}
