# 海量角色功能分析与 Demo 使用说明

日期：2026-05-25  
适用范围：`Assets/MassiveCharacters` 下的方案 C 与方案 D Demo。

## 文档目的

本文用于说明当前海量动态角色 Demo 已实现的功能、两套方案的技术差异、资源约定、生成方式、场景操作方式和验收检查点。

本文不是调研结论文档，也不是详细开发计划；它面向后续实际操作 Demo 和快速理解当前实现。

## Demo 目标

当前 Demo 用于验证两条海量动态角色渲染路线：

1. **方案 C：GPU 骨骼动画**
   - 离线烘焙每帧骨骼矩阵。
   - 运行时每个实例独立播放动画。
   - 顶点阶段读取骨骼矩阵贴图，在 GPU 上完成蒙皮。

2. **方案 D：VAT 顶点动画贴图**
   - 离线烘焙每帧顶点位置和法线。
   - 运行时每个实例独立播放动画。
   - 顶点阶段读取 VAT 贴图，还原当前帧顶点位置。

两套 Demo 都先使用 `Graphics.RenderMeshInstanced` 分批绘制，每批最多 1023 个实例。当前阶段不考虑 LOD，也不做近景传统角色兜底。

## 当前资源约定

源资源位置：

```text
Assets/Model/mixamo.FBX
Assets/Model/mixamo_mesh.fbx
Assets/Model/Animation/mixamo@Ami.FBX
Assets/Model/Animation/mixamo@idle.FBX
Assets/Model/Animation/mixamo@run.FBX
```

当前使用方式：

- `mixamo.FBX`：作为身体和骨架来源。
- `mixamo_mesh.fbx`：作为未蒙皮配件来源。
- `piaodai`：当前用于测试“模型内配件节点显隐”的节点。
- `Ami / idle / run`：当前三个动画片段。

当前配件策略：

- 方案 C 不再把 `piaodai` 拆成独立配件 pass，而是把 `mixamo.FBX` 内所有身体 `SkinnedMeshRenderer` 和 `mixamo_mesh.fbx` 内的 `piaodai` 合成一个 `GpuSkinning_BodyMesh`。
- 方案 C 的 `GpuSkinning_BodyMesh` 会包含 `Beta_Surface`、`Beta_Surface002` 等身体子网格，避免只提取第一个 renderer 导致模型缺块。
- 方案 C 会把所有身体 `SkinnedMeshRenderer` 用到的骨骼合并成一套 union 骨骼表，再用同一套骨骼顺序写 mesh 权重和骨骼矩阵贴图，避免 `Beta_Surface002` 引用 `Spine2` 等参考 renderer 未包含骨骼时报错。
- 方案 C 直接使用 UV0.x 做部件分区：身体写入 `0~1`，当前 `piaodai` 写入 `1~2`；后续最多先预留 3 个配件区间，即 `1~2`、`2~3`、`3~4`。
- 方案 D 当前仍保留 `piaodai` 独立刚性配件 mesh/pass，用于和身体 VAT 分开验证。
- 当前 `piaodai` 仍是刚性部件，只跟随实例整体 transform，不参与身体骨骼细节变形或 VAT 顶点动画。

## 功能模块

### 生成工具

代码位置：

```text
Assets/MassiveCharacters/Common/Editor/MassiveCharacterDemoBuilder.cs
```

Unity 菜单：

```text
TAWork/海量角色Demo/准备模型导入设置
TAWork/海量角色Demo/生成方案D VAT Demo
TAWork/海量角色Demo/生成方案C GPU骨骼动画Demo
TAWork/海量角色Demo/一键生成全部Demo
```

功能：

- 设置模型 Read/Write。
- 读取 Mixamo 模型和动画。
- 生成运行时 mesh。
- 烘焙 VAT 顶点动画贴图。
- 烘焙 GPU Skinning 骨骼矩阵贴图。
- 创建材质。
- 创建 Demo 场景。

### 运行时渲染器

代码位置：

```text
Assets/MassiveCharacters/Common/Scripts/MassiveCharacterDemoRenderer.cs
```

功能：

- 生成实例矩阵。
- 为每个实例分配动画片段、起始相位、播放速度。
- 上传实例动画参数到 `GraphicsBuffer`。
- 分批调用 `Graphics.RenderMeshInstanced`。
- 方案 C 使用一个完整角色 mesh 绘制，配件在 shader 内按 UV0.x 部件区间显隐。
- 方案 D 使用身体 pass 和刚性配件 pass 绘制。
- 在屏幕左上角显示简单统计信息。

