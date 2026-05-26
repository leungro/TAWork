# 方案 C 与方案 D 海量角色 Demo 制作规划

规划日期：2026-05-25  
项目上下文：Unity 2023.2.8f1，URP 16.0.5。  
前提修正：当前阶段不考虑 LOD，不考虑近景传统骨骼角色兜底；项目只验证“海量动态角色渲染”这一类角色。

## 目标

制作两套可独立运行、可横向对比的 Demo：

1. **方案 C：GPU 骨骼动画 / Animation Instancing Demo**  
   使用同一骨架、同一或少量 mesh 变体，离线烘焙骨骼矩阵，运行时在 GPU 侧蒙皮，并通过实例化绘制海量动态角色。

2. **方案 D：VAT / Vertex Animation Texture Demo**  
   离线烘焙每帧顶点位置和法线，运行时按动画时间采样顶点动画贴图，通过实例化绘制海量动态角色。

两套 Demo 都要支持：

- 海量实例生成。
- 每个实例独立位置、旋转、缩放。
- 每个实例独立动画时间、速度、随机起始相位。
- 至少 3 个动画片段：idle、walk、run。
- 至少 1 套基础身体 + 2 到 3 个可显隐配件。
- 可在运行时切换实例数量，便于性能观察。
- 可显示 CPU/GPU 相关统计信息。

## 不做范围

本阶段明确不做：

- LOD。
- IK、Ragdoll、物理布料。
- 复杂动画状态机。
- 多层动画混合。
- 根运动精确同步。
- 联机同步。
- 真实 AI 行为。
- 角色近景品质验证。
- 完整美术换装系统。

## Demo 总体目录建议

建议后续代码和资源按独立实验目录组织：

```text
Assets/
  Plan/
    海量人物生成与GPU动画/
      docs/
  MassiveCharacters/
    Common/
      Materials/
      Meshes/
      Scripts/
      Shaders/
      Editor/
    GpuSkinningDemo/
      Scenes/
      Materials/
      Scripts/
      Shaders/
      BakedData/
    VatDemo/
      Scenes/
      Materials/
      Scripts/
      Shaders/
      BakedData/
```

规划文档只定义目标结构，实际目录可根据项目现有规范调整。

## 共同资产约束

为保证两套方案可比较，应尽量使用同一套源资产：

- 一个标准人形模型。
- 一个统一骨架。
- 三个循环动画：idle、walk、run。
- 身体 mesh 一个。
- 配件 mesh 至少两个，例如帽子、背包、武器。
- 配件绑定到同一骨架或可随角色整体 transform 运动。
- 材质尽量保持简单，先使用 URP Lit 或自定义简化 Lit。

建议先使用低中面数测试模型：

- 身体：3000 到 10000 triangles。
- 单个配件：200 到 3000 triangles。
- 骨骼数：优先控制在 64 以内。
- 动画采样率：先用 30 FPS。

## 共同运行时功能

两套 Demo 的场景 UI 尽量一致，便于对比：

- 实例数量：100、500、1000、5000、10000。
- 动画模式：全 idle、全 walk、全 run、随机混合。
- 运动模式：静止、环形行走、随机方向移动。
- 配件模式：无配件、固定配件、随机配件组合。
- 渲染模式：身体、身体+配件、仅配件压力测试。
- 统计显示：FPS、主线程耗时、渲染线程耗时、Batches、SetPass、显存估算、动画数据大小。

实例数据建议统一：

```csharp
struct MassiveCharacterInstance
{
    float4x4 localToWorld;
    uint animationClipIndex;
    float animationTime;
    float animationSpeed;
    uint appearanceId;
    uint equipmentMask;
}
```

实际实现可为了 GPU 对齐拆成多个 `float4` buffer。

## 方案 C：GPU 骨骼动画 Demo

### 技术目标

验证“骨骼矩阵烘焙 + GPU 蒙皮 + 实例化绘制”的可行性。

核心思路：

- Editor 下逐帧采样动画。
- 每个 clip 烘焙为骨骼矩阵数据。
- 运行时每个实例只记录 clip、time、speed、transform、equipmentMask。
- Shader 根据实例 ID 找到实例动画状态，再读取骨骼矩阵，执行顶点蒙皮。
- 身体和配件可共享同一套骨骼动画数据。

