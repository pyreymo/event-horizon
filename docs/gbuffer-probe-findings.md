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

1. `GBuffer 2 RGB` 是 diffuse/albedo 类数据。
2. 它与 lighting 分离存储，而不是已经包含最终光照结果。
3. 当前 candidate exit 位于 deferred lighting 之前，注入的 albedo 会被 FFXIV 原生光照消费。

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
