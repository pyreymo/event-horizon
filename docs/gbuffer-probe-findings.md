# FFXIV opaque G-buffer 注入探针结论

日期：2026-07-16

## 探针方法

探针通过 hook 以下 `ID3D11DeviceContext` 调用跟踪渲染区段：

- `OMSetRenderTargets`
- `OMSetRenderTargetsAndUnorderedAccessViews`
- `Draw` / `DrawIndexed`
- `DrawInstanced` / `DrawIndexedInstanced`
- `ClearRenderTargetView`
- `ClearDepthStencilView`

当一次 OM 绑定中的五个 RTV 与 `RenderTargetManager.GBuffers[0..4]` 全部按槽位匹配，并且同时绑定了 DSV 时，将其识别为候选 opaque G-buffer 区段。初始实机验证在候选区段内发生实际 draw 后，向原五个 G-buffer MRT 和原 DSV 绘制了屏幕中央的 64×64 测试块。

测试像素使用固定的五组 MRT 输出、洋红色 diffuse 候选值和固定 NDC depth `0.5`。这只是接入及时序探针，不代表正确的 FFXIV 材质编码。

## 实机证据

实机日志稳定出现：

```text
G-buffer candidate bound: matched=5, views=5, size=2560x1440, dsv=0x2ABD7AC5920.
G-buffer probe #1 issued at candidate exit: 64x64, targets=5.
```

随后约两秒累计 300 次注入。相邻前五次约间隔 6–7 ms，约为 150 次/秒；这与高帧率逐帧执行相符，但仍应与当时实际 FPS 对照后再断言严格的一帧一次。

实机画面表现：

- 屏幕中央出现洋红色方块。
- 方块颜色随视角发生一定变化。
- 方块位于角色头部附近时，角色头表现为白色剪影。
- 镜头与角色头之间的障碍物能够改变该区域的遮挡结果；被遮挡的剪影区域呈探针的洋红色背景。

## 已确认结论

1. `RenderTargetManager.GBuffers[0..4]` 的 RTV 指针可以用来精确识别一段真实的五 MRT G-buffer 绘制区段；本次实机为 5/5 按槽位匹配。
2. 当前 hook 时点有效：在候选区段退出前提交的自定义 draw 没有被立即清除，并进入了 FFXIV 后续的场景处理链。
3. 自定义 draw 可以同时写入 FFXIV 的五个 G-buffer 和场景 DSV。固定深度也确实参与了后续遮挡关系；这不是 ImGui/Overlay 层的视觉覆盖。
4. 洋红色随视角变化，证明输出至少被后续依赖 G-buffer 的场景阶段继续解释。它很可能包含原生光照或材质响应，但仅凭这一现象还不能把变化唯一归因于光照。
5. 因此，“把不透明自定义几何作为 FFXIV opaque scene geometry 写入 G-buffer + depth”这条技术路径已经获得可见的实机可行性证明。

## 尚未确认

- 五个 G-buffer 的完整通道语义和编码范围。
- 当前测试常量中，究竟哪个 target/通道造成洋红色、白色角色剪影及视角相关变化。
- 当前候选区段在全部 opaque、character、forward/semitransparent 子阶段中的精确位置。
- 角色头白色是材质/分类通道被错误解释，还是后续角色 pass 与固定 depth 相互作用的结果。
- 固定 NDC depth `0.5` 没有世界空间含义，不能代表正常实体几何的深度行为。
- 当前约 150 次/秒是否严格等于当时每帧一次。

这些未知项意味着当前结果只能证明“写入与后续消费成立”，不能证明已经掌握了正确材质协议。

## 下一步建议

### 1. 先拆分 depth 与 MRT 影响

把当前一次写五个 MRT 的探针改成可选择模式，每次只改变一个变量：

1. `DepthOnly`：关闭所有 color write，只写 depth。
2. `Target0Only`：不写 depth，只写 `GBuffers[0]`。
3. `Target1Only`：不写 depth，只写 `GBuffers[1]`。
4. `Target2Only`：不写 depth，只写 `GBuffers[2]`。
5. `Target3Only`：不写 depth，只写 `GBuffers[3]`。
6. `Target4Only`：不写 depth，只写 `GBuffers[4]`。

其他 RTV 应通过独立 blend write-mask 保持完全不写，而不是输出零值。这样能直接回答：

- 白色角色剪影是否由 depth 引起。
- 哪个 target 控制 diffuse、normal、材质分类或光照参数。
- 哪些通道可以用于 decal，哪些必须由实体几何完整提供。

这是当前信息增益最高、风险最低的一步。

### 2. 对每个 target 做低幅度二分

确定有视觉影响的 target 后，不要继续使用全零/全一极值。以 `0.25 / 0.5 / 0.75` 逐通道测试，并记录：

- 纯色变化
- 明暗或高光变化
- 随镜头变化
- 随太阳/局部光源变化
- 角色、地形、天空和水面上的差异

优先验证已有线索：`GBuffers[0]` 的 normal 编码和 `GBuffers[2]` 的 diffuse 编码。

### 3. 再换成真正的世界空间三角形

材质最小组合明确后，再把屏幕空间固定深度探针换成：

- 世界空间顶点
- FFXIV 当前 view-projection
- 由 rasterizer 生成的正常 depth
- 固定法线和最小材质参数

这一步才真正验证“不透明实体几何”，并能区分实体前后遮挡、背面裁剪和相机运动是否正确。

### 4. 最后确认最终 hook 边界

在世界空间三角形成立后，再判断当前五 MRT 区段退出点是否足够稳定：

- 不同区域、天气、战斗和过场
- 动态分辨率、DLSS/FSR
- 角色与地形分别遮挡
- 水面、透明物和后处理

若某些场景中后续 opaque writer 覆盖探针，再沿当前 OM/Draw 记录把 hook 收紧到最后一次真实 G-buffer writer 之后。

## 推荐的下一轮实现范围

只增加一个 `ProbeMode` 选择和六套独立 color/depth write-mask，不改变候选区段识别与 command-list 提交路径。先完成 `DepthOnly` 与五个 `TargetNOnly` 的实机对照，再决定是否开始世界空间几何。

## 2026-07-16 后续实现状态

根据首次实机结果，探针已进行以下调整：