### 烘焙数据

每个动画 clip 输出：

- clip 名称。
- clip 时长。
- 采样 FPS。
- frame count。
- bone count。
- 每帧每骨骼的矩阵。
- bind pose 或 inverse bind pose。

建议第一版使用纹理承载骨骼矩阵：

- `Texture2DArray` 或 `Texture2D` atlas。
- 每个骨骼矩阵用 3 行 `float4` 存储。
- 采样方式使用 point filtering。
- 格式优先考虑 `RGBAFloat`，验证后再评估 `RGBAHalf`。

内存估算：

```text
数据大小 = clipFrameCount * boneCount * 3 * float4Size
float4Size = 16 bytes
示例：90 帧 * 64 骨骼 * 3 * 16 = 276,480 bytes，约 270 KB / clip
```

### Shader 输入

Mesh 顶点需要：

- position。
- normal。
- tangent，可选。
- uv。
- bone indices。
- bone weights。

实例数据需要：

- `localToWorld`。
- `clipIndex`。
- `animationTime` 或 normalized time。
- `animationSpeed`。
- `equipmentMask`。

### 配件显隐

根据当前需求，方案 C 第一版调整为“完整单 mesh + mesh 内部件显隐”：

- 生成 `GpuSkinning_BodyMesh` 时，合并 `mixamo.FBX` 下所有身体 `SkinnedMeshRenderer`，包括 `Beta_Surface` 和 `Beta_Surface002`。
- 合并 mesh 和烘焙骨骼贴图使用同一套 union 骨骼表，而不是只使用第一个 renderer 的骨骼数组，避免 `Beta_Surface002` 引用 `mixamorig:Spine2` 等骨骼时映射失败。
- 从 `mixamo_mesh.fbx` 中提取名称包含 `piaodai` 的节点，作为同一个 `GpuSkinning_BodyMesh` 内的配件顶点。
- 身体和 `piaodai` 用同一个 `RenderMeshInstanced` 调用绘制，不再为方案 C 生成 `GpuSkinning_PiaodaiMesh.asset` 或独立配件 pass。
- 直接使用 UV0.x 记录部件分区：身体为 `0~1`，配件 A/B/C 分别为 `1~2`、`2~3`、`3~4`，第一版最多限制 3 个配件。
- Shader 通过 `floor(uv0.x)` 和实例显示编号判断该实例是否显示对应配件；当前 `piaodai` 使用编号 `1`。
- 当前 `piaodai` 来自未蒙皮模型，先作为刚性部件跟随实例整体 transform，不参与骨骼细节变形。

注意：如果在 vertex shader 中把不可见配件顶点移到裁剪空间外，仍会消耗这些顶点的处理成本。第一版优先验证“单 mesh 内部件显隐”的数据组织和表现正确性；如果后续配件很多，仍需要考虑按配件 mask 分桶、拆分 draw 或 compute 侧生成可见实例列表。

### 运行时模块

建议脚本拆分：

- `GpuAnimationBakeWindow`：Editor 烘焙工具。
- `GpuAnimationClipData`：烘焙数据 ScriptableObject。
- `GpuSkinningCrowdRenderer`：实例数据生成、buffer 上传、绘制。
- `GpuSkinningCrowdController`：实例动画时间、移动逻辑、UI 参数。
- `MassiveCharacterStats`：统计显示。

### 绘制路线

第一步：

- 使用 `Graphics.RenderMeshInstanced`。
- 优点是实现快，便于调试。
- 缺点是单次 1023 实例限制，需要分批调用。

第二步：

- 切换到 `Graphics.RenderMeshIndirect`。
- 使用 `GraphicsBuffer` 存 instance data 和 draw args。
- 为后续 GPU culling / 分桶预留接口。

### 验收标准

最低验收：

- 1000 个实例同时播放动画。
- 每个实例动画相位不同。
- idle、walk、run 可切换。
- 至少一个配件可随机显隐。
- 无明显骨骼错位、爆点、闪烁。

推荐验收：

