# 海量动态角色渲染与 GPU 动画方案调研

调研日期：2026-05-25  
项目上下文：当前工程为 Unity 2023.2.8f1，URP 16.0.5。

## 结论摘要

如果目标是“同一套或少量相似人物模型，生成大量动态角色，并支持多套配件显隐、动画播放”，行业主流做法不是直接堆 `SkinnedMeshRenderer + Animator`，而是按距离和交互等级分层：

| 层级 | 推荐方案 | 是否 GPU 动画 | 适合数量级 | 主要用途 |
| --- | --- | --- | --- | --- |
| 近景主角 / 高交互 NPC | 传统 `SkinnedMeshRenderer + Animator` | 不作为海量方案；可能由引擎做 GPU skinning，但动画求值仍偏 CPU | 1 到几十 | IK、换装、布料、表情、碰撞、强交互 |
| 中景重复 NPC | 动画共享 / 状态桶 / 降频更新 | 否或半 GPU | 几十到数百 | 复用同状态动画，降低 Animator 求值成本 |
| 远景人群 / 海量群众 | GPU Animation / Animation Instancing / VAT / Compute Skinning + Instancing | 是，主流 | 数百到数万，取决于质量和平台 | 城市场景、人群、观众席、背景 NPC |
| 极远景点缀 | Impostor、Billboard、VAT 低 LOD | 是或预烘焙 | 数千到数万 | 只看轮廓和运动感 |

对本项目最现实的路线是：

1. 近景保留 Unity 原生骨骼角色，保证换装、交互、表现力。
2. 中远景做一套 GPU 动画实例化角色：离线烘焙动画数据，运行时用 `Graphics.RenderMeshIndirect` / `RenderMeshInstanced` / BatchRendererGroup 绘制。
3. 配件显隐不要走“每个实例独立 `SkinnedMeshRenderer` 开关”，而要把配件做成可实例化的独立 mesh pass、子网格 pass，或在实例数据中传 bitmask/索引。
4. 建立 LOD 切换：近景骨骼版，中景 GPU 骨骼矩阵版，远景 VAT/简化网格/Impostor。

简短回答用户问题：**是的，在“海量相同或相似动态角色”这个问题上，GPU 动画或动画烘焙上 GPU 的方式已经是主流方向；但完整角色系统通常是传统骨骼动画与 GPU 人群方案混用，而不是所有角色都改成 GPU 动画。**

## 关键技术边界

### Unity 原生 GPU Instancing 不支持 SkinnedMeshRenderer

Unity 的 GPU Instancing 主要面向 `MeshRenderer` 或显式的 `Graphics.RenderMesh*` 调用。官方文档明确说明，不支持 `SkinnedMeshRenderer` 参与普通 GPU Instancing。因此传统换装角色如果由很多 `SkinnedMeshRenderer` 组成，会在 CPU 动画求值、骨骼更新、蒙皮、裁剪、draw call、材质批次上同时放大成本。

这意味着：

- “同一个 Prefab 复制 1000 个，每个都有 Animator 和多件装备”不是海量人群的可扩展方案。
- Unity 原生 `Enable GPU Instancing` 不能自动解决骨骼动画角色的实例化。
- 要做海量动态角色，通常需要把角色变成“静态 mesh + GPU 可读动画数据 + 实例数据”的渲染模型。

### Unity 2023.2 下可用的绘制入口

当前工程是 Unity 2023.2，可优先考虑这些入口：

- `Graphics.RenderMeshInstanced`：一次最多 1023 个实例，适合原型和中等数量；需要材质支持 instancing。
- `Graphics.RenderMeshIndirect`：通过 GPU/CPU 写入 indirect args，适合大规模、GPU culling 和多 draw command。
- `BatchRendererGroup`：更底层、更接近 Entities Graphics 的高性能渲染 API，适合自研渲染管理器。
- Entities Graphics：ECS 渲染路径，底层也依赖 BatchRendererGroup，适合项目愿意引入 DOTS 数据组织时使用。

Unity 6 的 GPU Resident Drawer 会自动用 BatchRendererGroup 绘制兼容 GameObject，能降低普通 `MeshRenderer` 的 draw call CPU 成本，但它不是传统 `SkinnedMeshRenderer` 海量动画的直接答案。当前项目还在 Unity 2023.2，不能把 Unity 6 特性作为近期基础方案。