- 调用顺序改为 Original-first：离开 candidate 时先 AddRef 保留旧 G-buffer/DSV，调用游戏原始 OM target 切换，再临时绑定旧 targets 执行探针；`ExecuteCommandList(..., restoreContextState: true)` 恢复游戏刚绑定的新状态。
- 探针区域扩大为 128×128。
- 第一轮模式收敛为 `NoOp`、`DepthOnly`、`Target0Rgb`、`Target2Rgb`。
- `DepthOnly` 对全部 RTV 使用零 color write-mask，只以 `Always + DepthWriteMask.All` 写 depth。
- `Target0Rgb` 只写 G-buffer 0 的 RGB，左右半区分别输出 `(0.5, 1.0, 0.5)` 和 `(0.5, 0.0, 0.5)`；不写任何 alpha 或 depth。
- `Target2Rgb` 只写 G-buffer 2 的 RGB，左右半区分别输出中性灰 `0.25` 和 `0.75`；不写任何 alpha 或 depth。
- 新增从零开始的 `CandidateExitOrdinal`，只在每帧所选 candidate exit 执行模式。
- candidate 日志现在包含 ordinal、总 draw 数、indexed draw 数、RTV/DSV、viewport、开始/结束相对时间、持续时间和下一组 targets。

下一轮实机按 `NoOp → DepthOnly → Target0Rgb → Target2Rgb` 测试；若一帧存在多个 candidate exit，再保持模式不变逐个提高 ordinal。

## 第二轮实机结果

### Candidate ordinal

- `ordinal 0` 始终有效。
- `ordinal 1`、`ordinal 2` 均无效。

在已测试场景中，每帧只有一个完整的五 MRT G-buffer candidate exit。当前 5/5 RTV 匹配点因此不存在 ordinal 选择歧义；后续实验固定使用 `ordinal 0` 即可。此前“角色覆盖探针可能说明后面还有另一个 opaque G-buffer writer”的推断没有得到 ordinal 记录支持。

### DepthOnly

- 人物和 NPC 模型在测试区域内始终表现为白色剪影，与原本远近无关。
- 背景结果与镜头朝向高度相关，可能接近全黑、接近原色或非常明亮。

`DepthOnly` 只把区域内 depth 强制改为固定 NDC 值 `0.5`，却保留原像素的 normal、albedo、材质参数和分类数据。后续 deferred pass 会使用新 depth 重建位置，同时读取属于原场景表面的其余 G-buffer 数据。这是一个内部不一致的 G-buffer tuple。

因此这些现象不能用于判断一个正常实体会如何受光：

- 背景随镜头方向剧烈变化，符合“固定 NDC depth 重建出的伪世界位置随视线变化”，继而影响局部光、雾、阴影或其他 position-dependent pass。
- 人物/NPC 白色剪影可能还涉及角色分类、独立角色/半透明缓冲或材质参数，但它首先证明的是 depth 不能脱离其他 G-buffer 属性单独代表新实体。
- 白色剪影不再是“后续一定还有 opaque writer”的可靠证据。

### Target0Rgb

- 从上向下观察时，一半接近透明/原场景，另一半接近全黑。
- 反向观察时左右关系反转。
- 平视时两半趋向灰色。

左右测试值分别为 `(0.5, 1.0, 0.5)` 和 `(0.5, 0.0, 0.5)`。它们表现出方向相反且随观察/光照关系变化的响应，可以确认：

1. `GBuffer 0 RGB` 编码法线方向。
2. 当前注入发生在消费该法线的 lighting pass 之前。
3. 只写 RGB、保留 alpha 的 write-mask 工作正常。

当前证据足以支持 `GBuffer 0 RGB ≈ world-space normal`。精确坐标轴符号和归一化/压缩方式仍应在实现实体材质时用更多轴向值确认。

### Target2Rgb

- 区域内表面失去原纹理颜色，呈类似无纹理素模的深浅灰。
- 几何仍明显接受原场景光源照射。

这可以确认：

1. `GBuffer 2 RGB` 是 diffuse/albedo 类数据，至少控制基础明暗和原纹理信息。
2. 它与 lighting 分离存储，而不是已经包含最终光照结果。
3. 当前 candidate exit 位于 deferred lighting 之前，注入的 albedo 会被 FFXIV 原生光照消费。

该轮仅使用中性灰，尚未验证三个通道是否是直接的 RGB 色彩编码；后续世界三角形的彩色测试需要继续收紧这一点。

### 第二轮结论

原先进入世界空间三角形前要求确认的三项现已全部成立：

```text
GBuffer 0 RGB 的 normal 语义得到确认
GBuffer 2 RGB 的 diffuse/albedo 语义得到确认
最终 candidate ordinal 确认为 0
```

因此下一步可以进入最小世界空间三角形，但它仍只是几何/深度验证，不应直接称为完整材质 backend。首版应只在已有 opaque 地形或墙面前测试：写正常 rasterized depth、G0 RGB 法线和 G2 RGB 灰色 albedo；继续保留 G0/G2 alpha 以及 G1/G3/G4，避免尚未识别的通道被极值污染。

这会继承三角形后方原场景像素的未知材质通道，所以只能验证投影和遮挡。形成独立、可放置在任意背景上的实体之前，仍需识别或找到 G1/G3/G4 及各 alpha 的安全默认值。

## WorldTriangle 几何里程碑实现

在第二轮结果确认 G0/G2/ordinal 后，新增 `WorldTriangle` 探针模式，范围严格限定为验证世界空间投影、真实 rasterized depth、原生遮挡和 G-buffer 消费链路。

实现属性：

- 矩阵来源：当前活动场景相机的 `Scene.Camera.ViewMatrix * Render.Camera.ProjectionMatrix`，与 FFCS `Scene.Camera.WorldToScreen` 使用的路径一致。
- 三角形在首次进入该模式时按相机姿态生成一次，之后顶点固定在世界坐标中；移动相机不会带着三角形移动。
- 初始三个顶点相对相机的前向距离约为 3、7、5 米，构成带明显连续深度梯度的斜面，便于穿过墙、柱子和地面遮挡边界。
- 法线由 `Normalize(Cross(p1 - p0, p2 - p0))` 计算，并统一朝向初始化时的相机一侧。
- G0 RGB 写入 `normal * 0.5 + 0.5`。
- G2 RGB 写入中性灰 `0.5`。
- G0/G2 alpha 与 G1/G3/G4 完全不写，继续继承背景像素。
- depth 使用正常 rasterized `SV_Position.z`、`GreaterEqual` 和 `DepthWriteMask.All`。
- 不使用 screen-space scissor；`CullMode=None`。
- 每帧重新读取当前 View/Projection。若出现亚像素漂移或 TAA 重影，优先核对该 Projection 是否与 opaque pass 的 jittered projection 完全一致。
- `WorldTriangle` 模式启用且顶点已初始化后，ImGui background draw list 会复用 EventHorizon 的世界箭头，从本地玩家脚下指向固定三角形中心；目标不在屏幕内时箭头落在对应屏幕边缘。该箭头只用于定位，不参与或改变 G-buffer 探针。