### 动画数据资产

代码位置：

```text
Assets/MassiveCharacters/Common/Scripts/MassiveCharacterAnimationData.cs
```

数据类型：

- `GpuSkinningAnimationData`
- `VatAnimationData`
- `MassiveAnimationClipInfo`

记录内容：

- 动画片段名称。
- 动画时长。
- 起始帧。
- 帧数量。
- 采样率。
- 贴图尺寸。
- 本地包围盒。

## 方案 C：GPU 骨骼动画

### 数据流程

```text
mixamo.FBX + Ami/idle/run
  -> Editor 采样动画
  -> 计算每帧每根骨骼的矩阵
  -> 写入 RGBAFloat 骨骼矩阵贴图
  -> 运行时 shader 读取矩阵
  -> 顶点阶段执行 GPU 蒙皮
  -> RenderMeshInstanced 绘制海量实例
```

### 当前特点

- 动画数据量主要与骨骼数和帧数相关。
- 相比 VAT，动画数据通常更小。
- 适合后续扩展更多配件、换装、动画混合。
- 当前 Demo 已改为“完整单 mesh + mesh 内配件显隐”：身体和 `piaodai` 在同一个 `GpuSkinning_BodyMesh` 中提交一次实例化绘制。
- 当前 `piaodai` 先不做骨骼绑定，只验证 mesh 内刚性部件显隐；后续雨伞、公文包、拐杖可继续沿用同一套 UV0.x 部件区间扩展。

### 当前实现细节

GPU Skinning 运行时 mesh 会把骨骼下标和权重复制到普通 UV 通道：

```text
TEXCOORD2: bone indices
TEXCOORD3: bone weights
TEXCOORD0.x: 部件分区，0~1 为身体，1~2/2~3/3~4 为最多三个配件
```

这样避免不同平台对 `BLENDWEIGHTS/BLENDINDICES` 顶点语义支持不一致；配件显隐则直接复用 UV0.x 的整数区间。

运行时实例数据的 `anim.w` 表示当前实例要显示的配件区间编号：`0` 表示不显示配件，`1` 表示显示 UV0.x 在 `1~2` 的配件，`2` 表示显示 `2~3`，`3` 表示显示 `3~4`。当前 `piaodai` 固定写入编号 `1`。

## 方案 D：VAT 顶点动画贴图

### 数据流程

```text
mixamo.FBX + Ami/idle/run
  -> Editor 采样动画
  -> SkinnedMeshRenderer.BakeMesh
  -> 逐帧写入顶点位置贴图和法线贴图
  -> 运行时 shader 读取 VAT
  -> 顶点阶段还原当前帧顶点
  -> RenderMeshInstanced 绘制海量实例
```

### 当前特点

- 运行时不需要骨架和骨骼权重参与身体动画。
- 顶点动画表现直接来自烘焙结果。
- 动画数据量与顶点数和帧数强相关，显存压力比 GPU 骨骼动画更大。
- 更适合固定外观、固定动画集合的海量角色。

### 当前 VAT 贴图布局

当前模型顶点数较高，不能使用“贴图宽度 = 顶点数”的简单布局。现已改为单帧多行分块：

```text
textureWidth = min(vertexCount, SystemInfo.maxTextureSize)
rowsPerFrame = ceil(vertexCount / textureWidth)
textureHeight = totalFrameCount * rowsPerFrame
```

顶点写入和采样坐标：

```text
x = vertexIndex % textureWidth
y = frameIndex * rowsPerFrame + vertexIndex / textureWidth
```

这样可以避免 `Texture has out of range width` 报错。

## 生成 Demo

推荐执行：

```text
TAWork/海量角色Demo/一键生成全部Demo
```

如果只想验证单个方案：

```text
TAWork/海量角色Demo/生成方案D VAT Demo
TAWork/海量角色Demo/生成方案C GPU骨骼动画Demo
```

生成后预期产物：

```text
Assets/MassiveCharacters/VatDemo/BakedData/
Assets/MassiveCharacters/VatDemo/Materials/
Assets/MassiveCharacters/VatDemo/Scenes/VAT_MassiveCharacters.unity

Assets/MassiveCharacters/GpuSkinningDemo/BakedData/
Assets/MassiveCharacters/GpuSkinningDemo/Materials/
Assets/MassiveCharacters/GpuSkinningDemo/Scenes/GPU_Skinning_MassiveCharacters.unity
```