- 5000 个实例仍可运行并观察瓶颈。
- 身体 + 2 个配件都走 GPU skinning。
- `RenderMeshIndirect` 路线跑通。
- 能输出动画数据大小和每帧 buffer 上传量。

### 主要风险

- Unity mesh 的 bone weight 数据读取和 shader layout 对齐。
- 骨骼矩阵空间转换错误，导致动作变形。
- 法线/tangent 蒙皮不正确，导致光照异常。
- 配件如果骨架或 bind pose 不一致，会出现偏移。
- 阴影 pass 需要重复 GPU 蒙皮，可能放大成本。

## 方案 D：VAT Demo

### 技术目标

验证“顶点动画贴图 + 普通 mesh 实例化绘制”的可行性。

核心思路：

- Editor 下逐帧采样动画后的 skinned mesh 顶点。
- 把每帧每个顶点的位置、法线烘焙到纹理。
- 运行时 mesh 不再需要骨骼和权重。
- Shader 根据实例动画时间采样 VAT，还原当前帧顶点位置。
- 用实例化绘制大量角色。

### 烘焙数据

每个动画 clip 输出：

- clip 名称。
- clip 时长。
- 采样 FPS。
- frame count。
- vertex count。
- bounds。
- position texture。
- normal texture。

纹理布局建议：

```text
横轴：vertex index
纵轴：frame index
每个像素：一个顶点在某一帧的数据
```

如果顶点数超过最大纹理宽度，需要做 2D 分块或 Texture2DArray。

第一版建议：

- position 使用 `RGBAFloat`。
- normal 使用 `RGBAHalf` 或编码到 `RGBA8/ARGB32`。
- point filtering。
- 关闭 mipmap。

内存估算：

```text
position 数据大小 = frameCount * vertexCount * 16 bytes
normal 数据大小 = frameCount * vertexCount * 8 到 16 bytes
示例：90 帧 * 5000 顶点 * 16 = 7.2 MB / clip，仅 position
```

VAT 的动画数据通常明显大于骨骼矩阵方案，因此 Demo 必须显示数据大小。

### Shader 输入

Mesh 顶点需要：

- position，可作为 bind pose 或占位。
- normal，可作为默认值。
- uv。
- vertex id 或自定义 uv2 存 vertex index。

实例数据需要：

- `localToWorld`。
- `clipIndex`。
- `animationTime`。
- `animationSpeed`。
- `appearanceId`。
- `equipmentMask`。

Unity shader 中是否能直接稳定使用 vertex id，需要根据目标图形 API 验证。为了兼容，第一版建议在 mesh 的 `uv2.x` 写入归一化 vertex index。

### 配件显隐

VAT 下有两种可选路线：

1. **配件随主体整体运动，不做独立骨骼变形**  
   例如帽子、背包、武器只跟随角色 transform。实现简单，但不能跟随骨骼细节运动。

2. **配件单独 VAT 烘焙**  
   每个配件也烘焙 position/normal VAT，运行时按同一动画时间采样。表现更接近骨骼方案，但数据量更大。

Demo 建议两步走：

- 第一版：身体 VAT + 简单刚性配件显隐。
- 第二版：选择一个绑定配件做独立 VAT，验证数据和性能成本。

### 运行时模块

建议脚本拆分：

- `VatBakeWindow`：Editor 烘焙工具。
- `VatAnimationClipData`：烘焙数据 ScriptableObject。
- `VatCrowdRenderer`：实例数据生成、buffer 上传、绘制。
- `VatCrowdController`：实例动画时间、移动逻辑、UI 参数。
- `MassiveCharacterStats`：复用共同统计。

### 绘制路线

第一步：

- 使用 `Graphics.RenderMeshInstanced` 绘制 VAT mesh。
- 验证动画贴图采样、帧插值、实例随机相位。

第二步：

- 切换到 `Graphics.RenderMeshIndirect`。
- 和方案 C 使用相同实例生成逻辑，便于对比。

### 验收标准

最低验收：

- 1000 个实例同时播放 VAT 动画。
- 每个实例动画相位不同。
- idle、walk、run 可切换。
- 至少一个刚性配件可随机显隐。
- 动画没有明显顶点错序、纹理采样错帧。