该模式可以判断 ViewProjection、reverse-Z、连续深度、原生前后遮挡和 G0/G2 的 lighting 消费是否成立，但不能判断完整材质、高光、AO、TAA、阴影分类或安全 opaque tuple。三角形可能接收已有 shadow map 的结果，但不会参与已经结束的 shadow-map pass，因此不会投射自己的阴影。

### WorldTriangle 不可见后的单帧诊断矩阵

首版世界三角形的 ImGui 定位箭头指向合理位置，但实机没有任何可见三角形。为一次性拆分矩阵、转置和 depth 三类原因，`WorldTriangle` 临时改为同时绘制六个固定世界三角形：

| 标签 | G2 颜色 | VP 来源 | CPU 上传前转置 | Depth |
| --- | --- | --- | --- | --- |
| `C/NoDepth/T` | 红 | `Control.ViewProjectionMatrix` | 是 | 禁用 |
| `S/NoDepth/T` | 绿 | `Scene.ViewMatrix * Render.ProjectionMatrix` | 是 | 禁用 |
| `C/NoDepth/Raw` | 蓝 | `Control.ViewProjectionMatrix` | 否 | 禁用 |
| `C/Always/T` | 黄 | `Control.ViewProjectionMatrix` | 是 | `Always + WriteAll` |
| `C/GreaterEqual/T` | 洋红 | `Control.ViewProjectionMatrix` | 是 | `GreaterEqual + WriteAll` |
| `S/GreaterEqual/T` | 青 | `Scene.ViewMatrix * Render.ProjectionMatrix` | 是 | `GreaterEqual + WriteAll` |

六组三角形按 3×2 排列在初始化时相机前方约 5 米，关闭背面剔除。每组都有同色 ImGui 世界指向线和文字标签。

结果判读：

- 红色可见：`Control.ViewProjectionMatrix + transpose` 的 world VS/MRT 路径成立。
- 绿色可见而红色不可见：应采用 Scene VP，而不是 Control VP。
- 蓝色可见而红色不可见：矩阵上传转置约定错误。
- 红/绿/蓝可见，但黄/洋红/青不可见：问题集中在 depth 输出或 DSV 兼容性。
- 黄色可见但洋红/青不可见：rasterized depth 有效，但 reverse-Z 比较方向或数值范围不对。
- 洋红可见而青不可见，或相反：矩阵来源影响真实 depth 对齐。
- 六组都不可见但 ImGui 标记位置合理：优先检查 world input layout、world shader、command-list draw range或 G-buffer 分类通道；不应继续调整世界位置。

初始化日志同时记录 Control VP 与 Scene VP 的最大元素差 `matrixMaxDelta`，用来判断两套矩阵是否实际相同。

此外固定绘制一个橙色的 `ClipControl` 三角形，位置在屏幕中心上方，不使用世界坐标或任何相机矩阵，且关闭 depth test/write。它复用 WorldTriangle 的 input layout、VS/PS、MRT blend 和 command-list 路径：

- `ClipControl` 可见而六个世界三角形都不可见：问题集中在世界坐标到 clip-space 的变换。
- `ClipControl` 也不可见：优先检查 WorldTriangle 专用 shader、input layout、顶点范围或 draw 提交，不再把原因归到矩阵/depth。

### 首轮 WorldTriangle 诊断结果与修正

实机结果：

- `ClipControl` 有三角形轮廓，但经过 lighting 后呈黑色。这证明 WorldTriangle 专用 VS/PS、input layout、MRT 绑定和 draw command 已经生效；黑色不能解释为“没有提交 draw”。
- 六个世界标记都落在初始化镜头的背面；转身后离屏箭头出现并持续指向固定世界位置。
- 六个离屏箭头汇聚到同一屏幕边缘点，是箭头裁边后的投影结果，不足以证明六个世界中心相同。

根因首先落在旧的摆放逻辑：它通过 `inverse(ViewMatrix)` 和猜测的 `+Z/-Z` 约定反推 forward/right/up，但该 forward 与 FFXIV/Dalamud 的实际屏幕投影方向相反。因此此轮没有产生有效的矩阵/depth 对比结果。

修正：

- 使用 FFCS `Scene.Camera.ScreenPointToRay`，在屏幕归一化位置 `(0.30/0.50/0.70, 0.32/0.62)` 直接生成六个固定世界三角形；每个顶点也分别由对应屏幕射线在 5m 处构造。初始化时它们应当处于视锥内并彼此分离。
- 红、绿、蓝三个无 depth 变体和橙色 `ClipControl` 改为只写 G2 RGB，保留原始 normal，避免错误/无关法线把 albedo 控制样本照成黑色。
- 黄、洋红、青仍写 G0 RGB + G2 RGB，并分别使用 `Always` 或 `GreaterEqual`，继续承担真实 depth 与 normal/albedo tuple 的验证。

### 第二轮 WorldTriangle 诊断结果

实机可见性：

| 变体 | 结果 |
| --- | --- |
| 橙 `ClipControl` | 可见，最终呈灰/黑色 |
| 红 `C/NoDepth/T` | 可见，最终呈灰/黑色 |
| 绿 `S/NoDepth/T` | 可见，最终呈灰/黑色 |
| 蓝 `C/NoDepth/Raw` | 不可见 |
| 黄 `C/Always/T` | 可见，最终呈灰/黑色 |
| 洋红 `C/GreaterEqual/T` | 可见，最终呈灰/黑色 |
| 青 `S/GreaterEqual/T` | 不可见 |

由此确认：

1. CPU 矩阵上传到当前 `mul(rowVector, ViewProjection)` shader 前必须 transpose；蓝色失败排除了 raw 上传。
2. `Control.ViewProjectionMatrix` 是当前 opaque depth 对齐所需的矩阵来源。Scene VP 足以生成落在视口内的片元（绿色可见），但它产生的 rasterized depth 无法通过现有 DSV 的 `GreaterEqual`（青色失败）。
3. FFXIV 当前路径使用 reverse-Z；`GreaterEqual + DepthWriteMask.All` 的洋红色世界三角形可见，证明真实 rasterized depth、DSV 测试/写入和 G-buffer draw 已贯通。
4. `Always` 黄色可见说明 depth 写入本身也工作；洋红色进一步证明最终实现无需依赖 `Always`。
5. 世界空间不透明几何的最小技术路径已经成立：`Control VP + transpose + GreaterEqual + G0/G2 write`。