注意：当前项目已在 Unity 中打开时，不要再用 batchmode 同时打开同一个项目执行生成。Unity 会报 `Multiple Unity instances cannot open the same project`。

## 使用 Demo 场景

打开场景：

```text
Assets/MassiveCharacters/VatDemo/Scenes/VAT_MassiveCharacters.unity
Assets/MassiveCharacters/GpuSkinningDemo/Scenes/GPU_Skinning_MassiveCharacters.unity
```

场景中核心对象：

```text
VAT Massive Character Renderer
GPU Skinning Massive Character Renderer
```

Inspector 常用参数：

| 参数 | 说明 |
| --- | --- |
| `instanceCount` | 实例数量，默认 1000 |
| `gridColumns` | 网格列数 |
| `spacing` | 实例间距 |
| `animationSpeed` | 整体动画速度倍率 |
| `animationMode` | 随机、Ami、Idle、Run |
| `movementMode` | 静止、环绕、前后移动 |
| `randomAccessory` | 是否随机显示配件 |
| `showAccessory` | 方案 C 控制 mesh 内配件显隐；方案 D 控制配件 pass |
| `accessoryDisplayPart` | 方案 C 的配件显示编号，当前 `1` 对应 UV0.x 的 `1~2` 区间 |
| `animateAccessory` | 当前主要用于方案 D 独立配件 pass；方案 C 的 `piaodai` 由 UV0.x 配件区间走刚性路径 |
| `showStats` | 是否显示左上角统计信息 |

推荐操作顺序：

1. 保持默认 1000 实例，确认角色和配件能显示。
2. 切换 `animationMode`，分别检查 Ami、Idle、Run。
3. 开关 `showAccessory`，确认方案 C 的 mesh 内 `piaodai` 或方案 D 的配件 pass 可显示和隐藏。
4. 开关 `randomAccessory`，确认实例间配件显隐有差异。
5. 逐步提高 `instanceCount`，观察 FPS 和动画数据估算。

## 验收检查点

最低验收：

- 一键生成不报错。
- 两个 Demo 场景都能创建。
- 1000 个实例能显示。
- 每个实例有随机动画相位。
- Ami、Idle、Run 都能播放。
- `piaodai` 配件能随机显隐。
- VAT 不再出现贴图宽度超限报错。

推荐验收：

- `instanceCount` 提高到 5000 后仍能观察到可用帧率。
- 方案 C 和方案 D 都能显示动画数据大小。
- VAT 动画没有明显顶点错位、爆点、抽帧。
- GPU Skinning 动画没有明显骨骼错位。

## 常见问题

### VAT 贴图宽度超限

现象：

```text
Texture has out of range width
```

当前已通过单帧多行分块修复。

### VAT 贴图高度超限

如果后续出现高度超限，说明顶点数、动画帧数或动画片段数量组合后仍超过单张 2D 贴图限制。

处理方向：

- 降低采样率。
- 减少动画片段数量。
- 使用更低顶点数模型。
- 改为 `Texture2DArray` 或多贴图分片。

### 生成后身体不动

优先检查：

- 动画 FBX 是否为 Humanoid 且能采样到 `mixamo.FBX`。
- `AnimationMode.SampleAnimationClip` 是否正确驱动实例骨架。
- 必要时后续改为临时 `Animator` 采样烘焙。

### 配件不跟随身体细节动作

这是当前第一版预期行为。

当前 `piaodai` 是刚性配件，只跟随实例整体 transform。后续如果要雨伞、公文包、拐杖跟随手部或身体骨骼，需要做骨骼绑定配件或单独 VAT 配件。

方案 C 当前已经把 `piaodai` 放进完整单 mesh，但它仍然是刚性部件；这只解决“同一 mesh 内按部件显隐”的数据组织问题，不等价于配件已经绑定到手部或身体骨骼。

## 后续建议

当前 Demo 先保证两套路线可生成、可播放、可对比。后续建议优先补：

- 生成后自动记录顶点数、帧数、贴图尺寸和数据大小。
- 增加一键对比场景。
- 增加更明确的性能测试 UI。
- 为方案 C 补一个绑定骨骼的真实配件。
- 为方案 D 评估 `RGBAHalf` 或 normal 压缩，降低显存占用。