推荐验收：

- 5000 个实例仍可运行并观察瓶颈。
- 支持当前帧与下一帧插值。
- 一个配件完成独立 VAT 烘焙。
- 能输出 VAT 纹理尺寸和总数据大小。

### 主要风险

- 顶点顺序必须稳定，烘焙 mesh 和渲染 mesh 不能重排。
- VAT 显存压力大，clip 和顶点数增加后膨胀明显。
- 法线压缩会影响光照质量。
- 帧插值会增加采样次数。
- 配件独立 VAT 会让数据量和 draw 数增加。

## 两方案对比验证表

Demo 完成后需要输出以下对比：

| 对比项 | 方案 C：GPU 骨骼动画 | 方案 D：VAT |
| --- | --- | --- |
| 1000 实例 FPS | 待测 | 待测 |
| 5000 实例 FPS | 待测 | 待测 |
| 10000 实例 FPS | 待测 | 待测 |
| CPU 主线程耗时 | 待测 | 待测 |
| GPU 耗时 | 待测 | 待测 |
| draw call 数 | 待测 | 待测 |
| 动画数据显存 | 预计较低 | 预计较高 |
| 顶点 shader 成本 | 较高，需要骨骼权重计算 | 较低到中等，需要纹理采样 |
| 配件支持 | 较自然，共享骨骼数据 | 较困难，需刚性或独立 VAT |
| 动画混合扩展 | 可扩展但复杂 | 较弱 |
| 内容管线复杂度 | 中到高 | 中 |
| 近景表现潜力 | 更好 | 较弱 |

## 开发顺序建议

### 第 1 步：共同基础

- 搭建 `MassiveCharacters/Common`。
- 准备测试模型、动画、配件。
- 做统一实例数据结构。
- 做统一统计面板。
- 做统一生成规则：网格阵列、随机散布、随机动画相位。

### 第 2 步：先做 VAT 最小闭环

VAT 对运行时蒙皮逻辑要求低，能更快验证：

- 海量实例绘制。
- 动画时间随机化。
- `RenderMeshInstanced` 到 `RenderMeshIndirect` 的绘制链路。
- UI 和统计面板。

### 第 3 步：再做 GPU 骨骼动画

在绘制和实例系统稳定后，补上：

- 骨骼矩阵烘焙。
- GPU skinning shader。
- 配件共享骨骼动画。
- 与 VAT 的性能和内存对比。

### 第 4 步：统一对比场景

新增一个对比场景：

- 左侧运行方案 C。
- 右侧运行方案 D。
- 使用相同实例数量、动画分布、配件比例。
- 输出同一套统计指标。

## 里程碑

| 里程碑 | 内容 | 产物 |
| --- | --- | --- |
| M1 | 公共测试资产和实例生成器 | Common 脚本、测试场景 |
| M2 | VAT 烘焙和 1000 实例播放 | VatDemo 场景、VAT 数据、shader |
| M3 | VAT Indirect 绘制和配件验证 | VatDemo 性能版 |
| M4 | GPU 骨骼矩阵烘焙和 1000 实例播放 | GpuSkinningDemo 场景、骨骼数据、shader |
| M5 | GPU Skinning Indirect 绘制和配件验证 | GpuSkinningDemo 性能版 |
| M6 | 两方案横向对比报告 | 性能表、内存表、推荐结论 |

## 预计决策点

Demo 完成后根据结果选择主路线：

- 如果重点是配件、换装、动画扩展，优先方案 C。
- 如果重点是极致数量、固定外观、固定动画，优先方案 D。
- 如果两者都要，推荐方案 C 作为主角色方案，方案 D 作为超远景或固定群众方案；但当前阶段不实现 LOD，只保留该结论作为后续扩展方向。

## 当前阶段推荐验收口径

由于当前明确“不考虑 LOD，只存在海量渲染角色”，建议第一阶段验收口径定为：

- 同屏 5000 个实例为主要目标。
- 10000 个实例作为压力目标。
- 必须支持动画播放和随机相位。
- 必须支持配件显隐，但允许第一版配件显隐采用简单策略。
- 必须记录动画数据显存，因为这是方案 C 和方案 D 的核心差异。
- 必须保留同一套输入资产，避免两方案因资产差异导致对比失真。