所有三角形最终呈灰/黑色是下一阶段的 G-buffer 语义/材质 tuple 问题，不再是投影或 depth 问题。尤其是橙、红、绿样本关闭了 depth 且只写 G2 RGB，却没有按写入值表现出对应色彩；这说明此前由中性灰实验得出的 `G2 RGB ≈ diffuse/albedo` 还不能扩大成“G2 三通道就是直接 RGB albedo”。另一种可能是继承的 G0/G2 alpha、G1、G3、G4 中存在材质分类或色彩解释字段。

下一步应固定 `Control VP + transpose`，先以相同的 no-depth 条件并排测试 G2 的纯 R、纯 G、纯 B、白和黑，同时记录 G2 RTV 的实际 DXGI format。确认色彩通道映射后，再使用 `GreaterEqual + DepthWriteMask.All` 建立 `KnownSafeOpaqueTuple`；不应继续增加 ViewProjection 或 depth 比较变体。

## Donor opaque tuple 实现

矩阵/depth 基线固定后，探针分成两个独立模式：

### WorldTriangle：一次性 G2 通道扫描

以 `Control VP + CPU transpose + no depth write` 同时绘制五个世界三角形，只写 G2 RGB：

```text
G2 R     = (1, 0, 0)
G2 G     = (0, 1, 0)
G2 B     = (0, 0, 1)
G2 White = (1, 1, 1)
G2 Black = (0, 0, 0)
```

这轮只用于确认独立通道响应，不再承担矩阵或 depth 验证。

### DonorOpaqueTuple：完整原生模板

选择模式前，将屏幕中心对准普通不透明地板或墙面。模式启用后的第一个有效 candidate exit 会：

1. 获取旧 G0..G4 RTV 对应的 Texture2D 和实际 resource/RTV DXGI format。
2. 在重新绑定旧 MRT 前，对每张 G-buffer 从屏幕中心执行一次 1x1 `CopySubresourceRegion`，写入插件拥有的同格式 1x1 texture。
3. 为五张 donor texture 创建独立 SRV；源 G-buffer 不会同时作为 SRV 和 RTV。
4. 将旧 G0..G4 重新绑定为 MRT，用 `Control VP + transpose + GreaterEqual + DepthWriteMask.All` 绘制一个世界三角形。
5. pixel shader 从五张 donor SRV 完整读取并输出 RGBA，只把 G0 RGB 替换为三角形的编码法线；首轮保留 donor G2 RGB，不写自定义颜色。

donor 只在进入模式时捕获一次，随后持续复用。切换模式或重载探针会释放并重新捕获。日志记录捕获坐标及五张 G-buffer 的 resource/RTV formats。

当前仍不修改 stencil。同时新增 `OMSetDepthStencilState` hook：持续记录游戏最近绑定的原生 DepthStencilState，在 candidate 开始时取快照，并在区段内发生变化时更新；candidate/probe 日志包含 depth enable、write mask、comparison、stencil enable、read/write mask、front/back操作和 `StencilRef`。若完整 donor MRT tuple 仍异常，下一步可直接依据该日志验证背景 stencil 或写入普通 opaque stencil reference，无需再次增加观测 hook。

## 首轮 donor tuple 实机结果

- G2 纯 R、纯 G、纯 B、白、黑五个 no-depth 三角形均呈灰黑、近似半透明，彼此没有显著差异。
- 完整 donor G0..G4 三角形没有正常颜色或亮度，主体近似纯黑。
- donor 三角形能够遮挡或切断局部光源效果，证明它的 rasterized depth 与 `GreaterEqual + WriteAll` 仍然有效。
- 人物模型与三角形重叠的部分呈白色轮廓，和早期 `DepthOnly` 探针中的人物/NPC白色异常一致。

当前可以排除“世界几何或 reverse-Z 又失效”。失败集中在 donor 数据有效性或 lighting 分类：

1. 1x1 donor copy/SRV 可能因 resource format、RTV format、typed SRV解释或采样内容不合适而返回零或错误值；在确认 raw donor 值前，不能把黑色直接归因于材质协议。
2. 捕获的屏幕中心像素若不是普通 opaque表面，而是角色、天空、透明效果或未覆盖像素，完整 tuple 本身就不是安全 donor。
3. 即使五个 MRT 正确，未写 stencil 仍可能让 lighting pass 跳过三角形或走错误分类。人物白色轮廓以及三角形影响局部光源，使 stencil/后续角色 pass 成为高优先级嫌疑。
4. 还可能存在 G0..G4 之外的 lighting输入，例如额外目标、stencil分类或与 depth/屏幕位置耦合的数据。

下一步不应继续目测更多常量。先获取本轮日志中的五张 `resource/rtv DXGI format` 与 `nativeOM` stencil state；随后为 donor 1x1 增加一次性 staging readback，记录每张纹理的 raw bytes，证明 copy 和 SRV源数据非零。只有 donor 数据确认有效后，才并排测试“保留背景 stencil”和“写入原生普通 opaque StencilRef”两个三角形。

### Donor format 与原生 stencil 观测

实机日志：

```text
G0 B8G8R8A8_UNorm
G1 B8G8R8A8_UNorm
G2 B8G8R8A8_UNorm
G3 R16G16B16A16_Float
G4 B8G8R8A8_UNorm
```

resource 和 RTV format 完全一致，不存在 typeless view 或明显 format 不兼容。BGRA资源在 `SV_Target`/typed SRV中仍使用逻辑 RGBA，当前不优先怀疑简单 R/B swizzle。

candidate末端捕获到的原生 OM state：

```text
DepthEnable=false
StencilEnable=true
StencilReadMask=0x80
StencilWriteMask=0x00
StencilRef=128
```

这不是一个写 stencil 的 opaque draw state，而是一个读取既有 `0x80` 分类位的状态；它很可能属于 candidate 后段的分类/消费准备。插件此前使用 stencil disabled 绘制，只保留三角形屏幕覆盖位置原有的 stencil，因此“新 depth + 新 MRT，但没有相应 0x80 分类位”成为黑色结果的强嫌疑。

新增一次性对照：

1. donor 捕获后将五张1x1 default texture复制到 staging texture；首次 command list执行后只 Map一次，日志以十六进制输出 `G0..G4` raw bytes。
2. `Donor preserve stencil`：完整 donor MRT + 新法线 + `GreaterEqual`，stencil disabled，保持当前行为。
3. `Donor write stencil 0x80`：相同 donor tuple，但启用 stencil write mask `0x80`、comparison `Always`、pass `Replace`、reference `0x80`，在 depth通过时写入分类位。