## 主流方案对比

### 方案 A：传统 SkinnedMeshRenderer + Animator + LOD/Culling

做法：

- 每个角色保留完整骨骼、Animator、`SkinnedMeshRenderer`。
- 用 Animator Culling Mode、LODGroup、动画降频、Update When Offscreen 控制、骨骼数量裁剪来降成本。
- 配件通过多个 `SkinnedMeshRenderer` 显隐，或运行时合并换装 mesh。

优点：

- 表现力最好，支持 Unity 动画状态机、IK、物理、布料、表情、武器挂点。
- 换装开发最直观，内容管线成熟。
- 适合近景和可交互 NPC。

缺点：

- 不适合海量复制。Animator 求值、骨骼更新、蒙皮、包围盒更新和 draw call 都会线性膨胀。
- 多配件、多材质会进一步增加批次。
- 普通 GPU Instancing 不能直接合批 `SkinnedMeshRenderer`。

适用判断：

- 屏幕内几十个以内：可接受。
- 100 到 300 个：需要严格 LOD、动画降频和材质/骨骼优化。
- 1000 个以上：不建议作为主方案。

### 方案 B：动画共享 / 状态桶 / Pose Sharing

做法：

- 把角色按动画状态分桶，例如 idle、walk、run、cheer。
- 同一桶只求值一次或少量几次动画，再把姿态共享给多个角色。
- Unreal 的 Animation Sharing Plugin 是这个思路的官方实现：将大量角色归入状态桶，避免每个角色独立跑完整动画蓝图。

优点：

- 改造成本低于完整 GPU 动画。
- 近中景仍可保留骨骼角色和部分换装能力。
- 对“大量角色动作相似”的场景很有效。

缺点：

- 本质上仍是骨骼角色，draw call 和 skinned mesh 成本没有根除。
- 角色个性化动画、过渡、受击、IK 会受到限制。
- Unity 里通常需要自研或依赖第三方框架，并非一个开箱即用的内置人群系统。

适用判断：

- 适合中景 NPC、队列、观众、重复状态群众。
- 可作为近景骨骼版和远景 GPU 版之间的过渡层。

### 方案 C：GPU 骨骼动画 / Animation Instancing

做法：

- 离线把动画 clip 采样成骨骼矩阵纹理、StructuredBuffer 或 ComputeBuffer。
- 每个实例只保存 transform、当前 clip、播放时间、动画速度、随机相位、LOD、装备 bitmask 等少量实例数据。
- 顶点着色器或 compute shader 根据顶点 bone weights 读取骨骼矩阵并蒙皮。
- 用 `RenderMeshInstanced`、`RenderMeshIndirect`、BatchRendererGroup 或 Entities Graphics 绘制大量实例。

优点：

- 非常适合“相同骨架、相同或少量网格变体、播放不同时间点动画”的海量角色。
- CPU 只管理实例数据和可见性，动画求值搬到 GPU。
- 可以自然配合 GPU frustum culling、distance LOD、indirect draw。
- NVIDIA GPU Gems 早在 DirectX 10 时代就展示过用 instancing + vertex texture fetch 渲染近万独立动画角色，说明这是很早就被验证的人群渲染路线。

缺点：

- 动画通常需要预烘焙，运行时动画混合、IK、ragdoll、复杂表情会变难。
- 需要自定义 shader、资产烘焙工具、实例数据管理、LOD 和调试工具。
- 配件、换装、材质变体需要从管线开始设计，不能照搬普通角色 Prefab。
- 动画压缩、采样率、纹理格式和骨骼数量会影响显存。

适用判断：

- 这是 Unity 中做海量动态人物的重点候选。
- 对本项目，如果目标超过数百个可见动态人，建议优先做该方案的原型。

### 方案 D：VAT（Vertex Animation Texture，顶点动画贴图）

做法：

- 离线把每帧每个顶点的位置、法线等烘焙到纹理。
- 运行时角色变成普通静态 mesh，shader 按动画时间从纹理采样顶点位置。
- Unreal City Sample 的大规模城市群众就使用了 Mass Crowd、动画优化和动画烘焙/实例化思路；UE 社区和 City Sample 管线中常见 `AnimToTexture` / VAT 用于远景群众。