## 2026-05-25 实施记录

已按当前资源开始落地 Demo，代码目录为：

```text
Assets/MassiveCharacters/
  Common/
    Scripts/
    Editor/
    Shaders/
  VatDemo/
    BakedData/
    Materials/
    Scenes/
  GpuSkinningDemo/
    BakedData/
    Materials/
    Scenes/
```

默认资源约定：

- 蒙皮模型：`Assets/Model/mixamo.FBX`
- 未蒙皮/普通模型：`Assets/Model/mixamo_mesh.fbx`
- 动画：`Assets/Model/Animation/mixamo@Ami.FBX`
- 动画：`Assets/Model/Animation/mixamo@idle.FBX`
- 动画：`Assets/Model/Animation/mixamo@run.FBX`
- 当前配件测试节点：名称包含 `piaodai` 的节点。

Unity 菜单入口：

```text
TAWork/海量角色Demo/准备模型导入设置
TAWork/海量角色Demo/生成方案D VAT Demo
TAWork/海量角色Demo/生成方案C GPU骨骼动画Demo
TAWork/海量角色Demo/一键生成全部Demo
```

当前第一版实现策略：

- 两个方案的运行时渲染都先使用 `Graphics.RenderMeshInstanced` 分批绘制，每批最多 1023 个实例。
- 实例矩阵走 Unity 原生 instancing 数据，动画参数通过 `GraphicsBuffer` 传给 shader。
- 方案 D 的身体使用 `mixamo.FBX` 的蒙皮身体烘焙 VAT，并给运行时 mesh 写入 `uv2.x = vertexIndex`。
- 方案 D 的 `piaodai` 从 `mixamo_mesh.fbx` 的模型内节点提取，第一版作为刚性配件，只跟随实例 transform，不单独采样身体 VAT。
- 方案 C 会从 `mixamo.FBX` 烘焙骨骼矩阵贴图，完整单 mesh 使用 GPU skinning shader。
- 方案 C 的 `GpuSkinning_BodyMesh` 会合并 `mixamo.FBX` 下所有身体蒙皮 renderer，并把 `mixamo_mesh.fbx` 的 `piaodai` 合入同一个 mesh，用 shader 内 UV0.x 部件区间控制显隐。
- 方案 C 不再以第一个 `SkinnedMeshRenderer` 作为完整骨骼来源，而是合并所有身体 renderer 的 bones/bindposes；mesh 顶点权重和骨骼矩阵贴图都按这套 union 顺序写入。
- Demo 场景生成后默认 1000 实例、随机动画、随机配件显隐。
- 当前 `piaodai` 只是第一版测试节点，后续雨伞、公文包、拐杖等配件可沿用“模型内节点 + UV0.x 部件区间 + 实例显示编号”的管线接入；如果需要跟随手部细节动作，应进一步做骨骼绑定或写入配件挂点骨骼逻辑。

当前代码文件：

- `Assets/MassiveCharacters/Common/Scripts/MassiveCharacterAnimationData.cs`
- `Assets/MassiveCharacters/Common/Scripts/MassiveCharacterDemoRenderer.cs`
- `Assets/MassiveCharacters/Common/Editor/MassiveCharacterDemoBuilder.cs`
- `Assets/MassiveCharacters/Common/Shaders/MassiveVatInstanced.shader`
- `Assets/MassiveCharacters/Common/Shaders/MassiveGpuSkinningInstanced.shader`

补充说明文档：

- `Assets/Plan/海量人物生成与GPU动画/docs/海量角色功能分析与Demo使用说明.md`
- `Assets/Plan/海量人物生成与GPU动画/docs/VAT贴图尺寸报错分析与Demo操作说明.md`

本次推进补充：

