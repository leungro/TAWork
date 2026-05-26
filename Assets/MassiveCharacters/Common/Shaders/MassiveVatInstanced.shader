Shader "MassiveCharacters/VAT Instanced"
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
                float2 vertexIndexUV : TEXCOORD1;
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

            TEXTURE2D(_MassiveVatPositionTexture);
            SAMPLER(sampler_MassiveVatPositionTexture);
            TEXTURE2D(_MassiveVatNormalTexture);
            SAMPLER(sampler_MassiveVatNormalTexture);

            StructuredBuffer<MassiveInstanceAnimData> _MassiveInstanceAnimData;
            float4 _BaseColor;
            float4 _MassiveVatTextureSize;
            float4 _MassiveClipStartFrames;
            float4 _MassiveClipFrameCounts;
            float _MassiveSampleRate;
            int _MassiveVatVertexCount;
            int _MassiveVatRowsPerFrame;
            int _MassiveInstanceBase;
            int _MassiveIsAccessoryPass;
            int _MassiveUseVertexAnimation;

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
            /// 将顶点索引和帧索引转换到 VAT 纹理采样坐标。
            /// </summary>
            float2 GetVatUv(int vertexIndex, int frameIndex)
            {
                int width = max(1, (int)round(_MassiveVatTextureSize.x));
                float height = max(1.0, _MassiveVatTextureSize.y);
                int rowsPerFrame = max(1, _MassiveVatRowsPerFrame);
                int x = vertexIndex % width;
                int y = frameIndex * rowsPerFrame + vertexIndex / width;
                return float2((x + 0.5) / width, (y + 0.5) / height);
            }

            /// <summary>
            /// 根据动画片段下标和时间换算全局帧下标。
            /// 当前 Demo 烘焙顺序固定为 Ami、Idle、Run。
            /// </summary>
            void ResolveVatFrames(float clipIndex, float clipTime, out int frame0, out int frame1, out float lerpValue)
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
            /// 顶点阶段从 VAT 贴图恢复当前位置和法线。
            /// </summary>
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                uint instanceIndex = (uint)_MassiveInstanceBase + GetMassiveInstanceId(input);
                MassiveInstanceAnimData instanceData = _MassiveInstanceAnimData[instanceIndex];

                // 只有配件 pass 才根据 anim.w 做显隐，身体 pass 始终显示。
                if (_MassiveIsAccessoryPass != 0 && instanceData.anim.w < 0.5)
                {
                    output.positionCS = float4(0, 0, 0, 0);
                    output.normalWS = float3(0, 1, 0);
                    output.positionWS = 0;
                    return output;
                }

                // 当前第一版配件走刚性显隐，不采样身体 VAT；身体 pass 才使用 VAT 动画。
                if (_MassiveUseVertexAnimation == 0)
                {
                    float3 rigidPositionWS = TransformObjectToWorld(input.positionOS);
                    output.positionWS = rigidPositionWS;
                    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                    output.positionCS = TransformWorldToHClip(rigidPositionWS);
                    return output;
                }

                int vertexIndex = clamp((int)round(input.vertexIndexUV.x), 0, max(0, _MassiveVatVertexCount - 1));
                int frame0;
                int frame1;
                float frameLerp;
                ResolveVatFrames(instanceData.anim.x, instanceData.anim.y, frame0, frame1, frameLerp);

                float2 uv0 = GetVatUv(vertexIndex, frame0);
                float2 uv1 = GetVatUv(vertexIndex, frame1);
                float3 positionOS0 = SAMPLE_TEXTURE2D_LOD(_MassiveVatPositionTexture, sampler_MassiveVatPositionTexture, uv0, 0).xyz;
                float3 positionOS1 = SAMPLE_TEXTURE2D_LOD(_MassiveVatPositionTexture, sampler_MassiveVatPositionTexture, uv1, 0).xyz;
                float3 normalOS0 = SAMPLE_TEXTURE2D_LOD(_MassiveVatNormalTexture, sampler_MassiveVatNormalTexture, uv0, 0).xyz;
                float3 normalOS1 = SAMPLE_TEXTURE2D_LOD(_MassiveVatNormalTexture, sampler_MassiveVatNormalTexture, uv1, 0).xyz;

                float3 positionOS = lerp(positionOS0, positionOS1, frameLerp);
                float3 normalOS = normalize(lerp(normalOS0, normalOS1, frameLerp));
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