优点：

- 运行时非常便宜，不需要骨骼矩阵和 bone weights。
- 对极远景、观众席、背景群众很合适。
- 和普通 mesh instancing 很契合。

缺点：

- 显存随“顶点数 × 帧数 × 动画数”增长，动画多时压力明显。
- 不适合近景，因为插值、法线、脚底接触、动作混合和局部换装受限。
- 不适合动态装配很多不同配件，除非每个配件也单独烘焙，或拆成多个 VAT pass。

适用判断：

- 极远景和低 LOD 很推荐。
- 中景如果对细节要求不高，也可以用。
- 不建议作为唯一角色方案。

### 方案 E：Compute Skinning + Indirect Draw

做法：

- compute shader 按实例批量计算蒙皮后的顶点，写入大 vertex buffer。
- 后续用 indirect draw 绘制。
- 动画矩阵仍可来自烘焙数据、CPU 更新，或 GPU 计算。

优点：

- 比纯 vertex shader skinning 更适合复用蒙皮结果、多 pass 渲染、阴影 pass、深度 pass。
- 可和 GPU culling、LOD、Hi-Z occlusion 形成完整 GPU-driven 管线。

缺点：

- 实现复杂度高，需要管理 buffer 分配、同步、double buffering、阴影和多相机。
- 如果每帧都写大量顶点，带宽成本可能高于 vertex shader 直接蒙皮。

适用判断：

- 适合技术储备较强、需要完整 GPU-driven 渲染管线的项目。
- 本项目建议作为第二阶段，不作为最小验证方案。

### 方案 F：DOTS / ECS 人群渲染框架

做法：

- 用 Entities 管理大量角色实例数据。
- Entities Graphics 负责把 ECS 渲染数据送到 Unity 渲染架构，底层使用 BatchRendererGroup。
- 动画部分可自研 GPU skinning，或调研 Rukhanka、Latios Kinemation 等 DOTS 动画框架。

优点：

- 数据组织适合海量实体。
- 和多线程、Job、Burst、GPU culling 更容易形成统一架构。
- 对城市 NPC、人群仿真、刷怪系统等大规模系统更自然。

缺点：

- 对现有 MonoBehaviour/Animator/Prefab 工作流冲击大。
- Unity DOTS 动画生态仍需要谨慎验证版本兼容、渲染管线兼容和维护成本。
- 当前工程未引入 Entities 相关包，迁移成本不可忽略。

适用判断：

- 如果后续不仅是渲染，还要做海量行为、寻路、状态机，值得评估。
- 如果只是“先把很多人画出来并播放动画”，自研轻量实例管理器更快。

## 多套配件显隐的处理方式

海量角色的换装系统要避免“每个角色很多个 Renderer 独立开关”。推荐按以下顺序评估：

| 方式 | 做法 | 优点 | 缺点 | 适用 |
| --- | --- | --- | --- | --- |
| 独立配件 pass | 身体、头发、帽子、背包、武器等各自作为可实例化 mesh 批次 | 显隐简单，配件组合灵活 | pass/draw command 增多 | 中景 GPU 骨骼动画 |
| 子网格/材质槽 bitmask | 一个 mesh 内含多个部分，实例数据传显隐 bitmask，shader 丢弃或偏移不可见部分 | 实例数据简单 | 被隐藏顶点仍可能被处理；透明/clip 会影响性能 | 配件数量少、网格不大 |
| 预组合变体 | 常见套装预先合并成有限 mesh 变体 | 性能最好，draw 最少 | 组合爆炸，灵活性差 | 远景群众、固定职业 NPC |
| GPU buffer 索引 | 实例数据存 body/head/hair/equip 索引，各部件分别 instancing | 灵活且可批处理 | 管线复杂，需要按部件和材质分组 | 大规模正式方案 |
| VAT 配件烘焙 | 每个配件也烘焙 VAT 或绑定相同动画时间 | 运行时便宜 | 内容量和显存高，变体管理复杂 | 远景低 LOD |

推荐策略：

- 近景：普通换装，允许多个 `SkinnedMeshRenderer`，但控制数量和材质。
- 中景：身体、头发、帽子、武器等拆成少量可实例化部件；共享同一骨架动画纹理。
- 远景：预组合 8 到 32 个常见外观变体，减少组合复杂度。