若 raw donor非零且只有 `write stencil 0x80` 获得正常lighting，便可确认第一个安全 opaque协议至少包括五个 MRT、depth和 stencil bit `0x80`。若两个仍同样黑，则需要继续寻找其他 stencil位、额外目标或 lighting阶段依赖。

### 首轮 stencil 对照与 donor 采样修正

实机结果：

- `Donor preserve stencil`：半透明黑色，能遮挡场景光，保留白色人物剪影，depth正确。
- `Donor write stencil 0x80`：同样半透明黑色并遮挡场景光，但白色人物剪影消失，depth正确。
- donor raw readback全部非零：`G0=B2E3BD23,G1=335F51CC,G2=A0A1A4FF,G3=4E006E0000000000,G4=44EE940A`。这证明1x1 copy、staging readback和donor资源至少不是全零失败。

`0x80` 明确影响角色/场景分类，但没有恢复lighting，因此它不是完整 opaque协议的充分条件。当前 donor 捕获点 `(0.5,0.5)` 实际位于第三人称角色头部，采到的是角色材质 tuple，而不是计划中的普通地板/墙面；该样本不能继续作为场景 opaque donor解释。

donor采样点改为屏幕归一化 `(0.25,0.75)`，避开画面中央角色。Donor模式下 ImGui background draw list 会在该位置显示橙色圆形十字和 `Donor sample` 标签；进入模式前应让普通地板或哑光墙面覆盖该标记。切换离开再进入 Donor模式会释放并重新捕获。

### 地板 donor 结果与真实 stencil readback

地板 donor raw：

```text
G0=82FF7E80
G1=04648500
G2=2B3E4CFF
G3=FF7B00000000003C
G4=7FFF7F00
```

按 BGRA resource 的逻辑 RGBA解释：

- G0 RGB约为 `(0x7E,0xFF,0x82)`，对应接近世界 `+Y` 的地板法线；G0 alpha为 `0x80`。
- G2 RGB约为 `(0x4C,0x3E,0x2B)`，是合理的棕色地板基础色；这重新确认 G2 是直接 RGB albedo，先前彩色测试无差异来自不完整分类/lighting，而不是G2通道映射。
- G3 half-float为 `(65504,0,0,1)`，符合原生 sentinel/default形态。
- 五张 donor均为非零且内容与地板语义一致，donor MRT捕获路径确认有效。

地板 donor仍与角色 donor一样呈黑，写 `0x80` 也没有恢复lighting。因此下一步不再根据“原生状态读取了 bit 0x80”猜测 donor stencil。实现改为：对原 DSV创建同尺寸 staging texture，在首次捕获时执行一次完整 `CopyResource`，随后只 Map一次并读取 donor坐标的原始4/8字节。日志追加：

```text
DS[format]=rawBytes,stencil=0xNN
```

第二个 donor三角形随后使用 write mask `0xFF` 写入读到的真实 `0xNN`，标签也会更新为该值。如果真实 stencil对照仍然黑，则五 MRT、depth、donor stencil均可排除，下一主假设应改为：当前 candidate exit 晚于某个基于旧 depth/stencil建立的 light list、light volume或分类准备阶段，世界几何必须更早注入。

### 真实地板 stencil 结果与注入点前移

DSV为 `R24G8_Typeless`，donor点 raw值为：

```text
DS=DA730410
stencil=0x10
```

使用 write mask `0xFF` 写入真实地板 stencil `0x10` 后，donor三角形仍与保留背景stencil版本相同：半透明黑色、能遮挡场景光、depth正确。至此已同时验证并排除：

```text
合法地板 G0..G4
合法地板 stencil 0x10
Control VP + transpose
GreaterEqual + depth write
```

“能遮光但自身不受光”最符合当前注入发生在某个基于旧 depth/stencil构建的 light list、light volume或分类准备之后。即使物理命令顺序仍早于最终lighting draw，相关准备也可能在五 MRT candidate内部通过 compute/UAV或中间pass提前完成。

Donor模式改为两阶段：

1. 首帧 candidate exit只捕获 donor MRT/DSV并执行 readback，不再晚期绘制世界三角形。
2. 下一帧在所选 candidate的第一个 `Draw`、`DrawIndexed`、`DrawInstanced`或`DrawIndexedInstanced` detour中，在调用原始draw之前执行 donor command list。
3. 此时 G-buffer/depth clear已经完成，但尚无任何原生 candidate draw；插件写入的新depth、stencil和MRT可参与candidate内部后续的全部准备工作。
4. 随后的原生不透明几何继续使用同一DSV，距离更近的表面会正常覆盖插件三角形，距离更远的表面会被其depth挡住。

日志中成功的早期绘制会显示 `geometry=world triangle at candidate begin`。若此前黑色来自过晚的light分类，这一版本应首次获得正常场景lighting；若仍黑，则需要检查candidate开始前的独立depth prepass或更早的light-culling阶段。

### Candidate begin 实机结果

- 保留当时 stencil 的橙色 donor 从半透明黑色变为全白。
- 提前强写地板终态 stencil `0x10` 的洋红 donor 仍为黑色。
- 两者仍能进行正常 depth遮挡。

这证明时点确实参与结果，但 candidate begin 与 candidate exit 都不是完整答案：

```text
begin + 初始/clear后的stencil → 白色
begin + 提前写终态0x10       → 黑色
exit  + 终态stencil          → 黑色
```

地板 stencil `0x10` 很可能是在 candidate内部某个阶段由原生几何逐步生成，不能在第一个draw前简单预写。下一步先记录 candidate 内部全部 `OMSetDepthStencilState` 转换及其发生时的累计draw ordinal，而不是盲猜新的注入点。

transition日志格式：

```text
G-buffer OM transitions: ordinal=0,
draw=0(initial):...
|| draw=N:state=...,depth=...,stencil=...,read=...,write=...,ref=...,front=Comparison/Fail/DepthFail/Pass,...
```

最多保留前48次转换，并将 front/back stencil操作展开为实际枚举值。另增加独立的 early injection确认日志，避免常规 probe日志节流掩盖是否真的在 draw 0 前提交。取得 transition序列后，再选择1到数个有真实 stencil write或depth阶段切换的draw边界做注入 sweep。

### Candidate 内部 donor sweep

实机 transition 将区段分为：

```text
draw 0     depth Greater, stencil disabled
draw 306   depth GreaterEqual, write stencil 0x10
draw 334   write stencil 0x08
draw 358   write stencil 0x09
draw 378   depth readonly, test stencil 0x01
draw 379   depth readonly, test stencil 0x08
draw 384   write stencil 0x80
draw 433   再次写 stencil 0x10
draw 626   depth readonly, Replace stencil 0x10
draw 802   depth disabled, test stencil 0x80
```

