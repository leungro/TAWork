using UnityEngine;

namespace MassiveCharacters
{
    /// <summary>
    /// 海量角色 Demo 共用的动画片段描述。
    /// </summary>
    [System.Serializable]
    public sealed class MassiveAnimationClipInfo
    {
        public string clipName;
        public float duration;
        public int frameCount;
        public int startFrame;
    }

    /// <summary>
    /// 方案 C 使用的骨骼矩阵动画数据。
    /// </summary>
    public sealed class GpuSkinningAnimationData : ScriptableObject
    {
        public Texture2D boneTexture;
        public MassiveAnimationClipInfo[] clips;
        public int boneCount;
        public int textureWidth;
        public int textureHeight;
        public float sampleRate = 30f;
        public Bounds localBounds = new Bounds(Vector3.up, Vector3.one * 2f);

        /// <summary>
        /// 返回所有动画帧数量，用于运行时校验烘焙数据。
        /// </summary>
        public int TotalFrameCount
        {
            get
            {
                if (clips == null || clips.Length == 0)
                {
                    return 0;
                }

                int maxFrame = 0;
                for (int i = 0; i < clips.Length; i++)
                {
                    MassiveAnimationClipInfo clip = clips[i];
                    if (clip != null)
                    {
                        maxFrame = Mathf.Max(maxFrame, clip.startFrame + clip.frameCount);
                    }
                }

                return maxFrame;
            }
        }
    }

    /// <summary>
    /// 方案 D 使用的顶点动画贴图数据。
    /// </summary>
    public sealed class VatAnimationData : ScriptableObject
    {
        public Texture2D positionTexture;
        public Texture2D normalTexture;
        public MassiveAnimationClipInfo[] clips;
        public int vertexCount;
        public int textureWidth;
        public int textureHeight;
        public int rowsPerFrame = 1;
        public float sampleRate = 30f;
        public Bounds localBounds = new Bounds(Vector3.up, Vector3.one * 2f);

        /// <summary>
        /// 返回所有动画帧数量，用于运行时估算贴图数据规模。
        /// </summary>
        public int TotalFrameCount
        {
            get
            {
                if (clips == null || clips.Length == 0)
                {
                    return 0;
                }

                int maxFrame = 0;
                for (int i = 0; i < clips.Length; i++)
                {
                    MassiveAnimationClipInfo clip = clips[i];
                    if (clip != null)
                    {
                        maxFrame = Mathf.Max(maxFrame, clip.startFrame + clip.frameCount);
                    }
                }

                return maxFrame;
            }
        }
    }
}