## 动画播放能力对比

| 能力 | 传统骨骼 | 动画共享 | GPU 骨骼动画 | VAT |
| --- | --- | --- | --- | --- |
| 单 clip 播放 | 好 | 好 | 好 | 好 |
| 多角色不同相位 | 好 | 中 | 好 | 好 |
| clip 混合 | 好 | 中 | 可做但成本上升 | 较弱 |
| 上下半身分层 | 好 | 中 | 可做但复杂 | 不适合 |
| IK / LookAt | 好 | 中 | 困难 | 困难 |
| Ragdoll | 好 | 中 | 不适合 | 不适合 |
| 表情 BlendShape | 好 | 中 | 可做但需额外纹理/权重 | 困难 |
| 换装 | 好 | 好 | 可做，需管线设计 | 较弱 |
| 海量实例性能 | 弱 | 中 | 强 | 很强 |

## 推荐落地路线

### 阶段 1：验证 GPU 动画实例化最小闭环

目标：

- 1 个基础人形骨架。
- 1 到 3 个动画 clip：idle、walk、run。
- 1 个身体 mesh + 2 到 3 个配件 mesh。
- 1000 个实例可见，支持随机动画相位和简单移动。

技术点：

- Editor 烘焙动画：按固定 FPS 采样骨骼矩阵，写入 Texture2DArray 或 buffer 资产。
- Shader 读取骨骼矩阵并在 vertex stage 蒙皮。
- C# 维护实例数据：位置、旋转、缩放、clip、time、speed、appearance id、equipment mask。
- 绘制先用 `Graphics.RenderMeshInstanced` 验证，数量突破后换 `Graphics.RenderMeshIndirect`。

验收指标：

- 1000 个角色播放动画，CPU 主线程开销明显低于 1000 个 Animator。
- 支持至少一种配件随机显隐。
- 支持阴影或至少 depth prepass 的可控方案。
- 可以和近景传统角色并存。

### 阶段 2：加入 LOD 和批次组织

目标：

- LOD0：传统骨骼角色，近景少量。
- LOD1：GPU 骨骼动画，中景主力。
- LOD2：VAT 或低骨骼低顶点 GPU 动画，远景主力。
- LOD3：Billboard/Impostor，极远景。

技术点：

- CPU 或 GPU frustum culling。
- 按 mesh、material、clip atlas、配件组合分桶。
- 支持动画降采样和远景降低更新频率。
- 设计统一的角色外观 ID，近景和远景能对应同一套外观规则。

### 阶段 3：评估 ECS / BatchRendererGroup / GPU-driven

只有在以下条件满足时再推进：

- 目标数量超过数千，并且不只是渲染，还包括行为、寻路、状态机。
- 项目能接受 DOTS 或自研数据导向架构。
- 已经有稳定的动画烘焙、LOD、配件批处理资产管线。

否则，轻量 `RenderMeshIndirect` 管线更可控。

## 风险与注意事项

1. **动画内存不是免费的**  
   骨骼数、采样 FPS、clip 数量、矩阵格式都会吃显存。需要从第一版就记录内存公式，例如：`clipFrames × boneCount × matrixRows × float4Size`。

2. **配件灵活性和批处理天然冲突**  
   配件组合越自由，分桶越碎。海量人群应限制外观组合，使用职业、体型、套装、颜色表等有限随机规则。

3. **阴影可能比主 pass 更贵**  
   GPU 动画角色如果投射实时阴影，阴影 pass 也要重复蒙皮。远景建议关闭阴影或使用简化阴影。

4. **动画混合要克制**  
   GPU 上支持两个 clip 混合不难，但多个 layer、mask、IK、根运动校正会迅速复杂化。海量角色应以“预烘焙循环动画 + 少量过渡”为主。

5. **包围盒与裁剪要单独设计**  
   传统 `SkinnedMeshRenderer` 会处理动态包围盒问题；自研 GPU 动画需要自己给每个实例或每批实例设置保守 bounds，否则容易被错误裁剪。

6. **近景切换要避免穿帮**  
   GPU 人群切到传统骨骼角色时，需要同步位置、朝向、动画 clip、播放时间和外观 ID。

## 方案选择建议