Donor模式改为在同一帧六个真实边界前分别绘制一个不同位置的三角形：

```text
306, 334, 378, 384, 433, 626
```

每个三角形均使用相同地板 G0..G4、真实 rasterized depth、`GreaterEqual`和真实地板 stencil `0x10`；唯一变量是注入 draw ordinal。ImGui标签直接显示 `Donor before draw N`。首帧仍只在 exit 捕获 donor，从下一帧开始执行 sweep。

这组边界覆盖普通场景stencil开始写入、材质分类切换、stencil只读阶段、角色分类、第二批场景写入和depth readonly末段。若其中存在正常受光的三角形，即可把最终 backend的注入点收敛到对应阶段附近；若六者仍均异常，则应继续向 candidate前的独立prepass追踪。

### Donor sweep 结果与 UAV 检查

draw 306、334、378、384、433、626 六个 donor三角形均为黑色，没有显著差异。结合 draw 0 的白色结果，可以确认单纯在五 MRT candidate内移动注入时点不能得到正常受光表面；继续细分 draw ordinal不再有足够信息价值。

此时需要检查此前被候选识别忽略的 `OMSetRenderTargetsAndUnorderedAccessViews` 参数。原生 opaque draw可能在五个 RTV之外写 pixel-shader UAV、材质分类或其他辅助输出，而插件command list只绑定了五 MRT/DSV。新增一次性日志：

```text
G-buffer UAV transitions: ordinal=0,
draw=N:start=S,count=C,uavs=[...]
```

记录candidate内最多32次UAV绑定变化；若没有则输出 `none`。同时修复 sweep索引曾被多次 `ClearRenderTargetView` 重置的问题，确保每个candidate只注入六次，并将candidate、transition、probe和sweep日志全部压缩为首次少量输出，避免持续刷屏。

若UAV transitions非空，下一步应识别并复制/写入对应UAV，而不是继续移动注入点。若为 `none`，主假设才转向五 MRT candidate之前的独立depth prepass、light-culling compute或其他非OM阶段。

### UAV 结果与 normal 最终对照

实机 `G-buffer UAV transitions: none`，排除五 MRT之外的 OM UAV输出。另修复 late donor skip 每帧刷日志：该预期分支现在完全静默；此前因为 `injectionCount`不增长而始终满足“前两次日志”条件。

在转向candidate之前的prepass前，还需分离最后一个未独立控制的变量：此前所有完整donor三角形都把合法地板G0 RGB替换为自定义三角形法线。如果法线坐标系、朝向或编码仍有误，它本身足以让完整donor被照黑。

六点sweep改成三组相邻配对：

```text
draw 306：完整donor，保留地板G0 RGB
draw 307：完整donor，替换为三角形法线
draw 378：完整donor，保留地板G0 RGB
draw 379：完整donor，替换为三角形法线
draw 433：完整donor，保留地板G0 RGB
draw 434：完整donor，替换为三角形法线
```

每对只相差一个draw和normal来源，标签分别显示 `floor normal` / `triangle normal`。若floor-normal版本正常而triangle-normal版本黑，问题回到法线协议；若两者均黑，normal、MRT、stencil、UAV和candidate内部时点均可排除，随后应正式追踪candidate之前的depth/light-culling阶段。

### Normal 对照结论与 pre-candidate 追踪

三组 floor-normal / triangle-normal donor均为黑色，无显著差异。至此 G-buffer tuple层调查结束，已排除：

```text
G0..G4原生地板内容
原样地板法线与自定义法线
G2 albedo
真实地板stencil
reverse-Z depth
OM UAV
candidate内部多个阶段
```

Donor sweep绘制已停止，Donor模式不再显示六个世界箭头或黑三角，只保留采样标记与捕获/追踪。新增长度32的OM绑定历史环形缓冲，统计所有 immediate-context draw，并在candidate首次开始时输出：

```text
G-buffer pre-candidate OM history:
serial=...,previousDraws=...,OMSetRT:rtvCount=...,rtvs=[...],dsv=...
|| ...
```

DSV clear也记录 flags、depth和stencil。目标是寻找candidate之前使用相同DSV的零RTV/少RTV pass及其draw数量。若存在明确depth-only prepass，下一实验应分两阶段：先在prepass写插件depth/stencil，再在五MRT candidate写材质tuple；若不存在，则继续追踪candidate之前的compute light-culling或其他非OM资源。

### Pre-candidate OM 结果与 G-buffer consumer 追踪

实机历史显示主场景DSV在五MRT之前的相邻序列是：

```text
3 RTV（对应最终G-buffer slot 0、2、3）+ 主DSV
→ ClearDSV(depth+stencil, depth=0, stencil=0)
→ 1个非G-buffer RTV + 同一主DSV，0 draw
→ 5个完整G-buffer RTV + 同一主DSV
```

`3 RTV`绑定到clear之间、clear到单RTV、单RTV到五MRT之间均没有draw。因此这里不是独立depth prepass，而是主opaque pass开始时的附件初始化/切换；当前证据不支持在它里面增加一轮插件depth draw。

下一追踪点改为五张G-buffer的首次SRV消费。新增 `PSSetShaderResources` 与 `CSSetShaderResources` hook，通过view底层resource identity将SRV映射回G0..G4，汇总记录：

```text
candidate内：candidate ordinal + candidate draw
candidate外：距离最近candidate exit的draw数
shader stage、SRV slot和G-buffer slot映射
```

日志只输出一次 `G-buffer SRV consumers: ...`。若首次消费发生在当前注入边界之前，说明candidate识别范围包含了G-buffer producer之后的阶段，应把注入点移动到首次consumer之前；若首次消费严格发生在exit之后，则继续追踪该consumer依赖的光照辅助资源生成时点。

### G-buffer consumer 实机结果与写后回读

首次consumer严格发生在candidate exit之后：

```text
exit + 0 draw：CS t1读取G3
exit + 1 draw：PS t0读取G0
exit + 32 draw：PS读取G0/G1/G2/G4
exit + 34 draw：PS读取完整G0/G1/G2/G3/G4
```

后续大量PS pass以不同slot组合持续读取G-buffer。没有任何G-buffer SRV消费发生在candidate内部。这证明当前exit注入在producer/consumer顺序上是成立的：插件command list执行完后，游戏才开始第一轮G-buffer消费。“lighting辅助数据已在注入前由G-buffer构建完成”不再是主要解释。

下一步改为验证插件draw的实际写后内容。Donor模式在已有snapshot后的下一次有效exit只绘制一个完整donor三角形；`ExecuteCommandList`返回后、游戏提交下一条命令之前，计算三角形内部的投影像素，并从原G0..G4和DSV各复制该1x1像素到staging texture后立即回读：

