using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MassiveCharacters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MassiveCharacters.Editor
{
    /// <summary>
    /// 海量角色 Demo 的一键准备、烘焙和场景生成工具。
    /// </summary>
    public static class MassiveCharacterDemoBuilder
    {
        private const string Root = "Assets/MassiveCharacters";
        private const string ModelPath = "Assets/Model/mixamo.FBX";
        private const string MeshModelPath = "Assets/Model/mixamo_mesh.fbx";
        private const string AmiPath = "Assets/Model/Animation/mixamo@Ami.FBX";
        private const string IdlePath = "Assets/Model/Animation/mixamo@idle.FBX";
        private const string RunPath = "Assets/Model/Animation/mixamo@run.FBX";
        private const string AccessoryKeyword = "piaodai";
        private const float SampleRate = 30f;
        private const int BodyUvPartIndex = 0;
        private const int PiaodaiUvPartIndex = 1;
        private const string LegacyGpuAccessoryMeshPath = "Assets/MassiveCharacters/GpuSkinningDemo/BakedData/GpuSkinning_PiaodaiMesh.asset";
        private const string LegacyGpuAccessoryMaterialPath = "Assets/MassiveCharacters/GpuSkinningDemo/Materials/GpuSkinning_Piaodai.mat";

        private static readonly string[] ClipPaths = { AmiPath, IdlePath, RunPath };
        private static readonly string[] ClipNames = { "Ami", "idle", "run" };

        /// <summary>
        /// VAT 烘焙时使用的 mesh 部件描述。
        /// </summary>
        private sealed class VatMeshPart
        {
            public Transform transform;
            public MeshFilter meshFilter;
            public SkinnedMeshRenderer skinnedRenderer;

            public Mesh SharedMesh => skinnedRenderer != null ? skinnedRenderer.sharedMesh : meshFilter != null ? meshFilter.sharedMesh : null;
        }

        /// <summary>
        /// 方案 C 使用的合并骨骼表，保证多个身体 SkinnedMeshRenderer 可以共享同一份骨骼矩阵贴图。
        /// </summary>
        private sealed class GpuBoneSet
        {
            public Transform[] bones;
            public Matrix4x4[] bindposes;
            public Dictionary<Transform, int> lookup;
        }

        /// <summary>
        /// 一键准备导入设置、烘焙两套动画数据，并生成 VAT/GPU Skinning 两个 Demo 场景。
        /// </summary>
        [MenuItem("TAWork/海量角色Demo/一键生成全部Demo")]
        public static void BuildAllDemos()
        {
            EnsureFolders();
            PrepareModelImportSettings();
            BuildVatDemoAssets();
            BuildGpuSkinningDemoAssets();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("海量角色 Demo 生成完成：请打开 Assets/MassiveCharacters/VatDemo/Scenes 或 GpuSkinningDemo/Scenes 下的场景查看。");
        }

        /// <summary>
        /// 只更新 FBX 导入设置，确保后续烘焙可以读取 mesh 和骨骼数据。
        /// </summary>
        [MenuItem("TAWork/海量角色Demo/准备模型导入设置")]
        public static void PrepareModelImportSettings()
        {
            SetModelReadable(ModelPath, false);
            SetModelReadable(MeshModelPath, true);
            for (int i = 0; i < ClipPaths.Length; i++)
            {
                SetModelReadable(ClipPaths[i], false);
            }
        }

        /// <summary>
        /// 只生成 VAT 方案的烘焙数据、材质和场景。
        /// </summary>
        [MenuItem("TAWork/海量角色Demo/生成方案D VAT Demo")]
        public static void BuildVatDemoAssets()
        {
            EnsureFolders();
            PrepareModelImportSettings();

            GameObject bodySourcePrefab = LoadAsset<GameObject>(ModelPath);
            GameObject accessorySourcePrefab = LoadAsset<GameObject>(MeshModelPath);
            Mesh bodyMesh = CreateVatRuntimeMesh(bodySourcePrefab, false, "Assets/MassiveCharacters/VatDemo/BakedData/Vat_BodyMesh.asset");
            Mesh accessoryMesh = CreateRigidAccessoryRuntimeMesh(accessorySourcePrefab, "Assets/MassiveCharacters/VatDemo/BakedData/Vat_PiaodaiMesh.asset", "Vat_PiaodaiMesh");
            VatAnimationData data = BakeVatAnimation(bodySourcePrefab, bodyMesh, "Assets/MassiveCharacters/VatDemo/BakedData/VatAnimationData.asset");
            Material bodyMaterial = CreateMaterial("Assets/MassiveCharacters/VatDemo/Materials/Vat_Body.mat", "MassiveCharacters/VAT Instanced", new Color(0.65f, 0.82f, 1f, 1f));
            Material accessoryMaterial = CreateMaterial("Assets/MassiveCharacters/VatDemo/Materials/Vat_Piaodai.mat", "MassiveCharacters/VAT Instanced", new Color(1f, 0.63f, 0.2f, 1f));

            CreateDemoScene(
                "Assets/MassiveCharacters/VatDemo/Scenes/VAT_MassiveCharacters.unity",
                MassiveCharacterDemoMode.Vat,
                bodyMesh,
                accessoryMesh,
                bodyMaterial,
                accessoryMaterial,
                null,
                data,
                false);
        }

        /// <summary>
        /// 只生成 GPU 骨骼动画方案的烘焙数据、材质和场景。
        /// </summary>
        [MenuItem("TAWork/海量角色Demo/生成方案C GPU骨骼动画Demo")]
        public static void BuildGpuSkinningDemoAssets()
        {
            EnsureFolders();
            PrepareModelImportSettings();
            DeleteLegacyGpuAccessoryAssets();

            GameObject bodySourcePrefab = LoadAsset<GameObject>(ModelPath);
            GameObject accessorySourcePrefab = LoadAsset<GameObject>(MeshModelPath);
            Mesh bodyMesh = CreateGpuCombinedRuntimeMesh(bodySourcePrefab, accessorySourcePrefab, "Assets/MassiveCharacters/GpuSkinningDemo/BakedData/GpuSkinning_BodyMesh.asset");
            Mesh accessoryMesh = null;
            GpuSkinningAnimationData data = BakeGpuSkinningAnimation(bodySourcePrefab, bodyMesh, "Assets/MassiveCharacters/GpuSkinningDemo/BakedData/GpuSkinningAnimationData.asset");
            Material bodyMaterial = CreateMaterial("Assets/MassiveCharacters/GpuSkinningDemo/Materials/GpuSkinning_Body.mat", "MassiveCharacters/GPU Skinning Instanced", new Color(0.72f, 0.9f, 0.78f, 1f));
            Material accessoryMaterial = null;

            CreateDemoScene(
                "Assets/MassiveCharacters/GpuSkinningDemo/Scenes/GPU_Skinning_MassiveCharacters.unity",
                MassiveCharacterDemoMode.GpuSkinning,
                bodyMesh,
                accessoryMesh,
                bodyMaterial,
                accessoryMaterial,
                data,
                null,
                false);
        }

        /// <summary>
        /// 创建 Demo 所需目录。
        /// </summary>
        private static void EnsureFolders()
        {
            string[] folders =
            {
                Root,
                $"{Root}/Common",
                $"{Root}/Common/Scripts",
                $"{Root}/Common/Editor",
                $"{Root}/Common/Shaders",
                $"{Root}/VatDemo",
                $"{Root}/VatDemo/BakedData",
                $"{Root}/VatDemo/Materials",
                $"{Root}/VatDemo/Scenes",
                $"{Root}/GpuSkinningDemo",
                $"{Root}/GpuSkinningDemo/BakedData",
                $"{Root}/GpuSkinningDemo/Materials",
                $"{Root}/GpuSkinningDemo/Scenes"
            };

            foreach (string folder in folders)
            {
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    string parent = Path.GetDirectoryName(folder)?.Replace("\\", "/");
                    string name = Path.GetFileName(folder);
                    AssetDatabase.CreateFolder(parent, name);
                }
            }
        }

        /// <summary>
        /// 方案 C 已切换为完整单 mesh，清理旧版独立 piaodai pass 资产，避免误判当前仍在拆分配件。
        /// </summary>
        private static void DeleteLegacyGpuAccessoryAssets()
        {
            string[] legacyAssets =
            {
                LegacyGpuAccessoryMeshPath,
                LegacyGpuAccessoryMaterialPath
            };

            foreach (string path in legacyAssets)
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
            }
        }

        /// <summary>
        /// 设置模型导入器的 Read/Write 和动画开关。
        /// </summary>
        private static void SetModelReadable(string path, bool keepAnimation)
        {
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"没有找到模型导入器：{path}");
                return;
            }

            bool dirty = false;
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                dirty = true;
            }

            if (importer.importAnimation != keepAnimation && (path == MeshModelPath || path == ModelPath))
            {
                importer.importAnimation = keepAnimation;
                dirty = true;
            }

            if (dirty)
            {
                importer.SaveAndReimport();
            }
        }

        /// <summary>
        /// 烘焙 VAT 动画数据，并生成位置/法线贴图。
        /// </summary>
        private static VatAnimationData BakeVatAnimation(GameObject sourcePrefab, Mesh renderMesh, string assetPath)
        {
            GameObject instance = PrefabUtility.InstantiatePrefab(sourcePrefab) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException("无法实例化 mixamo.FBX。");
            }

            try
            {
                List<VatMeshPart> bodyParts = CollectVatParts(instance, false);
                if (bodyParts.Count == 0)
                {
                    throw new InvalidOperationException("mixamo.FBX 内没有找到用于 VAT 烘焙的 SkinnedMeshRenderer 或 MeshFilter。");
                }

                AnimationClip[] clips = LoadAnimationClips();
                List<MassiveAnimationClipInfo> clipInfos = new List<MassiveAnimationClipInfo>();
                List<Vector3[]> positions = new List<Vector3[]>();
                List<Vector3[]> normals = new List<Vector3[]>();
                Bounds bounds = new Bounds(Vector3.zero, Vector3.one);
                bool hasBounds = false;
                int startFrame = 0;

                AnimationMode.StartAnimationMode();
                try
                {
                    foreach (AnimationClip clip in clips)
                    {
                        int frameCount = Mathf.Max(2, Mathf.RoundToInt(clip.length * SampleRate) + 1);
                        clipInfos.Add(new MassiveAnimationClipInfo
                        {
                            clipName = clip.name,
                            duration = Mathf.Max(0.033f, clip.length),
                            frameCount = frameCount,
                            startFrame = startFrame
                        });

                        for (int frame = 0; frame < frameCount; frame++)
                        {
                            float time = Mathf.Min(clip.length, frame / SampleRate);
                            AnimationMode.SampleAnimationClip(instance, clip, time);
                            BakeVatFrame(instance.transform, bodyParts, out Vector3[] framePositions, out Vector3[] frameNormals, out Bounds frameBounds);
                            positions.Add(framePositions);
                            normals.Add(frameNormals);

                            if (!hasBounds)
                            {
                                bounds = frameBounds;
                                hasBounds = true;
                            }
                            else
                            {
                                bounds.Encapsulate(frameBounds);
                            }

                        }

                        startFrame += frameCount;
                    }
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }

                int vertexCount = renderMesh.vertexCount;
                CalculateVatTextureLayout(vertexCount, positions.Count, out int textureWidth, out int textureHeight, out int rowsPerFrame);
                Texture2D positionTexture = CreateVatTexture($"{PathWithoutExtension(assetPath)}_Position.asset", positions, vertexCount, textureWidth, textureHeight, rowsPerFrame, true);
                Texture2D normalTexture = CreateVatTexture($"{PathWithoutExtension(assetPath)}_Normal.asset", normals, vertexCount, textureWidth, textureHeight, rowsPerFrame, false);
                VatAnimationData data = LoadOrCreate<VatAnimationData>(assetPath);
                data.positionTexture = positionTexture;
                data.normalTexture = normalTexture;
                data.clips = clipInfos.ToArray();
                data.vertexCount = vertexCount;
                data.textureWidth = textureWidth;
                data.textureHeight = textureHeight;
                data.rowsPerFrame = rowsPerFrame;
                data.sampleRate = SampleRate;
                data.localBounds = ExpandBounds(bounds, 0.5f);
                EditorUtility.SetDirty(data);
                return AssetDatabase.LoadAssetAtPath<VatAnimationData>(assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// 烘焙 GPU Skinning 需要的每帧每骨骼矩阵贴图。
        /// </summary>
        private static GpuSkinningAnimationData BakeGpuSkinningAnimation(GameObject sourcePrefab, Mesh renderMesh, string assetPath)
        {
            GameObject instance = PrefabUtility.InstantiatePrefab(sourcePrefab) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException("无法实例化 mixamo.FBX。");
            }

            try
            {
                SkinnedMeshRenderer[] bodyRenderers = CollectGpuBodyRenderers(instance);
                if (bodyRenderers.Length == 0)
                {
                    throw new InvalidOperationException("mixamo.FBX 内没有找到 SkinnedMeshRenderer。");
                }

                SkinnedMeshRenderer referenceRenderer = bodyRenderers[0];
                GpuBoneSet boneSet = BuildGpuBoneSet(bodyRenderers);
                Transform[] bones = boneSet.bones;
                Matrix4x4[] bindposes = boneSet.bindposes;
                AnimationClip[] clips = LoadAnimationClips();
                List<MassiveAnimationClipInfo> clipInfos = new List<MassiveAnimationClipInfo>();
                List<Matrix4x4> matrices = new List<Matrix4x4>();
                Bounds bounds = renderMesh.bounds;
                int startFrame = 0;

                AnimationMode.StartAnimationMode();
                try
                {
                    foreach (AnimationClip clip in clips)
                    {
                        int frameCount = Mathf.Max(2, Mathf.RoundToInt(clip.length * SampleRate) + 1);
                        clipInfos.Add(new MassiveAnimationClipInfo
                        {
                            clipName = clip.name,
                            duration = Mathf.Max(0.033f, clip.length),
                            frameCount = frameCount,
                            startFrame = startFrame
                        });

                        for (int frame = 0; frame < frameCount; frame++)
                        {
                            float time = Mathf.Min(clip.length, frame / SampleRate);
                            AnimationMode.SampleAnimationClip(instance, clip, time);
                            for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                            {
                                Matrix4x4 matrix = referenceRenderer.transform.worldToLocalMatrix * bones[boneIndex].localToWorldMatrix * bindposes[boneIndex];
                                matrices.Add(matrix);
                            }
                        }

                        startFrame += frameCount;
                    }
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }

                Texture2D boneTexture = CreateBoneTexture($"{PathWithoutExtension(assetPath)}_BoneTexture.asset", matrices, bones.Length);
                GpuSkinningAnimationData data = LoadOrCreate<GpuSkinningAnimationData>(assetPath);
                data.boneTexture = boneTexture;
                data.clips = clipInfos.ToArray();
                data.boneCount = bones.Length;
                data.textureWidth = boneTexture.width;
                data.textureHeight = boneTexture.height;
                data.sampleRate = SampleRate;
                data.localBounds = ExpandBounds(bounds, 0.5f);
                EditorUtility.SetDirty(data);
                return AssetDatabase.LoadAssetAtPath<GpuSkinningAnimationData>(assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// 为 VAT 生成运行时 mesh，并把顶点下标写入 UV2。
        /// </summary>
        private static Mesh CreateVatRuntimeMesh(GameObject sourcePrefab, bool accessory, string assetPath)
        {
            List<VatMeshPart> parts = CollectVatParts(sourcePrefab, accessory);
            if (parts.Count == 0)
            {
                if (accessory)
                {
                    Debug.LogWarning($"没有找到名称包含 {AccessoryKeyword} 的 VAT 配件 mesh，本次 Demo 会跳过配件 pass。");
                    return null;
                }

                throw new InvalidOperationException($"模型 {sourcePrefab.name} 中没有找到可用于 VAT 的 mesh。");
            }

            Mesh mesh = BuildCombinedMesh(sourcePrefab.transform, parts);
            mesh.name = accessory ? "Vat_PiaodaiMesh" : "Vat_BodyMesh";
            FillVertexIndexUv(mesh);
            mesh.UploadMeshData(false);
            SaveMesh(mesh, assetPath);
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }

        /// <summary>
        /// 从未蒙皮模型中提取 piaodai 节点，作为第一版模型内配件显隐测试网格。
        /// </summary>
        private static Mesh CreateRigidAccessoryRuntimeMesh(GameObject sourcePrefab, string assetPath, string meshName)
        {
            List<VatMeshPart> parts = CollectVatParts(sourcePrefab, true);
            if (parts.Count == 0)
            {
                Debug.LogWarning($"没有在 {sourcePrefab.name} 中找到名称包含 {AccessoryKeyword} 的配件 mesh，本次 Demo 会跳过配件 pass。");
                return null;
            }

            Mesh mesh = BuildCombinedMesh(sourcePrefab.transform, parts);
            mesh.name = meshName;
            FillVertexIndexUv(mesh);
            FillRigidSkinningChannels(mesh);
            mesh.UploadMeshData(false);
            SaveMesh(mesh, assetPath);
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }

        /// <summary>
        /// 收集 VAT 需要的 mesh 部件；配件按节点名包含 piaodai 识别。
        /// </summary>
        private static List<VatMeshPart> CollectVatParts(GameObject root, bool accessory)
        {
            List<VatMeshPart> parts = new List<VatMeshPart>();
            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer renderer in skinnedRenderers)
            {
                bool isAccessory = IsAccessoryNode(renderer.transform);
                if (isAccessory == accessory && renderer.sharedMesh != null)
                {
                    parts.Add(new VatMeshPart { transform = renderer.transform, skinnedRenderer = renderer });
                }
            }

            MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter meshFilter in meshFilters)
            {
                bool isAccessory = IsAccessoryNode(meshFilter.transform);
                if (isAccessory == accessory && meshFilter.sharedMesh != null)
                {
                    parts.Add(new VatMeshPart { transform = meshFilter.transform, meshFilter = meshFilter });
                }
            }

            return parts;
        }

        /// <summary>
        /// 判断当前节点或父节点是否属于 piaodai 配件。
        /// </summary>
        private static bool IsAccessoryNode(Transform transform)
        {
            while (transform != null)
            {
                if (transform.name.IndexOf(AccessoryKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                transform = transform.parent;
            }

            return false;
        }

        /// <summary>
        /// 把多个 VAT 部件合并成一个稳定顶点顺序的运行时 mesh。
        /// </summary>
        private static Mesh BuildCombinedMesh(Transform root, List<VatMeshPart> parts)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> indices = new List<int>();

            foreach (VatMeshPart part in parts)
            {
                Mesh mesh = part.SharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                int vertexOffset = vertices.Count;
                Matrix4x4 localToRoot = root.worldToLocalMatrix * part.transform.localToWorldMatrix;
                Matrix4x4 normalToRoot = localToRoot.inverse.transpose;
                Vector3[] sourceVertices = mesh.vertices;
                Vector3[] sourceNormals = mesh.normals;
                Vector2[] sourceUvs = mesh.uv;

                for (int i = 0; i < sourceVertices.Length; i++)
                {
                    vertices.Add(localToRoot.MultiplyPoint3x4(sourceVertices[i]));
                    normals.Add(sourceNormals != null && sourceNormals.Length == sourceVertices.Length ? normalToRoot.MultiplyVector(sourceNormals[i]).normalized : Vector3.up);
                    uvs.Add(sourceUvs != null && sourceUvs.Length == sourceVertices.Length ? sourceUvs[i] : Vector2.zero);
                }

                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    int[] sourceIndices = mesh.GetIndices(subMesh);
                    for (int i = 0; i < sourceIndices.Length; i++)
                    {
                        indices.Add(vertexOffset + sourceIndices[i]);
                    }
                }
            }

            Mesh combined = new Mesh();
            combined.indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            combined.SetVertices(vertices);
            combined.SetNormals(normals);
            combined.SetUVs(0, uvs);
            combined.SetTriangles(indices, 0);
            combined.RecalculateBounds();
            return combined;
        }

        /// <summary>
        /// 给 mesh 的 UV2 写入稳定顶点序号，供 VAT shader 根据顶点找到动画贴图列。
        /// </summary>
        private static void FillVertexIndexUv(Mesh mesh)
        {
            Vector2[] uv2 = new Vector2[mesh.vertexCount];
            for (int i = 0; i < uv2.Length; i++)
            {
                uv2[i] = new Vector2(i, 0f);
            }

            mesh.uv2 = uv2;
        }

        /// <summary>
        /// 给刚性配件补一组默认骨骼权重，避免 GPU Skinning shader 读取空 BLENDWEIGHTS/BLENDINDICES。
        /// </summary>
        private static void FillRigidSkinningChannels(Mesh mesh)
        {
            BoneWeight[] boneWeights = new BoneWeight[mesh.vertexCount];
            Vector4[] boneIndicesUv = new Vector4[mesh.vertexCount];
            Vector4[] boneWeightsUv = new Vector4[mesh.vertexCount];
            for (int i = 0; i < boneWeights.Length; i++)
            {
                boneWeights[i] = new BoneWeight
                {
                    boneIndex0 = 0,
                    weight0 = 1f
                };
                boneIndicesUv[i] = Vector4.zero;
                boneWeightsUv[i] = new Vector4(1f, 0f, 0f, 0f);
            }

            mesh.boneWeights = boneWeights;
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.SetUVs(2, boneIndicesUv);
            mesh.SetUVs(3, boneWeightsUv);
        }

        /// <summary>
        /// 在当前动画采样姿态下烘焙一帧 VAT 顶点和法线。
        /// </summary>
        private static void BakeVatFrame(Transform root, List<VatMeshPart> parts, out Vector3[] positions, out Vector3[] normals, out Bounds bounds)
        {
            List<Vector3> positionList = new List<Vector3>();
            List<Vector3> normalList = new List<Vector3>();
            bool hasBounds = false;
            bounds = new Bounds(Vector3.zero, Vector3.one);

            foreach (VatMeshPart part in parts)
            {
                if (part.skinnedRenderer != null)
                {
                    Mesh bakedMesh = new Mesh();
                    part.skinnedRenderer.BakeMesh(bakedMesh, true);
                    AppendFrameMesh(root, part.skinnedRenderer.transform, bakedMesh, positionList, normalList, ref bounds, ref hasBounds);
                    UnityEngine.Object.DestroyImmediate(bakedMesh);
                }
                else if (part.meshFilter != null && part.meshFilter.sharedMesh != null)
                {
                    AppendFrameMesh(root, part.meshFilter.transform, part.meshFilter.sharedMesh, positionList, normalList, ref bounds, ref hasBounds);
                }
            }

            positions = positionList.ToArray();
            normals = normalList.ToArray();
        }

        /// <summary>
        /// 把单个 mesh 当前姿态追加到 VAT 帧数据。
        /// </summary>
        private static void AppendFrameMesh(Transform root, Transform meshTransform, Mesh mesh, List<Vector3> positions, List<Vector3> normals, ref Bounds bounds, ref bool hasBounds)
        {
            Matrix4x4 localToRoot = root.worldToLocalMatrix * meshTransform.localToWorldMatrix;
            Matrix4x4 normalToRoot = localToRoot.inverse.transpose;
            Vector3[] sourceVertices = mesh.vertices;
            Vector3[] sourceNormals = mesh.normals;

            for (int i = 0; i < sourceVertices.Length; i++)
            {
                Vector3 position = localToRoot.MultiplyPoint3x4(sourceVertices[i]);
                Vector3 normal = sourceNormals != null && sourceNormals.Length == sourceVertices.Length ? normalToRoot.MultiplyVector(sourceNormals[i]).normalized : Vector3.up;
                positions.Add(position);
                normals.Add(normal);

                if (!hasBounds)
                {
                    bounds = new Bounds(position, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(position);
                }
            }
        }

        /// <summary>
        /// 为方案 C 生成完整单 mesh：合并身体所有 SkinnedMeshRenderer，并把 piaodai 作为 mesh 内部件写入同一份顶点数据。
        /// </summary>
        private static Mesh CreateGpuCombinedRuntimeMesh(GameObject bodyPrefab, GameObject accessoryPrefab, string assetPath)
        {
            SkinnedMeshRenderer[] bodyRenderers = CollectGpuBodyRenderers(bodyPrefab);

            if (bodyRenderers.Length == 0)
            {
                throw new InvalidOperationException($"模型 {bodyPrefab.name} 中没有找到可用于 GPU Skinning 的身体 SkinnedMeshRenderer。");
            }

            SkinnedMeshRenderer referenceRenderer = bodyRenderers[0];
            GpuBoneSet boneSet = BuildGpuBoneSet(bodyRenderers);

            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> indices = new List<int>();
            List<BoneWeight> boneWeights = new List<BoneWeight>();
            List<Vector4> boneIndicesUv = new List<Vector4>();
            List<Vector4> boneWeightsUv = new List<Vector4>();

            foreach (SkinnedMeshRenderer renderer in bodyRenderers)
            {
                AppendSkinnedRendererToGpuMesh(
                    referenceRenderer,
                    boneSet.lookup,
                    renderer,
                    BodyUvPartIndex,
                    vertices,
                    normals,
                    uvs,
                    indices,
                    boneWeights,
                    boneIndicesUv,
                    boneWeightsUv);
            }

            List<VatMeshPart> accessoryParts = CollectVatParts(accessoryPrefab, true);
            if (accessoryParts.Count == 0)
            {
                Debug.LogWarning($"没有在 {accessoryPrefab.name} 中找到名称包含 {AccessoryKeyword} 的配件 mesh，方案 C 会生成无配件的完整身体 mesh。");
            }

            foreach (VatMeshPart part in accessoryParts)
            {
                AppendRigidAccessoryToGpuMesh(
                    referenceRenderer,
                    bodyPrefab.transform,
                    accessoryPrefab.transform,
                    part,
                    vertices,
                    normals,
                    uvs,
                    indices,
                    boneWeights,
                    boneIndicesUv,
                    boneWeightsUv);
            }

            Mesh combined = new Mesh
            {
                name = "GpuSkinning_BodyMesh",
                indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16
            };
            combined.SetVertices(vertices);
            combined.SetNormals(normals);
            combined.SetUVs(0, uvs);
            combined.SetUVs(2, boneIndicesUv);
            combined.SetUVs(3, boneWeightsUv);
            combined.SetTriangles(indices, 0);
            combined.boneWeights = boneWeights.ToArray();
            combined.bindposes = boneSet.bindposes;
            combined.RecalculateBounds();
            combined.UploadMeshData(false);
            SaveMesh(combined, assetPath);

            Debug.Log($"方案 C 已生成单 mesh：身体 renderer {bodyRenderers.Length} 个，配件部件 {accessoryParts.Count} 个，顶点 {combined.vertexCount}。");
            return AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }

        /// <summary>
        /// 收集方案 C 的全部身体蒙皮 renderer，避免只取第一个 renderer 导致漏掉 Beta_Surface002 等子部件。
        /// </summary>
        private static SkinnedMeshRenderer[] CollectGpuBodyRenderers(GameObject root)
        {
            return root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null && !IsAccessoryNode(renderer.transform))
                .OrderBy(renderer => renderer.transform.GetSiblingIndex())
                .ThenBy(renderer => renderer.name, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// 合并所有身体 renderer 用到的骨骼和 bindpose，避免 Beta_Surface002 等子网格引用参考 renderer 未包含的骨骼。
        /// </summary>
        private static GpuBoneSet BuildGpuBoneSet(SkinnedMeshRenderer[] renderers)
        {
            List<Transform> bones = new List<Transform>();
            List<Matrix4x4> bindposes = new List<Matrix4x4>();
            Dictionary<Transform, int> lookup = new Dictionary<Transform, int>();

            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Transform[] rendererBones = renderer.bones;
                Matrix4x4[] rendererBindposes = mesh.bindposes;
                if (rendererBones.Length != rendererBindposes.Length)
                {
                    throw new InvalidOperationException($"mesh {mesh.name} 的骨骼数量 {rendererBones.Length} 和 bindpose 数量 {rendererBindposes.Length} 不一致，无法合并 GPU Skinning 骨骼表。");
                }

                for (int i = 0; i < rendererBones.Length; i++)
                {
                    Transform bone = rendererBones[i];
                    if (bone == null || lookup.ContainsKey(bone))
                    {
                        continue;
                    }

                    lookup.Add(bone, bones.Count);
                    bones.Add(bone);
                    bindposes.Add(rendererBindposes[i]);
                }
            }

            if (bones.Count == 0)
            {
                throw new InvalidOperationException("方案 C 没有收集到有效骨骼，无法生成 GPU Skinning 数据。");
            }

            return new GpuBoneSet
            {
                bones = bones.ToArray(),
                bindposes = bindposes.ToArray(),
                lookup = lookup
            };
        }

        /// <summary>
        /// 把一个蒙皮 renderer 追加进方案 C 的单 mesh，并把骨骼权重映射到统一骨骼数组。
        /// </summary>
        private static void AppendSkinnedRendererToGpuMesh(
            SkinnedMeshRenderer referenceRenderer,
            Dictionary<Transform, int> boneLookup,
            SkinnedMeshRenderer renderer,
            int uvPartIndex,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> indices,
            List<BoneWeight> boneWeights,
            List<Vector4> boneIndicesUv,
            List<Vector4> boneWeightsUv)
        {
            Mesh mesh = renderer.sharedMesh;
            if (mesh == null)
            {
                return;
            }

            BoneWeight[] sourceBoneWeights = mesh.boneWeights;
            if (sourceBoneWeights == null || sourceBoneWeights.Length != mesh.vertexCount)
            {
                throw new InvalidOperationException($"mesh {mesh.name} 没有有效骨骼权重，无法合入方案 C 单 mesh。");
            }

            int vertexOffset = vertices.Count;
            Matrix4x4 localToReference = referenceRenderer.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Matrix4x4 normalToReference = localToReference.inverse.transpose;
            Vector3[] sourceVertices = mesh.vertices;
            Vector3[] sourceNormals = mesh.normals;
            Vector2[] sourceUvs = mesh.uv;

            for (int i = 0; i < sourceVertices.Length; i++)
            {
                BoneWeight remappedWeight = RemapBoneWeight(sourceBoneWeights[i], renderer.bones, boneLookup, mesh.name);
                vertices.Add(localToReference.MultiplyPoint3x4(sourceVertices[i]));
                normals.Add(sourceNormals != null && sourceNormals.Length == sourceVertices.Length ? normalToReference.MultiplyVector(sourceNormals[i]).normalized : Vector3.up);
                uvs.Add(ResolvePartUv(sourceUvs, i, uvPartIndex));
                boneWeights.Add(remappedWeight);
                boneIndicesUv.Add(new Vector4(remappedWeight.boneIndex0, remappedWeight.boneIndex1, remappedWeight.boneIndex2, remappedWeight.boneIndex3));
                boneWeightsUv.Add(new Vector4(remappedWeight.weight0, remappedWeight.weight1, remappedWeight.weight2, remappedWeight.weight3));
            }

            AppendMeshIndices(mesh, vertexOffset, indices);
        }

        /// <summary>
        /// 把未蒙皮配件作为刚性部件追加进方案 C 单 mesh，并写入单独 UV0.x 区间供 shader 识别。
        /// </summary>
        private static void AppendRigidAccessoryToGpuMesh(
            SkinnedMeshRenderer referenceRenderer,
            Transform bodyRoot,
            Transform accessoryRoot,
            VatMeshPart part,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> indices,
            List<BoneWeight> boneWeights,
            List<Vector4> boneIndicesUv,
            List<Vector4> boneWeightsUv)
        {
            Mesh mesh = part.SharedMesh;
            if (mesh == null)
            {
                return;
            }

            int vertexOffset = vertices.Count;
            Matrix4x4 partLocalToAccessoryRoot = accessoryRoot.worldToLocalMatrix * part.transform.localToWorldMatrix;
            Matrix4x4 localToReference = referenceRenderer.transform.worldToLocalMatrix * bodyRoot.localToWorldMatrix * partLocalToAccessoryRoot;
            Matrix4x4 normalToReference = localToReference.inverse.transpose;
            Vector3[] sourceVertices = mesh.vertices;
            Vector3[] sourceNormals = mesh.normals;
            Vector2[] sourceUvs = mesh.uv;

            for (int i = 0; i < sourceVertices.Length; i++)
            {
                vertices.Add(localToReference.MultiplyPoint3x4(sourceVertices[i]));
                normals.Add(sourceNormals != null && sourceNormals.Length == sourceVertices.Length ? normalToReference.MultiplyVector(sourceNormals[i]).normalized : Vector3.up);
                uvs.Add(ResolvePartUv(sourceUvs, i, PiaodaiUvPartIndex));
                boneWeights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1f });
                boneIndicesUv.Add(Vector4.zero);
                boneWeightsUv.Add(new Vector4(1f, 0f, 0f, 0f));
            }

            AppendMeshIndices(mesh, vertexOffset, indices);
        }

        /// <summary>
        /// 将源 mesh 的所有 subMesh 索引追加到合并后的单 subMesh 中，Demo 阶段统一使用一个材质绘制。
        /// </summary>
        private static void AppendMeshIndices(Mesh mesh, int vertexOffset, List<int> indices)
        {
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] sourceIndices = mesh.GetIndices(subMesh);
                for (int i = 0; i < sourceIndices.Length; i++)
                {
                    indices.Add(vertexOffset + sourceIndices[i]);
                }
            }
        }

        /// <summary>
        /// 把源 renderer 的骨骼权重重定向到参考 renderer 的骨骼数组。
        /// </summary>
        private static BoneWeight RemapBoneWeight(BoneWeight source, Transform[] sourceBones, Dictionary<Transform, int> boneLookup, string meshName)
        {
            BoneWeight result = new BoneWeight();
            result.boneIndex0 = RemapBoneIndex(source.boneIndex0, sourceBones, boneLookup, meshName);
            result.boneIndex1 = RemapBoneIndex(source.boneIndex1, sourceBones, boneLookup, meshName);
            result.boneIndex2 = RemapBoneIndex(source.boneIndex2, sourceBones, boneLookup, meshName);
            result.boneIndex3 = RemapBoneIndex(source.boneIndex3, sourceBones, boneLookup, meshName);
            result.weight0 = source.weight0;
            result.weight1 = source.weight1;
            result.weight2 = source.weight2;
            result.weight3 = source.weight3;
            return result;
        }

        /// <summary>
        /// 查找单个骨骼在参考骨骼数组中的下标。
        /// </summary>
        private static int RemapBoneIndex(int sourceIndex, Transform[] sourceBones, Dictionary<Transform, int> boneLookup, string meshName)
        {
            if (sourceIndex < 0 || sourceIndex >= sourceBones.Length || sourceBones[sourceIndex] == null)
            {
                return 0;
            }

            if (boneLookup.TryGetValue(sourceBones[sourceIndex], out int remappedIndex))
            {
                return remappedIndex;
            }

            throw new InvalidOperationException($"mesh {meshName} 使用了参考骨骼数组中不存在的骨骼：{sourceBones[sourceIndex].name}。");
        }

        /// <summary>
        /// UV0.x 直接作为部件区间：身体写入 0~1，配件 1/2/3 分别写入 1~2、2~3、3~4。
        /// </summary>
        private static Vector2 ResolvePartUv(Vector2[] sourceUvs, int vertexIndex, int uvPartIndex)
        {
            Vector2 uv = sourceUvs != null && sourceUvs.Length > vertexIndex ? sourceUvs[vertexIndex] : Vector2.zero;
            int partIndex = Mathf.Clamp(uvPartIndex, 0, 3);
            float normalizedX = Mathf.Repeat(uv.x, 1f);
            return new Vector2(normalizedX + partIndex, uv.y);
        }

        /// <summary>
        /// 将 Unity 蒙皮权重复制到普通 UV 通道，避免 shader 依赖平台相关的 BLENDWEIGHTS/BLENDINDICES 语义。
        /// </summary>
        private static void FillGpuSkinningChannels(Mesh mesh)
        {
            BoneWeight[] boneWeights = mesh.boneWeights;
            if (boneWeights == null || boneWeights.Length != mesh.vertexCount)
            {
                throw new InvalidOperationException($"mesh {mesh.name} 没有有效骨骼权重，无法生成 GPU Skinning 运行时 mesh。");
            }

            Vector4[] boneIndicesUv = new Vector4[mesh.vertexCount];
            Vector4[] boneWeightsUv = new Vector4[mesh.vertexCount];
            for (int i = 0; i < boneWeights.Length; i++)
            {
                BoneWeight weight = boneWeights[i];
                boneIndicesUv[i] = new Vector4(weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3);
                boneWeightsUv[i] = new Vector4(weight.weight0, weight.weight1, weight.weight2, weight.weight3);
            }

            mesh.SetUVs(2, boneIndicesUv);
            mesh.SetUVs(3, boneWeightsUv);
        }

        /// <summary>
        /// 生成 Demo 场景，并把渲染器组件挂到场景对象上。
        /// </summary>
        private static void CreateDemoScene(
            string scenePath,
            MassiveCharacterDemoMode mode,
            Mesh bodyMesh,
            Mesh accessoryMesh,
            Material bodyMaterial,
            Material accessoryMaterial,
            GpuSkinningAnimationData gpuData,
            VatAnimationData vatData,
            bool animateAccessory)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject cameraObject = new GameObject("Main Camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 24f, -42f);
            cameraObject.transform.rotation = Quaternion.Euler(58f, 0f, 0f);
            camera.fieldOfView = 45f;

            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(45f, 35f, 0f);

            GameObject rendererObject = new GameObject(mode == MassiveCharacterDemoMode.Vat ? "VAT Massive Character Renderer" : "GPU Skinning Massive Character Renderer");
            MassiveCharacterDemoRenderer renderer = rendererObject.AddComponent<MassiveCharacterDemoRenderer>();
            renderer.demoMode = mode;
            renderer.bodyMesh = bodyMesh;
            renderer.accessoryMesh = accessoryMesh;
            renderer.bodyMaterial = bodyMaterial;
            renderer.accessoryMaterial = accessoryMaterial;
            renderer.gpuSkinningData = gpuData;
            renderer.vatData = vatData;
            renderer.instanceCount = 1000;
            renderer.gridColumns = 40;
            renderer.spacing = 1.8f;
            renderer.animationMode = MassiveAnimationMode.Random;
            renderer.movementMode = MassiveMovementMode.Static;
            renderer.randomAccessory = true;
            renderer.showAccessory = mode == MassiveCharacterDemoMode.GpuSkinning || accessoryMesh != null;
            renderer.animateAccessory = animateAccessory;

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Reference Ground";
            ground.transform.position = new Vector3(0f, -0.02f, 35f);
            ground.transform.localScale = new Vector3(14f, 1f, 14f);

            EditorSceneManager.SaveScene(scene, scenePath);
        }

        /// <summary>
        /// 从模型里查找身体或 piaodai 配件对应的 SkinnedMeshRenderer。
        /// </summary>
        private static SkinnedMeshRenderer FindSkinnedMeshRenderer(GameObject root, bool accessory)
        {
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer fallback = null;
            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                bool isAccessory = IsAccessoryNode(renderer.transform);
                if (accessory == isAccessory)
                {
                    return renderer;
                }

                if (!isAccessory && fallback == null)
                {
                    fallback = renderer;
                }
            }

            return accessory ? null : fallback;
        }

        /// <summary>
        /// 从模型里查找身体或 piaodai 配件对应的 Mesh。
        /// </summary>
        private static Mesh FindSourceMesh(GameObject root, bool accessory)
        {
            return FindSourceMesh(root, accessory, false);
        }

        /// <summary>
        /// 从模型里查找身体或 piaodai 配件对应的 Mesh，可选择缺失时不抛错。
        /// </summary>
        private static Mesh FindSourceMesh(GameObject root, bool accessory, bool optional)
        {
            SkinnedMeshRenderer skinned = FindSkinnedMeshRenderer(root, accessory);
            if (skinned != null)
            {
                return skinned.sharedMesh;
            }

            MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter meshFilter in meshFilters)
            {
                bool isAccessory = IsAccessoryNode(meshFilter.transform);
                if (accessory == isAccessory)
                {
                    return meshFilter.sharedMesh;
                }
            }

            if (accessory)
            {
                Debug.LogWarning($"没有找到名称包含 {AccessoryKeyword} 的配件 mesh，本次 Demo 会跳过配件 pass。");
                return null;
            }

            if (optional)
            {
                return null;
            }

            throw new InvalidOperationException($"模型 {root.name} 中没有找到身体 mesh。");
        }

        /// <summary>
        /// 加载三个 Mixamo 动画 clip。
        /// </summary>
        private static AnimationClip[] LoadAnimationClips()
        {
            AnimationClip[] clips = new AnimationClip[ClipPaths.Length];
            for (int i = 0; i < ClipPaths.Length; i++)
            {
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(ClipPaths[i]);
                AnimationClip clip = assets.OfType<AnimationClip>().FirstOrDefault(x => string.Equals(x.name, ClipNames[i], StringComparison.OrdinalIgnoreCase))
                    ?? assets.OfType<AnimationClip>().FirstOrDefault(x => !x.name.StartsWith("__", StringComparison.Ordinal));
                if (clip == null)
                {
                    throw new InvalidOperationException($"没有在 {ClipPaths[i]} 中找到动画 clip。");
                }

                clips[i] = clip;
            }

            return clips;
        }

        /// <summary>
        /// 计算 VAT 贴图布局；顶点数超过最大贴图宽度时，每帧拆成多行存储。
        /// </summary>
        private static void CalculateVatTextureLayout(int vertexCount, int frameCount, out int textureWidth, out int textureHeight, out int rowsPerFrame)
        {
            int maxTextureSize = Mathf.Max(1, SystemInfo.maxTextureSize);
            textureWidth = Mathf.Min(Mathf.Max(1, vertexCount), maxTextureSize);
            rowsPerFrame = Mathf.CeilToInt(vertexCount / (float)textureWidth);
            textureHeight = Mathf.Max(1, frameCount * rowsPerFrame);

            if (textureHeight > maxTextureSize)
            {
                throw new InvalidOperationException($"VAT 贴图高度 {textureHeight} 超过当前设备最大尺寸 {maxTextureSize}。请降低模型顶点数、减少动画帧数或改用 Texture2DArray/多贴图分片。");
            }
        }

        /// <summary>
        /// 把每帧顶点数据写入 VAT 贴图资产，支持单帧多行布局。
        /// </summary>
        private static Texture2D CreateVatTexture(string assetPath, List<Vector3[]> frames, int vertexCount, int textureWidth, int textureHeight, int rowsPerFrame, bool position)
        {
            Texture2D texture = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBAFloat, false, true)
            {
                name = position ? "VatPositionTexture" : "VatNormalTexture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] colors = new Color[textureWidth * textureHeight];
            for (int frame = 0; frame < frames.Count; frame++)
            {
                Vector3[] values = frames[frame];
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    Vector3 value = vertex < values.Length ? values[vertex] : Vector3.zero;
                    int x = vertex % textureWidth;
                    int y = frame * rowsPerFrame + vertex / textureWidth;
                    colors[y * textureWidth + x] = new Color(value.x, value.y, value.z, 1f);
                }
            }

            texture.SetPixels(colors);
            texture.Apply(false, false);
            SaveAsset(texture, assetPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// 把骨骼矩阵按 3 行 float4 写入纹理资产。
        /// </summary>
        private static Texture2D CreateBoneTexture(string assetPath, List<Matrix4x4> matrices, int boneCount)
        {
            int pixelCount = matrices.Count * 3;
            int width = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(pixelCount)));
            int height = Mathf.CeilToInt(pixelCount / (float)width);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                name = "GpuSkinningBoneTexture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] colors = new Color[width * height];
            for (int i = 0; i < matrices.Count; i++)
            {
                Matrix4x4 matrix = matrices[i];
                int pixel = i * 3;
                colors[pixel] = RowToColor(matrix, 0);
                colors[pixel + 1] = RowToColor(matrix, 1);
                colors[pixel + 2] = RowToColor(matrix, 2);
            }

            texture.SetPixels(colors);
            texture.Apply(false, false);
            SaveAsset(texture, assetPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// 把矩阵指定行转换成纹理颜色。
        /// </summary>
        private static Color RowToColor(Matrix4x4 matrix, int row)
        {
            return new Color(matrix[row, 0], matrix[row, 1], matrix[row, 2], matrix[row, 3]);
        }

        /// <summary>
        /// 创建或覆盖材质资产。
        /// </summary>
        private static Material CreateMaterial(string assetPath, string shaderName, Color color)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new InvalidOperationException($"找不到 Shader：{shaderName}");
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, assetPath);
            }
            else
            {
                material.shader = shader;
            }

            material.enableInstancing = true;
            material.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// 保存 mesh 资产，已存在则覆盖内容。
        /// </summary>
        private static void SaveMesh(Mesh mesh, string assetPath)
        {
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, assetPath);
            }
            else
            {
                EditorUtility.CopySerialized(mesh, existing);
                EditorUtility.SetDirty(existing);
            }
        }

        /// <summary>
        /// 保存普通 Unity 资产，已存在则覆盖内容。
        /// </summary>
        private static void SaveAsset(UnityEngine.Object asset, string assetPath)
        {
            UnityEngine.Object existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(asset, assetPath);
            }
            else
            {
                EditorUtility.CopySerialized(asset, existing);
                EditorUtility.SetDirty(existing);
            }
        }

        /// <summary>
        /// 加载已有 ScriptableObject，不存在则创建。
        /// </summary>
        private static T LoadOrCreate<T>(string assetPath) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null)
            {
                return asset;
            }

            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, assetPath);
            return asset;
        }

        /// <summary>
        /// 按路径加载资源，缺失时给出清晰错误。
        /// </summary>
        private static T LoadAsset<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                throw new FileNotFoundException($"资源不存在：{path}");
            }

            return asset;
        }

        /// <summary>
        /// 去掉扩展名，方便拼接附属贴图资产路径。
        /// </summary>
        private static string PathWithoutExtension(string assetPath)
        {
            return Path.Combine(Path.GetDirectoryName(assetPath) ?? string.Empty, Path.GetFileNameWithoutExtension(assetPath)).Replace("\\", "/");
        }

        /// <summary>
        /// 给包围盒留出一定余量，避免动画极值被裁掉。
        /// </summary>
        private static Bounds ExpandBounds(Bounds bounds, float padding)
        {
            bounds.Expand(Vector3.one * padding);
            return bounds;
        }
    }
}