| 目标 | 建议 |
| --- | --- |
| 只需要几十个高质量 NPC | 传统 `SkinnedMeshRenderer + Animator`，重点做 LOD、动画裁剪、材质合并 |
| 需要几百个中景角色 | 动画共享 + 部分 GPU 动画实例化，减少 Animator 数量 |
| 需要上千个动态群众 | GPU 骨骼动画 / Animation Instancing 作为主方案 |
| 需要上万远景观众或城市人群 | VAT / Impostor / GPU Instancing，牺牲近景表现 |
| 需要自由换装且近距离观察 | 不建议完全 GPU 化；近景保留传统骨骼，远景使用外观近似变体 |
| 需要行为系统也海量化 | 评估 ECS + Entities Graphics + 自研或第三方 DOTS 动画 |

## 本项目建议结论

基于当前 Unity 2023.2 + URP 项目，建议先不要直接引入重型 DOTS 全家桶，也不要尝试把现有 `SkinnedMeshRenderer` Prefab 直接实例化成海量人群。更稳的路线是做一个独立原型：

- 资产侧：先选一套标准人形骨架，统一骨骼命名和配件绑定规范。
- 烘焙侧：实现动画 clip 到骨骼矩阵纹理/buffer 的 Editor 工具。
- 渲染侧：实现 GPU skinning shader + `RenderMeshInstanced`，再升级到 `RenderMeshIndirect`。
- 外观侧：配件拆成有限部件批次，实例数据中携带外观 ID 和配件 mask。
- 运行侧：传统近景角色与 GPU 中远景角色共存，并做 LOD 切换。

这个方向既符合当前行业主流，也能和 Unity 现有项目渐进集成。

## 资料来源

- Unity Manual: GPU instancing  
  https://docs.unity3d.com/2023.2/Documentation/Manual/GPUInstancing.html
- Unity Manual: Skinned Mesh Renderer component  
  https://docs.unity3d.com/2023.2/Documentation/Manual/class-SkinnedMeshRenderer.html
- Unity Scripting API: Graphics.RenderMeshInstanced  
  https://docs.unity3d.com/2023.2/Documentation/ScriptReference/Graphics.RenderMeshInstanced.html
- Unity Scripting API: Graphics.RenderMeshIndirect  
  https://docs.unity3d.com/2023.2/Documentation/ScriptReference/Graphics.RenderMeshIndirect.html
- Unity Manual: BatchRendererGroup  
  https://docs.unity3d.com/2023.2/Documentation/Manual/batch-renderer-group.html
- Unity Entities Graphics 1.3 documentation  
  https://docs.unity3d.com/Packages/com.unity.entities.graphics@1.3/manual/index.html
- Unity 6 Manual: GPU Resident Drawer in URP  
  https://docs.unity3d.com/6000.0/Documentation/Manual/urp/gpu-resident-drawer.html
- Unity-Technologies Animation Instancing sample  
  https://github.com/Unity-Technologies/Animation-Instancing
- NVIDIA GPU Gems 3 Chapter 2: Animated Crowd Rendering  
  https://developer.nvidia.com/gpugems/gpugems3/part-i-geometry/chapter-2-animated-crowd-rendering
- Unreal Engine: Mass Entity  
  https://dev.epicgames.com/documentation/en-us/unreal-engine/mass-entity-in-unreal-engine
- Unreal Engine: Mass Crowd  
  https://dev.epicgames.com/documentation/en-us/unreal-engine/mass-crowd-in-unreal-engine
- Unreal Engine: City Sample Crowd  
  https://dev.epicgames.com/documentation/en-us/unreal-engine/city-sample-crowd-in-unreal-engine
- Unreal Engine: Animation Sharing Plugin  
  https://dev.epicgames.com/documentation/en-us/unreal-engine/animation-sharing-plugin-in-unreal-engine
- Unreal Engine: Animation Budget Allocator  
  https://dev.epicgames.com/documentation/en-us/unreal-engine/animation-budget-allocator-in-unreal-engine
- GPU Instancer Wiki: Best Practices / FAQ  
  https://wiki.gurbu.com/index.php?title=GPU_Instancer:BestPractices  
  https://wiki.gurbu.com/index.php?title=GPU_Instancer:FAQ
- Latios Framework / Kinemation  
  https://github.com/Dreaming381/Latios-Framework/tree/master/Kinemation