```text
G-buffer injected pixel readback at (x,y): G0=...,G1=...,G2=...,G3=...,G4=...,DS[...]=...,stencil=...
```

该验证只执行一次。若G0..G4与donor raw一致且stencil/depth也已改变，便可确认五MRT写入本身正确，下一调查目标转为G-buffer之外的独立lighting输入；若不一致，则先修正插件MRT绑定、pixel shader输出或采样点，而不再扩展pass追踪。

### 首轮写后回读、崩溃原因与安全修正

崩溃前实际已取得两次写后回读。示例：

```text
donor:   G0=83FF7880,G1=00D7D900,G2=10222FFF,G3=FF7B00000000003C,G4=7FFF7F00,DS=70FD0410
injected:G0=AC940A00,G1=00000000,G2=00000000,G3=0000000000000000,G4=00000000,DS=00000000
```

第二次也同样只有G0为另一非donor值，其余MRT和DS为零。这更像采样到了未被三角形覆盖的远平面/天空像素，而不是读到了插件输出；原验证仍混有CPU世界投影采样点和`GreaterEqual` depth是否通过两个变量，因此结果不足以判定MRT写入失败。

崩溃dump明确给出：

```text
SharpDX.Direct3D11.DeviceContext.UnmapSubresource
→ GBufferProbeController.LogDonorReadbackOnce
→ TryIssueProbe
→ OMSetRenderTargetsDetour
```

时间线上probe先被关闭，随后快速重新启用；渲染线程创建并Map新的donor snapshot时，配置更新线程执行状态重置并释放snapshot，最终在`UnmapSubresource`触发未处理异常。修正为：配置重置和完整probe提交/readback通过同一个 `probeStateLock`串行化，禁止snapshot在Map/Unmap期间被释放。

写后验证同时改为固定clip-space三角形：

```text
clip vertices = (-0.12,0.10), (0.12,0.10), (0,0.34)
sample        = (0.50W,0.41H)
depth         = Always + WriteAll
stencil       = donor reference + Replace
```

它继续完整输出donor G0..G4，但不再依赖世界矩阵、相机运动、场景遮挡或CPU投影。验证仍只执行一次。

固定clip-space + `Depth Always` 后，首次回读仍为：

```text
G0=E2842F00,G1=00000000,G2=00000000,G3=0000000000000000,G4=00000000,DS=00000000
```

由于几何覆盖与depth已从变量中排除，复查发现该回读没有复用已经成功的donor复制链。donor MRT读取采用：

```text
全尺寸source的1x1区域 → 1x1 Default texture → 1x1 Staging texture → Map
```

而写后读取直接执行“全尺寸source区域 → 1x1 Staging”，DSV也从全尺寸直接复制子区域到1x1 staging。返回的随机G0和其余零值可能是无效/未执行copy后的staging内容，不能作为MRT未写入的证据。

写后MRT回读现已改为与donor相同的Default中转路径；DSV则恢复为整张 `CopyResource` 到同尺寸staging，再按row pitch和 `(x,y)` 读取。下一次结果才可用于判断draw是否真正写入。

### 根因：WorldConstants 未绑定到 pixel shader

校准后的固定clip-space回读：

```text
G0=E482CE00
G1=00000000
G2=00000000
G3=0000000000000000
G4=00000000
DS=00008010
stencil=0x10
```

`R24G8` raw中的24-bit depth为 `0x800000`，正好对应固定clip-space `z=0.5`；stencil也正确写为donor值 `0x10`。这证明固定三角形确实覆盖了采样点，draw、depth和stencil提交均成立。

MRT形态则精确对应world pixel shader的非donor分支：G0写编码法线且alpha为0，G1/G3/G4清零，G2读取零值Albedo后也是零。复查绑定发现：

```csharp
VertexShader.SetConstantBuffer(0, worldConstantsBuffer);
PixelShader.Set(worldPixelShader);
// 缺少 PixelShader.SetConstantBuffer(0, worldConstantsBuffer)
```

同一个 `WorldConstants` cbuffer同时被VS和PS使用，但此前只绑定到VS。结果是：

- VS能读取ViewProjection与Diagnostic.x，所以世界投影和固定clip-space几何均可正常工作；
- PS的 `Diagnostic.y` 始终为0，永远不进入donor采样分支；
- PS的 `Albedo` 始终为0，所以WorldTriangle的G2红/绿/蓝/白/黑全部变成黑色；
- PS仍能读取插值法线，因此G0非零，恰好与写后raw一致。

这一个绑定遗漏完整解释了此前所有world/donor材质异常。已在常规world probe和保留的candidate内donor提交路径中补上：

```csharp
PixelShader.SetConstantBuffer(0, worldConstantsBuffer);
```

因此此前基于“完整donor仍黑”推出的材质tuple、stencil、注入时点、UAV或独立lighting输入假设不能继续视为有效负面证据；这些实验的PS从未实际输出预期donor tuple。矩阵、rasterized depth、stencil、producer/consumer顺序和G0/G2 screen-space独立探针结论仍然有效。

### 修复后的完整 tuple 写入确认

同一帧的donor与写后回读：

```text
donor:
G0=83FF7F80,G1=00D8F300,G2=232A34FF,G3=FF7B00000000003C,G4=7FFF7F00
DS=2FDE0410,stencil=0x10

injected:
G0=83FF7F80,G1=00D8F300,G2=232A34FF,G3=FF7B00000000003C,G4=7FFF7F00
DS=00008010,stencil=0x10
```

五张MRT逐字节完全相同；stencil同为 `0x10`。注入depth的24-bit值为 `0x800000`，与固定clip-space `z=0.5`完全一致。至此确认：

```text
donor snapshot SRV
→ world pixel shader完整五MRT输出
→ command list执行
→ 原G0..G4 + 主depth/stencil
```

整条写入链成立。固定clip-space校准draw与写后readback现已移除，Donor模式恢复为每帧在candidate exit绘制真实世界空间三角形，使用 `GreaterEqual + WriteAll`、完整donor G0..G4与donor stencil。ImGui世界箭头恢复，标签为 `Donor opaque tuple`。下一实机结果将首次有效验证完整原生opaque tuple的lighting表现。

### 首次有效 lighting 与 normal 对照

修复PS constant buffer后，世界donor三角形首次出现颜色并对场景光源产生响应，但高光/反射方向难以解释。这是当前输入的预期限制：为了验证完整tuple逐字节复制，三角形仍保留地板donor的G0 RGB，即接近世界 `+Y` 的法线；三角形自身则是面向相机附近的另一几何平面。lighting使用“新世界位置 + 地板法线 + 地板材质”，反射和局部灯响应不会与可见三角形平面一致。