- `MassiveCharacterDemoBuilder` 已分离身体源模型和配件源模型：身体动画数据来自 `mixamo.FBX`，配件网格来自 `mixamo_mesh.fbx`。
- `piaodai` 节点识别会向上检查父节点名，适配“模型中做配件节点”的层级组织方式。
- 运行时新增 `animateAccessory` 开关；当前默认为关闭，表示配件刚性跟随实例整体 transform，不参与 VAT 或 GPU skinning。
- GPU Skinning shader 和 VAT shader 都支持 `_MassiveUseVertexAnimation = 0` 的刚性配件路径，避免未蒙皮配件被错误动画化。
- 方案 C 已改为单 mesh：`GpuSkinning_BodyMesh` 包含 `Beta_Surface`、`Beta_Surface002` 和 `piaodai`，其中 `piaodai` 在 shader 内通过 UV0.x 的 `1~2` 区间显隐，不再单独出 `GpuSkinning_PiaodaiMesh.asset`。
- 重新执行方案 C 生成菜单时，会自动清理旧版 `GpuSkinning_PiaodaiMesh.asset` 和 `GpuSkinning_Piaodai.mat`，避免历史资产误导当前方案判断。
- GPU Skinning 运行时 mesh 会把骨骼下标和权重复制到 shader 的 `TEXCOORD2/3`，同时直接改写 UV0.x 作为部件区间，避免新增 UV4 通道。
- VAT 贴图布局已改成单帧多行分块，避免当前模型 `28414` 顶点导致 `Texture2D` 宽度超过设备最大 `16384` 的错误。
- 覆盖式重复生成资产时，场景引用会重新指向磁盘上的真实 asset，减少反复执行菜单后的引用不稳定问题。

当前生成产物：

```text
Assets/MassiveCharacters/VatDemo/BakedData/Vat_BodyMesh.asset
Assets/MassiveCharacters/VatDemo/BakedData/Vat_PiaodaiMesh.asset
Assets/MassiveCharacters/VatDemo/BakedData/VatAnimationData.asset
Assets/MassiveCharacters/VatDemo/Scenes/VAT_MassiveCharacters.unity

Assets/MassiveCharacters/GpuSkinningDemo/BakedData/GpuSkinning_BodyMesh.asset
Assets/MassiveCharacters/GpuSkinningDemo/BakedData/GpuSkinningAnimationData.asset
Assets/MassiveCharacters/GpuSkinningDemo/Scenes/GPU_Skinning_MassiveCharacters.unity
```

验证步骤：

1. 等待 Unity 编译完成。
2. 执行 `TAWork/海量角色Demo/一键生成全部Demo`。
3. 分别打开 `VAT_MassiveCharacters.unity` 和 `GPU_Skinning_MassiveCharacters.unity`。
4. 观察 1000 个实例是否播放 Ami、idle、run 随机动画，并确认方案 C 单 mesh 内的 `piaodai` 或方案 D 独立配件按实例随机显隐。
5. 修改 `MassiveCharacterDemoRenderer` 上的 `instanceCount`、`animationMode`、`showAccessory`、`randomAccessory`、`accessoryDisplayPart` 做压力和显隐测试。

验证注意：

- 当前项目已有 Unity 实例打开，命令行 batchmode 无法并行打开项目验证；本次尝试执行 `MassiveCharacters.Editor.MassiveCharacterDemoBuilder.BuildAllDemos` 被 Unity 的“Multiple Unity instances cannot open the same project”限制拦截，需要在已打开的 Unity 中等待编译完成后执行菜单。
- 如果 Unity Console 仍显示旧的 `RenderMeshInstanced(ref ...)` 或 `unity_InstanceID` shader 错误，先确认 Unity 是否已经刷新到最新脚本；本地文件已改为非 `ref` 调用，并对非 instancing shader 变体做了实例 ID 兜底。
- MCP 包曾出现 `BuildTarget.VisionOS` 兼容报错，当前 `Packages/com.coplaydev.unity-mcp@78ee541841/Editor/Tools/Build/BuildTargetMapping.cs` 中相关分支已处于注释状态；若 Console 仍显示该错误，多半是 PackageCache 或旧编译日志，需要刷新/重启 Unity。
- 如果生成后身体完全静止，优先检查 Humanoid 动画 clip 是否能被 `AnimationMode.SampleAnimationClip` 正确采样到 `mixamo.FBX` 的实例；必要时下一步改为临时 `Animator` 采样烘焙。
