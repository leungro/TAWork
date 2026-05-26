using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MassiveCharacters
{
    /// <summary>
    /// 海量角色 Demo 的渲染类型。
    /// </summary>
    public enum MassiveCharacterDemoMode
    {
        GpuSkinning,
        Vat
    }

    /// <summary>
    /// 海量角色 Demo 的动画分布方式。
    /// </summary>
    public enum MassiveAnimationMode
    {
        Random,
        Ami,
        Idle,
        Run
    }

    /// <summary>
    /// 海量角色 Demo 的移动方式。
    /// </summary>
    public enum MassiveMovementMode
    {
        Static,
        Circle,
        Forward
    }

    /// <summary>
    /// 单个实例传给 Shader 的自定义动画数据。
    /// </summary>
    public struct MassiveInstanceAnimData
    {
        public Vector4 anim;
        public Vector4 extra;
    }

    /// <summary>
    /// RenderMeshInstanced 需要识别 objectToWorld 字段；其它字段用于保留扩展空间。
    /// </summary>
    public struct MassiveRenderInstance
    {
        public Matrix4x4 objectToWorld;
    }

    /// <summary>
    /// 方案 C 和方案 D 共用的海量实例渲染器。
    /// </summary>
    [ExecuteAlways]
    public sealed class MassiveCharacterDemoRenderer : MonoBehaviour
    {
        private const int MaxBatchSize = 1023;
        private const int MaxAccessoryPartIndex = 3;
        private static readonly int InstanceBaseId = Shader.PropertyToID("_MassiveInstanceBase");
        private static readonly int InstanceAnimDataId = Shader.PropertyToID("_MassiveInstanceAnimData");
        private static readonly int BoneTextureId = Shader.PropertyToID("_MassiveBoneTexture");
        private static readonly int BoneTextureSizeId = Shader.PropertyToID("_MassiveBoneTextureSize");
        private static readonly int BoneCountId = Shader.PropertyToID("_MassiveBoneCount");
        private static readonly int ClipStartFramesId = Shader.PropertyToID("_MassiveClipStartFrames");
        private static readonly int ClipFrameCountsId = Shader.PropertyToID("_MassiveClipFrameCounts");
        private static readonly int SampleRateId = Shader.PropertyToID("_MassiveSampleRate");
        private static readonly int VatPositionTextureId = Shader.PropertyToID("_MassiveVatPositionTexture");
        private static readonly int VatNormalTextureId = Shader.PropertyToID("_MassiveVatNormalTexture");
        private static readonly int VatTextureSizeId = Shader.PropertyToID("_MassiveVatTextureSize");
        private static readonly int VatVertexCountId = Shader.PropertyToID("_MassiveVatVertexCount");
        private static readonly int VatRowsPerFrameId = Shader.PropertyToID("_MassiveVatRowsPerFrame");
        private static readonly int IsAccessoryPassId = Shader.PropertyToID("_MassiveIsAccessoryPass");
        private static readonly int UseVertexAnimationId = Shader.PropertyToID("_MassiveUseVertexAnimation");
        private static readonly int ShowAccessoryId = Shader.PropertyToID("_MassiveShowAccessory");
        private static readonly int TintId = Shader.PropertyToID("_BaseColor");

        [Header("Demo 类型")]
        public MassiveCharacterDemoMode demoMode = MassiveCharacterDemoMode.Vat;

        [Header("渲染资源")]
        public Mesh bodyMesh;
        public Mesh accessoryMesh;
        public Material bodyMaterial;
        public Material accessoryMaterial;
        public GpuSkinningAnimationData gpuSkinningData;
        public VatAnimationData vatData;

        [Header("实例参数")]
        [Min(1)] public int instanceCount = 1000;
        [Min(1)] public int gridColumns = 50;
        [Min(0.1f)] public float spacing = 1.7f;
        [Min(0.01f)] public float animationSpeed = 1f;
        public MassiveAnimationMode animationMode = MassiveAnimationMode.Random;
        public MassiveMovementMode movementMode = MassiveMovementMode.Static;
        public bool randomAccessory = true;
        public bool showAccessory = true;
        [Range(0, MaxAccessoryPartIndex)] public int accessoryDisplayPart = 1;
        public bool animateAccessory;
        public int randomSeed = 12345;

        [Header("调试显示")]
        public bool showStats = true;
        public Color bodyTint = new Color(0.78f, 0.86f, 1f, 1f);
        public Color accessoryTint = new Color(1f, 0.65f, 0.2f, 1f);

        private MassiveRenderInstance[] _instances;
        private MassiveInstanceAnimData[] _animData;
        private Vector3[] _basePositions;
        private float[] _moveAngles;
        private GraphicsBuffer _animDataBuffer;
        private MaterialPropertyBlock _bodyProperties;
        private MaterialPropertyBlock _accessoryProperties;
        private RenderParams _bodyRenderParams;
        private RenderParams _accessoryRenderParams;
        private readonly MassiveRenderInstance[] _batchInstances = new MassiveRenderInstance[MaxBatchSize];
        private int _lastInstanceCount = -1;
        private int _lastSeed = int.MinValue;
        private bool _lastRandomAccessory;
        private int _lastAccessoryDisplayPart = int.MinValue;
        private double _lastUpdateTime;
        private double _lastStatsTime;
        private int _frames;
        private float _fps;

        /// <summary>
        /// 初始化材质参数块和实例数组。
        /// </summary>
        private void OnEnable()
        {
            _bodyProperties = new MaterialPropertyBlock();
            _accessoryProperties = new MaterialPropertyBlock();
            _lastUpdateTime = EditorPreviewTime();
            RebuildInstancesIfNeeded(true);
        }

        /// <summary>
        /// 释放 GPU buffer，避免退出播放模式或禁用组件后泄漏资源。
        /// </summary>
        private void OnDisable()
        {
            ReleaseBuffers();
        }

        /// <summary>
        /// 在编辑器和运行时都刷新实例动画，并提交实例化绘制。
        /// </summary>
        private void Update()
        {
            RebuildInstancesIfNeeded(false);
            if (!HasRenderableResources())
            {
                return;
            }

            UpdateStats();
            UpdateInstances(GetFrameDeltaTime());
            UploadAnimationBuffer();
            DrawMeshBatch(bodyMesh, bodyMaterial, _bodyProperties, ref _bodyRenderParams, false);

            if (showAccessory && accessoryMesh != null && accessoryMaterial != null)
            {
                DrawMeshBatch(accessoryMesh, accessoryMaterial, _accessoryProperties, ref _accessoryRenderParams, true);
            }
        }

        /// <summary>
        /// 根据实例数量或随机种子变化重新生成实例基础数据。
        /// </summary>
        private void RebuildInstancesIfNeeded(bool force)
        {
            int safeCount = Mathf.Max(1, instanceCount);
            int safeAccessoryDisplayPart = Mathf.Clamp(accessoryDisplayPart, 0, MaxAccessoryPartIndex);
            if (!force && _lastInstanceCount == safeCount && _lastSeed == randomSeed && _lastRandomAccessory == randomAccessory && _lastAccessoryDisplayPart == safeAccessoryDisplayPart)
            {
                return;
            }

            _lastInstanceCount = safeCount;
            _lastSeed = randomSeed;
            _lastRandomAccessory = randomAccessory;
            _lastAccessoryDisplayPart = safeAccessoryDisplayPart;
            _instances = new MassiveRenderInstance[safeCount];
            _animData = new MassiveInstanceAnimData[safeCount];
            _basePositions = new Vector3[safeCount];
            _moveAngles = new float[safeCount];

            UnityEngine.Random.State oldState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(randomSeed);

            int columns = Mathf.Max(1, gridColumns);
            for (int i = 0; i < safeCount; i++)
            {
                int x = i % columns;
                int z = i / columns;
                Vector3 position = new Vector3((x - columns * 0.5f) * spacing, 0f, z * spacing);
                float rotationY = UnityEngine.Random.Range(0f, 360f);
                int clipIndex = ResolveClipIndex(i);
                float clipDuration = Mathf.Max(0.033f, GetClipDuration(clipIndex));
                float phase = UnityEngine.Random.Range(0f, clipDuration);
                float speed = animationSpeed * UnityEngine.Random.Range(0.85f, 1.15f);
                float accessoryPart = randomAccessory ? (UnityEngine.Random.value > 0.5f ? safeAccessoryDisplayPart : 0f) : safeAccessoryDisplayPart;

                _basePositions[i] = position;
                _moveAngles[i] = rotationY * Mathf.Deg2Rad;
                _instances[i].objectToWorld = Matrix4x4.TRS(position, Quaternion.Euler(0f, rotationY, 0f), Vector3.one);
                _animData[i] = new MassiveInstanceAnimData
                {
                    anim = new Vector4(clipIndex, phase, speed, accessoryPart),
                    extra = new Vector4(rotationY, 0f, 0f, 0f)
                };
            }

            UnityEngine.Random.state = oldState;
            EnsureAnimationBuffer();
        }

        /// <summary>
        /// 根据当前模式更新每个实例的播放时间和世界矩阵。
        /// </summary>
        private void UpdateInstances(float deltaTime)
        {
            if (_instances == null || _animData == null)
            {
                return;
            }

            float time = Application.isPlaying ? Time.time : (float)EditorPreviewTime();
            for (int i = 0; i < _instances.Length; i++)
            {
                MassiveInstanceAnimData data = _animData[i];
                int clipIndex = Mathf.Clamp(Mathf.RoundToInt(data.anim.x), 0, GetClipCount() - 1);
                float duration = Mathf.Max(0.033f, GetClipDuration(clipIndex));
                data.anim.y = Mathf.Repeat(data.anim.y + deltaTime * data.anim.z, duration);
                _animData[i] = data;

                Vector3 position = _basePositions[i];
                float rotationY = data.extra.x;
                if (movementMode == MassiveMovementMode.Circle)
                {
                    float angle = _moveAngles[i] + time * 0.35f * data.anim.z;
                    position += new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.45f;
                    rotationY = angle * Mathf.Rad2Deg + 90f;
                }
                else if (movementMode == MassiveMovementMode.Forward)
                {
                    float angle = _moveAngles[i];
                    position += new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * Mathf.Sin(time * data.anim.z) * 0.65f;
                    rotationY = angle * Mathf.Rad2Deg;
                }

                _instances[i].objectToWorld = Matrix4x4.TRS(position, Quaternion.Euler(0f, rotationY, 0f), Vector3.one);
            }
        }

        /// <summary>
        /// 将实例动画参数上传到 GPU buffer。
        /// </summary>
        private void UploadAnimationBuffer()
        {
            EnsureAnimationBuffer();
            _animDataBuffer.SetData(_animData);
        }

        /// <summary>
        /// 分批提交 RenderMeshInstanced 绘制。
        /// </summary>
        private void DrawMeshBatch(Mesh mesh, Material material, MaterialPropertyBlock properties, ref RenderParams renderParams, bool accessory)
        {
            if (mesh == null || material == null || _instances == null)
            {
                return;
            }

            ConfigureProperties(properties, accessory);
            renderParams = new RenderParams(material)
            {
                layer = gameObject.layer,
                matProps = properties,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = CalculateWorldBounds()
            };

            int rendered = 0;
            while (rendered < _instances.Length)
            {
                int batchCount = Mathf.Min(MaxBatchSize, _instances.Length - rendered);
                Array.Copy(_instances, rendered, _batchInstances, 0, batchCount);
                properties.SetInt(InstanceBaseId, rendered);
                Graphics.RenderMeshInstanced(renderParams, mesh, 0, _batchInstances, batchCount);
                rendered += batchCount;
            }
        }

        /// <summary>
        /// 配置当前 Demo 类型对应的材质参数。
        /// </summary>
        private void ConfigureProperties(MaterialPropertyBlock properties, bool accessory)
        {
            properties.Clear();
            properties.SetBuffer(InstanceAnimDataId, _animDataBuffer);
            properties.SetColor(TintId, accessory ? accessoryTint : bodyTint);
            properties.SetInt(IsAccessoryPassId, accessory ? 1 : 0);
            properties.SetInt(UseVertexAnimationId, accessory && !animateAccessory ? 0 : 1);
            properties.SetInt(ShowAccessoryId, showAccessory ? 1 : 0);

            if (demoMode == MassiveCharacterDemoMode.GpuSkinning && gpuSkinningData != null)
            {
                properties.SetTexture(BoneTextureId, gpuSkinningData.boneTexture);
                properties.SetVector(BoneTextureSizeId, new Vector4(gpuSkinningData.textureWidth, gpuSkinningData.textureHeight, 1f / Mathf.Max(1, gpuSkinningData.textureWidth), 1f / Mathf.Max(1, gpuSkinningData.textureHeight)));
                properties.SetInt(BoneCountId, gpuSkinningData.boneCount);
                ConfigureClipProperties(properties, gpuSkinningData.clips, gpuSkinningData.sampleRate);
            }
            else if (demoMode == MassiveCharacterDemoMode.Vat && vatData != null)
            {
                properties.SetTexture(VatPositionTextureId, vatData.positionTexture);
                properties.SetTexture(VatNormalTextureId, vatData.normalTexture);
                properties.SetVector(VatTextureSizeId, new Vector4(vatData.textureWidth, vatData.textureHeight, 1f / Mathf.Max(1, vatData.textureWidth), 1f / Mathf.Max(1, vatData.textureHeight)));
                properties.SetInt(VatVertexCountId, vatData.vertexCount);
                properties.SetInt(VatRowsPerFrameId, Mathf.Max(1, vatData.rowsPerFrame));
                ConfigureClipProperties(properties, vatData.clips, vatData.sampleRate);
            }
        }

        /// <summary>
        /// 把前三个动画片段的起始帧、帧数和采样率传给 Shader。
        /// </summary>
        private static void ConfigureClipProperties(MaterialPropertyBlock properties, MassiveAnimationClipInfo[] clips, float sampleRate)
        {
            Vector4 starts = Vector4.zero;
            Vector4 counts = Vector4.one;
            if (clips != null)
            {
                for (int i = 0; i < Mathf.Min(4, clips.Length); i++)
                {
                    MassiveAnimationClipInfo clip = clips[i];
                    starts[i] = clip != null ? clip.startFrame : 0;
                    counts[i] = clip != null ? Mathf.Max(1, clip.frameCount) : 1;
                }
            }

            properties.SetVector(ClipStartFramesId, starts);
            properties.SetVector(ClipFrameCountsId, counts);
            properties.SetFloat(SampleRateId, Mathf.Max(1f, sampleRate));
        }

        /// <summary>
        /// 确保实例动画 buffer 的数量和 CPU 数据一致。
        /// </summary>
        private void EnsureAnimationBuffer()
        {
            int count = _animData == null ? 0 : _animData.Length;
            if (count <= 0)
            {
                return;
            }

            int stride = sizeof(float) * 8;
            if (_animDataBuffer != null && _animDataBuffer.count == count && _animDataBuffer.stride == stride)
            {
                return;
            }

            ReleaseBuffers();
            _animDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        }

        /// <summary>
        /// 释放所有运行时创建的 GPU buffer。
        /// </summary>
        private void ReleaseBuffers()
        {
            if (_animDataBuffer != null)
            {
                _animDataBuffer.Release();
                _animDataBuffer = null;
            }
        }

        /// <summary>
        /// 检查当前 Demo 是否有足够资源可以绘制。
        /// </summary>
        private bool HasRenderableResources()
        {
            if (bodyMesh == null || bodyMaterial == null)
            {
                return false;
            }

            if (demoMode == MassiveCharacterDemoMode.GpuSkinning)
            {
                return gpuSkinningData != null && gpuSkinningData.boneTexture != null && gpuSkinningData.clips != null && gpuSkinningData.clips.Length > 0;
            }

            return vatData != null && vatData.positionTexture != null && vatData.normalTexture != null && vatData.clips != null && vatData.clips.Length > 0;
        }

        /// <summary>
        /// 根据动画模式选择实例使用的 clip 下标。
        /// </summary>
        private int ResolveClipIndex(int instanceIndex)
        {
            int clipCount = GetClipCount();
            if (clipCount <= 0)
            {
                return 0;
            }

            if (animationMode == MassiveAnimationMode.Ami)
            {
                return 0;
            }

            if (animationMode == MassiveAnimationMode.Idle)
            {
                return Mathf.Min(1, clipCount - 1);
            }

            if (animationMode == MassiveAnimationMode.Run)
            {
                return Mathf.Min(2, clipCount - 1);
            }

            return instanceIndex % clipCount;
        }

        /// <summary>
        /// 获取当前 Demo 使用的数据 clip 数量。
        /// </summary>
        private int GetClipCount()
        {
            if (demoMode == MassiveCharacterDemoMode.GpuSkinning && gpuSkinningData != null && gpuSkinningData.clips != null)
            {
                return Mathf.Max(1, gpuSkinningData.clips.Length);
            }

            if (demoMode == MassiveCharacterDemoMode.Vat && vatData != null && vatData.clips != null)
            {
                return Mathf.Max(1, vatData.clips.Length);
            }

            return 1;
        }

        /// <summary>
        /// 获取指定 clip 的长度，防止动画时间越界。
        /// </summary>
        private float GetClipDuration(int clipIndex)
        {
            MassiveAnimationClipInfo[] clips = demoMode == MassiveCharacterDemoMode.GpuSkinning && gpuSkinningData != null ? gpuSkinningData.clips : vatData != null ? vatData.clips : null;
            if (clips == null || clips.Length == 0)
            {
                return 1f;
            }

            clipIndex = Mathf.Clamp(clipIndex, 0, clips.Length - 1);
            return Mathf.Max(0.033f, clips[clipIndex].duration);
        }

        /// <summary>
        /// 估算整批实例的世界包围盒，供 Unity 进行批级裁剪。
        /// </summary>
        private Bounds CalculateWorldBounds()
        {
            int columns = Mathf.Max(1, gridColumns);
            int rows = Mathf.CeilToInt(Mathf.Max(1, instanceCount) / (float)columns);
            Vector3 center = new Vector3(0f, 1f, rows * spacing * 0.5f);
            Vector3 size = new Vector3(columns * spacing + 10f, 6f, rows * spacing + 10f);
            return new Bounds(center, size);
        }

        /// <summary>
        /// 更新简单 FPS 统计。
        /// </summary>
        private void UpdateStats()
        {
            _frames++;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastStatsTime < 0.5f)
            {
                return;
            }

            _fps = (float)(_frames / Math.Max(0.0001, now - _lastStatsTime));
            _frames = 0;
            _lastStatsTime = now;
        }

        /// <summary>
        /// 绘制运行时统计，方便 Demo 阶段快速观察实例数量和数据规模。
        /// </summary>
        private void OnGUI()
        {
            if (!showStats)
            {
                return;
            }

            Rect rect = new Rect(16f, 16f, 430f, 150f);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, rect.height - 16f));
            GUILayout.Label($"Demo: {demoMode}");
            GUILayout.Label($"实例数量: {Mathf.Max(1, instanceCount)}，批次数: {Mathf.CeilToInt(Mathf.Max(1, instanceCount) / (float)MaxBatchSize)}");
            GUILayout.Label($"FPS: {_fps:F1}");
            GUILayout.Label($"动画: {animationMode}，移动: {movementMode}，配件: {(showAccessory ? $"显示区间 {Mathf.Clamp(accessoryDisplayPart, 0, MaxAccessoryPartIndex)}" : "关闭")}");
            GUILayout.Label($"动画数据估算: {EstimateAnimationMemoryText()}");
            GUILayout.EndArea();
        }

        /// <summary>
        /// 估算当前动画数据占用，便于比较方案 C 和方案 D。
        /// </summary>
        private string EstimateAnimationMemoryText()
        {
            if (demoMode == MassiveCharacterDemoMode.GpuSkinning && gpuSkinningData != null)
            {
                long bytes = (long)gpuSkinningData.TotalFrameCount * gpuSkinningData.boneCount * 3L * 16L;
                return $"{FormatBytes(bytes)} 骨骼矩阵";
            }

            if (demoMode == MassiveCharacterDemoMode.Vat && vatData != null)
            {
                long bytes = (long)vatData.TotalFrameCount * vatData.vertexCount * 32L;
                return $"{FormatBytes(bytes)} 顶点位置+法线";
            }

            return "未烘焙";
        }

        /// <summary>
        /// 把字节数格式化成更容易阅读的文本。
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes > 1024L * 1024L)
            {
                return $"{bytes / (1024f * 1024f):F2} MB";
            }

            return $"{bytes / 1024f:F2} KB";
        }

        /// <summary>
        /// 获取当前帧用于推进动画的时间增量，编辑器预览状态下也能稳定前进。
        /// </summary>
        private float GetFrameDeltaTime()
        {
            if (Application.isPlaying)
            {
                return Mathf.Max(0f, Time.deltaTime);
            }

            double now = EditorPreviewTime();
            float deltaTime = (float)Math.Max(0.0, now - _lastUpdateTime);
            _lastUpdateTime = now;
            return Mathf.Min(deltaTime, 0.1f);
        }

        /// <summary>
        /// 编辑器非播放状态下提供一个近似时间，让 Scene 视图里也能看到动画变化。
        /// </summary>
        private static double EditorPreviewTime()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorApplication.timeSinceStartup;
#else
            return Time.timeAsDouble;
#endif
        }
    }
}