Donor模式改为并排绘制两个材质完全相同的世界三角形：

```text
Donor floor normal    ：完整保留donor G0..G4
Donor triangle normal ：只用真实几何平面法线替换G0 RGB
```

两者继续使用各自真实rasterized depth和同一个donor stencil；其他所有MRT字段相同。若triangle-normal版本的光照方向明显合理，便可确认当前异常完全来自法线/几何不一致；若仍有无法解释的高光，再开始从donor tuple中隔离roughness/specular/reflection相关字段。

实机确认triangle-normal版本的光照方向合理，并且能够接收原生阴影。三角形不会阻挡shadow map中的光线，阴影仍会落到后方地面，这是因为它只参与主相机G-buffer，尚未参与更早的shadow caster pass；该问题暂不纳入opaque材质实验。

下一轮改为验证自定义albedo。六个三角形全部使用真实几何法线、相同donor G0 alpha/G1/G2 alpha/G3/G4、真实depth和donor stencil，仅控制G2 RGB：

```text
Donor albedo
18% gray       (0.18, 0.18, 0.18)
Red            (0.65, 0.05, 0.05)
Green          (0.05, 0.65, 0.05)
Blue           (0.05, 0.05, 0.65)
White          (0.75, 0.75, 0.75)
```

pixel shader先加载完整donor tuple并替换G0 RGB为几何法线；除基线样本外，再以 `Diagnostic.w`控制是否用 `Albedo.rgb`覆盖G2 RGB，G2 alpha保持donor值。该矩阵用于确认颜色通道、阴影结构、局部光响应以及高光是否基本独立于albedo。

实机结果：颜色可明确区分且符合预期；不同颜色与场景光颜色的叠加自然，局部光响应正常。由于六个样本位于不同世界位置，无法用它们比较同一片外部阴影；地板donor偏哑光，也没有观察到足够明确的高光差异。结论是G2 RGB可作为正式自定义albedo输入。

`FFXIV-TV`未提供roughness/specular通道解码；Pictomancy只明确标注G0为world-space normal、G3为scene-info分类。下一轮转向G1，但先不修改更可能承载分类的alpha。六个样本统一使用：

```text
真实几何法线
G2 RGB = (0.35,0.35,0.35)
其余完整donor tuple
donor stencil + 真实depth
```

仅改变：

```text
G1 donor
G1.R = 0
G1.R = 1
G1.G = 0
G1.G = 1
G1.B = 1（donor B约为0）
```

shader通过独立 `MaterialOverride` 与 `MaterialMask` 对G1执行分量级替换。测试时应移动相机并靠近明显局部灯，观察高光宽度、强度、环境反射、整体明暗或材质分类变化；R/G的0/1对照优先用于定位roughness与specular，B只做第一轮单向探针。

实机中G1.B=1整体接近纯黑、光源响应显著减弱但并非完全消失，因此B更像lighting model、分类或mask，而不是普通连续roughness参数；后续保持donor B。G1.G=0出现类似镜面或折射的强烈性质，但当前无法区分它究竟控制roughness、reflection、transmission还是某种特殊lighting分支。其余样本大致都是中性灰，G1.R=1只在背面出现轻微绿色。当前平面、纯色和不可控光源不足以可靠命名R/G/B，这里只记录现象，不继续推断。这里的“材质参数”与“有UV纹理的表面材质”概念不同，因此停止继续盲扫G1；roughness/specular若继续研究，应改用球体或其他法线连续变化的曲面。

下一里程碑改为带纹理的opaque表面：

```text
世界空间倾斜四边形（两个三角形）
Position + Normal + UV
插件自有256x256 RGBA测试纹理
线性过滤、Clamp寻址
G0 RGB = 真实平面法线
G2 RGB = TestAlbedo.Sample(UV).rgb
其余G-buffer = 完整donor tuple
真实depth + donor stencil
```

测试纹理由8x8明暗棋盘、白色网格和四象限红/绿/蓝/黄tint组成，便于判断UV方向、三角形接缝、透视插值和颜色响应。纹理alpha固定为1且本轮不写入G2 alpha；透明/cutout不在本轮范围。ImGui只显示一个 `Textured opaque quad` 世界箭头。

实机结果自然：四象限方向正确，棋盘与网格连续，两个三角形之间没有可见对角接缝；倾斜平面呈现合理的透视变化。纹理颜色接受场景光源及其颜色叠加，原生角色/武器和场景几何能够按depth正确遮挡四边形。至此确认首个实用opaque backend基线：

```text
自定义world-space mesh
+ perspective-correct UV
+ 插件自有颜色纹理
+ 自定义几何法线
+ donor安全opaque tuple
+ 主场景depth/stencil
→ 原生deferred lighting与遮挡
```

后续运行时调参补充了以下通道语义：

```text
G1 RGBA：共同影响粗糙度、高光强度、镜面和漫反射等表面光照行为，具体分量映射尚未确定
G3 B   ：镜面反射率类参数，精确定义尚未确定
G4 RGB ：自发光颜色
G4 A   ：自发光强度
```

因此运行时材质编辑器按上述粒度命名；尚未确认的分量继续标记为未知，不提前固化更具体的材质模型解释。

### Pictomancy opaque backend 最小迁移

原先由 EventHorizon `GBufferProbeController` 自己持有的 OM hook、candidate 匹配、MRT/depth状态、shader和跨帧纹理引用，已迁移到 `ffxiv_pictomancy` 的可选 `OpaqueGBufferBackend`。公开的最小入口为：

```text
PctOptions.EnableOpaqueGBufferBackend
PctService.DrawOpaque()
PctService.OpaqueMaterial
PctOpaqueDrawList.AddTriangleFilled
PctOpaqueDrawList.AddQuadFilled
PctOpaqueDrawList.AddImage
```

`PctOpaqueDrawList.Dispose()`发布一份不可变命令快照，backend在下一次主G-buffer candidate退出时消费。图片SRV在发布期间持有COM引用；新快照替换旧快照或调用 `ClearOpaque()` 时释放。该路径只承诺opaque/cutout语义，alpha使用0.5阈值，不提供透明混合、`PctTexture`输出、shadow caster或motion vector。

EventHorizon侧的Controller现只负责创建测试纹理、确定测试四边形的世界位置、调用 `DrawOpaque().AddImage(...)` 和提供ImGui箭头；不再拥有任何G-buffer hook或MRT写入代码。
