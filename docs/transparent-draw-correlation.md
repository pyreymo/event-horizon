# TransparentDrawCorrelation / material submission probe 交接说明

日期：2026-07-18

## 外部评审摘要

### 当前结论

已经找到并在实机上验证一个足够高层的原生 geometry/material builder：`ffxiv_dx11.exe+0x281DD0`。它的当前推定签名为：

```csharp
nint Builder(
    ModelRenderer* modelRenderer,
    ModelRenderer.OnRenderModelParams* param,
    ModelResourceHandle* modelResource,
    uint geometryIndex,
    int flags
);
```

一次正常调用会从 model resource 的 geometry entry 解析 material，并在当前线程的 `Graphics::Kernel::Context` 中生成完整的多 pass、多 view command 集合。对目标 `charactertransparency.shpk` geometry，主 build 稳定生成六条 `DrawIndexed` command：四条属于 View 30.12，另两条属于 View 32.11 / 33.11。主 view 中已经确认包含 Stage A、两族 Stage C 和一条条件式或辅助 command。

一次性 PoC 在原 hook、原线程、原参数和原 context 范围内再次调用同一 builder，成功生成第二组独立的六条 command。两组 command 都被原生 consumer 排序、被 executor 执行，并在 D3D 层形成成对的 Stage A 和两族 Stage C draw。因此目前最重要的架构结论是：

> 插件可以调用一次高层原生 builder，让游戏自动展开完整透明提交；不需要分别伪造 Stage A/C packet。

### 证据等级

已确认：

- `ModelRenderer.OnRenderMaterial` 的唯一直接调用者位于该 builder 内；
- builder 同时持有 model、geometry entry、material、view/context 和 command 输出 arena；
- builder 通过 `Context.AllocateCommand` / `PushBackCommand` 生成真正被后续 consumer/executor 使用的 command；
- 相同输入第二次调用会重新分配 command/payload，而不是复用第一次的 command；
- 第二次调用自动复制 Stage A、两族 Stage C 和 View 32/33 辅助提交；
- 普通只读 capture 与显式重复提交 PoC 相互独立，旧 Underpaint 近似后端没有参与或被修改。

transform 边界已经确认：

- 目标衣服走 skinned 分支。builder 不为它创建 world constant，也不读取一个可替换的 transform 参数；它直接绑定 Model-owned `BoneList` 中已经生成的 current/previous joint palette。
- joint palette 在 builder 之前由共享 `Skeleton.Transform`、共享 pose、bind/inverse-bind 数据、camera view 和可选 post-bone deformation 生成。
- CharacterBase 在调用 Model submit virtual 之前已经用角色 bounds / view distance 算出透明 sort depth，并把 object constant 与 packed sort/object 数据复制进异步 job。
- `OnRenderModelParams` 只是 272-byte frame/job-arena record 的 0x20-byte 前缀，不拥有 transform、bounds 或 palette；浅复制它不会产生独立实例。
- 因此当前没有“复制这件 skinned 衣服的调用级 input 后改 transform即可完整重算”的安全边界。这件衣服作为 transform donor 的小偏移 PoC 按情况 C 停止；该结论不终止 Underpaint 原生后端，后端主线转向非 skinned、独立 transform 的最小原生 Model/geometry 载体。

仍未确认的只剩当前 RVA、signature 和未公开字段偏移在客户端更新后的稳定性；这不改变本客户端版本上的停止结论。

### 希望 reviewer 重点检查

1. `0x281DD0` 作为“原生 Model/geometry/material -> 完整多 pass command”入口的调用约束是否已经识别完整。
2. 非 skinned 分支的 `Model+0x38` transform object 能否作为独立静态载体的 current/previous transform owner。
3. 最小原生 Model/geometry 外壳中，哪些字段必须原生创建，哪些 geometry buffer/range 可以安全替换为 Underpaint-owned VB/IB。
4. builder 返回的 `0x2` 来自局部 shader-key/guard 对象的清理返回值、调用者只透传最后一次 geometry 结果且不分支的判断是否正确。

### 代码位置与版本

- producer/consumer/executor probe：`EventHorizon/Integration/Debug/MaterialSubmissionContainerProbe.cs`
- 捕获控制、日志和 D3D 关联：`EventHorizon/Integration/Debug/TransparentDrawCorrelationTracer.cs`
- Debug UI：`EventHorizon/Playground3D/DemoWindow.cs`
- 分支：`3d-playground`
- executor 探针提交：`ead45e5`
- 一次性重复提交 PoC：`3cefc4a`
- 主 view 触发条件修复：`30c1d0f`

所有 native 地址和实机数据均来自 2026-07-18 客户端；RVA 不应被视为跨版本 API。

## 本轮状态

旧的 Underpaint 手写 Stage A/C 近似后端已经冻结，只保留为实验对照。临时 `Skip Stage A` A/B 开关已经删除。`agent/stability-probes` 中已经验证过的 pass 退出注入、viewport/scissor、投影与 depth-bias 稳定性修复继续保留，因为它们用于避免旧后端污染后续实验判断，不代表继续扩展近似材质系统。

新的 `TransparentDrawCorrelation` 路径只在 Debug 构建中存在。普通 `Arm transparent capture` 仍严格只读；另有显式的一次性原生重复提交实验，默认不启用：

- EventHorizon 在 framework/game-thread 时点从当前目标的固定 Slot 1 生成不可变 donor 快照；
- hook `ModelRenderer.OnRenderMaterial` 的唯一直接调用者，并以 Slot 1 的 `Model*`、MaterialIndex 2 和 `charactertransparency.shpk` 三重过滤；
- Underpaint 对原生半透明 Stage A/C 的 draw 做有界快照；
- 捕获 VB/IB、draw range、VS/PS/input layout、VS/PS constant-buffer 指针及有限 hash、PS SRV 的底层 resource 身份、线程、单调时间和 native module+RVA stack；native stack 从反向调用边界的第 0 帧开始，无法展开时写入明确原因而不再留空；
- 捕获最多 4 个 Stage A 帧、128 个 draw、128 个目标 material build 或 15 秒；draw 预算在 A/C 两侧各保留一半，避免 Stage A 抢完预算；任一边界到达后自动停止；每个 shader stage 每个 draw 最多只 readback/hash 一个有限大小的 constant buffer，避免无界 GPU 同步；
- matcher 只使用 Slot 1 donor texture，并同时结合 geometry、draw range、constant hash 和共享资源评分，不以 VB/IB 或 index count 单独判定；
- 普通 `Arm transparent capture` 全程不写队列、不额外调用 builder，也不跨帧保存可供异步解引用的原生指针；只有用户显式点击 `Arm one native duplicate` 才会在原 builder detour 内执行一次额外调用。

日志 source 固定为 `TransparentDrawCorrelation`，写入现有 `debug-logs/event-horizon-*.log`。

## 静态分析结论

先用 FFCS 确认公开结构和 `Graphics::Kernel::Context`，再因调用者和控制流信息不足，使用当前 2026-07-18 客户端的 IDA 数据库补齐直接调用关系。以下 RVA 只对该客户端版本有效：

- `ModelRenderer.OnRenderMaterial`（`ffxiv_dx11.exe+0x281540`）只有一个直接调用点：`ffxiv_dx11.exe+0x281EB7`，位于 geometry/material builder `ffxiv_dx11.exe+0x281DD0` 内。
- builder 从 `ModelResourceHandle+0xE8` 的 0x24-byte geometry 表读取当前 entry，从 `entry+8` 取得 material index，再从 `Model.Materials[index]` 取得 `Material*`。
- 调用者不读取 `OnRenderMaterial` 返回寄存器。当前 Human callback 主要修改线程私有 graphics context 的材质/GPU 状态和 `Params2+0x40` 控制 flags；若 callback 填充可选输出描述，wrapper 会把 stack-local 的 `+0x08/+0x10/+0x18/+0x20` 复制到 `Params2+0x10/+0x18/+0x20/+0x28`。当前 Human 路径静态上未见它填充这些可选字段。
- builder 后续同时使用 model、material、geometry entry、camera/view 状态、skeleton/constant buffers，并进入不同的 command builder 分支。
- 这些分支最终调用当前线程 `Graphics::Kernel::Context.AllocateCommand` / `PushBackCommand`。因此当前最可信的输出容器不是长期 render-item 数组，而是线程私有 `Context` 的 command arena 和 payload arena。
- builder 的上一层是遍历 model geometry 的 render job；再上一层负责建立并提交异步 job。只读阶段没有调用或复制任何 builder/packet；后续 PoC 仅在原 builder detour 内做了一次相同输入的重复调用，没有复制最终 packet。

## 2026-07-18 transform 输入边界静态分析

本轮继续先用 FFCS 确认 `ModelRenderer`、`Model`、`Skeleton`、`Transform`、`CharacterBase` 和 `DrawObject` 的公开布局，再用 IDA 从实际绑定的 constant resource、bounds 和 SortKey 反向追踪。结论针对当前目标 Slot 1 / MaterialIndex 2 / `charactertransparency.shpk`。

### 调用与生命周期

- `ffxiv_dx11.exe+0x280F40` 从 TLS job arena 分配 8704-byte block，每块容纳 32 个 272-byte record。它把 `Model*` 写到 `+0x00`、flags/LOD 写到 `+0x08/+0x0C`，再把上层给出的 16-byte payload 复制到 `+0x10`。
- `OnRenderModelParams` 正是该 272-byte record 的 0x20-byte 前缀，所有权为当前 frame/render job；不是 stack-local，也不是长期对象。它只可在当前 job/hook 生命周期内使用。
- job executor `ffxiv_dx11.exe+0x281AE0` 先调用 Model 的 `RenderModelCallback`，再遍历 geometry 调用 `ffxiv_dx11.exe+0x281DD0`。Human callback `ffxiv_dx11.exe+0x433270` 把 record `+0x10` 的第一个 qword 绑定为角色 object constant，并设置其他 TLS graphics state。
- `ffxiv_dx11.exe+0x281DD0` 对每个 geometry 调用 `OnRenderMaterial`，但不读取其返回寄存器；返回的 `0x2` 来自函数末尾局部 shader-key/guard 的清理 helper。`0x281AE0` 只让每次 geometry 的返回值覆盖前一次并最终返回最后一个，不比较、不累加，也不把它当作 item count。

### current / previous transform 与 palette

builder 有两个互斥分支：

1. 非 skinned Model 的 `Model+0x38` 非空时，builder 在 `0x28202B` 绑定该长期对象 `+0x20` 的 128-byte constant buffer。该对象的 update virtual `0x266DD0` 用对象 `+0x30` position、`+0x40` rotation、`+0x50` scale 和当前 camera view 生成 current 64 bytes；previous 64 bytes来自对象 `+0x60..+0x9F` 的上一帧缓存，首帧标记 `+0xA0` 令 previous=current。builder 不生成或上传它。
2. 本次目标衣服属于 skinned Model，`Model+0x38 == 0`。builder 从 geometry entry 选择 `Model.BoneList[paletteIndex]`，把 `BoneList+0x90 + ringIndex*8` 绑定为 `JointMatrixArray`，把上一 ring slot（首帧/刚更新时可退回 current）绑定为 `JointMatrixArrayPrev`。这就是 Stage A、两族 Stage C 和辅助 view 共用的 current/previous world-bearing constant；它们没有独立的 per-command world 副本。

`BoneList` 是 224-byte、Model-owned、注册到 `PostBoneDeformerBaseUpdater` 的长期对象。它在初始化时从共享 `Skeleton.Transform` 复制 position (`Skeleton+0x20`)、rotation (`+0x30`) 和 scale (`+0x40`)，持有 Skeleton ref，并为四个 ring slot 各创建 palette buffer。update virtual `0x267D70`：

- 从共享 Skeleton pose 取得每根骨骼的 model-space transform；
- 与 model resource 中的 bind/inverse-bind 数据组合；
- 把 Skeleton transform 与当前 camera view 组合后乘入每根骨骼矩阵；
- 写入当前 ring palette，更新 current/previous 有效位，再由 builder 选择 current 与 previous resource。

可选 post-bone deformation 指针会覆盖这份对象的局部 transform/pose 来源，但仍是长期、ref-counted、由 updater 调度的对象，不是 job-owned value。代码中未发现独立 normal/world-inverse constant；skinned shader 的 normal/world 变换随同一 joint palette/bind 数据完成。

### bounds、sort 与 object/history 数据

- Human 的 `ComputeAxisAlignedBounds` virtual 直接返回 CharacterBase `+0x1E0..+0x1EF` 的 16-byte bounds。该值在 Model submit/builder 之前已由 CharacterBase 更新链准备好；builder 不读取或重算它。
- Human submit loop `0x433FE0` 在调用 Model virtual slot 5 (`0x276190`) 前，优先取 attach bone 179 的世界位置与当前 view camera 算距离；没有该 bone 时使用上层传入的距离。它结合角色半径/偏移算出量化透明 depth，再加 Human slot-specific stable offset (`0x448A90`) 并打包进 16-byte job payload 的第二个 qword。
- payload 第一个 qword是 CharacterBase `+0x270` 的 CharacterData constant buffer。Human render-model callback 在 builder 之前把它绑定到 TLS；第二个 qword包含量化 sort depth、slot/order bits、outline/visibility bits和 CharacterBase `+0x95C` 的高 32 位 object 数据。它是 per-submit value，但不包含可重建 palette 或 bounds 的 transform。
- temporal identity 不以单独的 `historyIndex` 参数进入 builder。world history 由非 skinned transform 对象自身的 previous matrix/首帧位表达；本目标的 history 由 `BoneList` 对象身份、四槽 ring index和 current/previous 有效位表达。共享 Skeleton pose 与原角色 history 都不能通过浅复制 job 隔离。

### 字段分类

| 类别 | 当前确认字段/对象 |
| --- | --- |
| 不可变资源引用 | `ModelResourceHandle`、geometry entry、`Material*`、shader package、bind/inverse-bind 数据、Model material/geometry tables |
| per-instance 输入 | CharacterBase CharacterData CB、16-byte submit payload、view/slot/flags；但没有独立 transform value |
| current/previous temporal 输入 | 非 skinned transform object 的 current/previous matrix；本目标 `BoneList` 的 current/previous joint-palette ring 和有效位 |
| bounds/culling/sort 输入 | CharacterBase 16-byte bounds、attach-bone/world distance、slot stable offset、packed sort depth/object bits；均在 builder 前生成 |
| 可变 scratch | stack-local 0x48-byte `OnRenderMaterialParams2`、local shader-key guard、TLS graphics bindings/sort/view state |
| 输出 | 当前线程 `Graphics::Kernel::Context` command arena、payload arena和 sorted command groups |
| 共享状态 | `Model`、`Material`、shared `Skeleton` pose/transform、CharacterBase bounds/CharacterData CB、Model-owned `BoneList` palette resources |
| 隐式 thread/context 状态 | TLS `Graphics::Kernel::Context`、current view/subview、camera、SortKey、PostBone updater ring index、frame/job arena |

### builder 读写与副作用

- builder 读取 `ModelRenderer` 的 sampler/scene/subview keys和 shader handles；读取 `OnRenderModelParams` 的 Model、LOD/flags和 packed payload高位；读取 Model/ModelResource geometry、materials、BoneList/palette和附加 resources。
- 它修改 stack-local `OnRenderMaterialParams2`、TLS graphics bindings/keys、command/payload arena及 command group。command builders会临时切换 view/sort/GPU state并恢复其明确保存的字段；外层 job结束时清理 callback/TLS入口状态。
- 静态上未见 builder 修改共享 Model、Material、Skeleton transform/pose、BoneList palette/history或 CharacterBase bounds。除 command arena及同线程 TLS state外，未发现影响下一帧 geometry/history 的写入。
- 相同输入重复调用生成独立 command/payload allocation，但两组命令引用同一 CharacterData CB和同一 current/previous joint palette。地址独立不等于 per-instance数据独立。

### donor-specific 决策与后端主线

仅“复制当前 skinned 衣服并给副本偏移 transform”属于情况 C，不实现这个 donor-specific PoC：

- 在 builder 层复制 `OnRenderModelParams` 只能复制 Model 指针、CharacterData CB和已计算的 sort payload；palette、bounds和history仍属于 donor。
- 上移到 `0x281AE0` caller 或 `0x280F40` job producer仍得不到完整实例输入，因为 palette已在 PostBone updater中生成，bounds/sort已在 CharacterBase submit loop中生成。
- 再上移只能通过共享 CharacterBase/Skeleton transform/pose、共享 bounds/history，或自行创建并正确调度一套 Model/BoneList/palette/CharacterData/bounds/sort 生命周期来表达副本。前者违反“不修改后恢复共享状态”，后者已不是可复制的调用级 input，也没有已确认的安全 frame lifetime/release boundary。

这不是 Underpaint 原生后端的终止结论。`0x281DD0` 已经解决了正式后端的关键问题之一：给它一个完整原生 Model/geometry/material 输入，游戏会自动展开 Stage A、两族 Stage C 和辅助 view。当前缺口是给这个 builder 提供一个由插件独立拥有、能承载 Underpaint geometry/transform 的最小原生载体，而不是继续复制这件衣服。

后续主线改为：

```text
非 skinned 静态 Model
  -> 独立 Model+0x38 transform/history object
  -> 最小且独立的 native geometry entry
  -> 接入 Underpaint-owned VB/IB、draw range 和 bounds
  -> 在原 builder hook / 原线程 / 原 context 内调用 0x281DD0
  -> 验证游戏仍自动生成完整 Stage A / Stage C / 辅助 view
```

下一轮静态分析优先从已经发现的非 skinned 创建路径、`Model+0x38` transform object 和 geometry resource ownership开始；不再扩展衣服的 Skeleton/BoneList分析，不恢复通用 tracer，不 patch command packet，也不跨帧保存临时 native pointer。

### Render::Model wrapper、BgObject 分流与失败探针纠正

FFCS 给出 `Model.ModelDrawInit` 后，IDA 确认以下底层行为：

- `ffxiv_dx11.exe+0x2B86F0` 分配 0x180-byte `Model`，调用构造函数和 `ModelDrawInit`，然后接管调用者提供的 `Model+0x38` owner；初始化失败时走析构/引用计数路径。
- `ModelDrawInit`（`ffxiv_dx11.exe+0x273CE0`）对 `ModelResourceHandle` 增加引用，建立 Materials 数组，并从 mdl data 初始化 `Model+0x88` 的 per-geometry draw-count表及其他模型状态。
- `ffxiv_dx11.exe+0x2B8320` 才是 0xB0-byte transform/history owner 的创建函数；它初始化 position/rotation/scale、previous/history缓存并创建 128-byte constant buffer。

但 `0x2B86F0` 没有任何已确认的代码调用者、虚表或函数表引用，IDA只见 PE unwind/CFG元数据。它只能称为 `Render::Model` 初始化 wrapper，不能称为安全公共工厂，也不能据此判断 Scene对象类别。

BgObject 路线已经单独闭环：其实际 `UpdateRender`（`ffxiv_dx11.exe+0x452E50`）读取 `BgObject+0x90` 的 `ModelResourceHandle`，并调用 `Manager.BGInstancingRenderer` 的虚函数创建实例及其他 BG专用render object。它不创建这里讨论的 `Render::Model`，也不经过 `0x281DD0`。因此旅馆、城市中的建筑和摆件不会帮助原 ModelRenderer探针命中 `Model+0x38`。

builder 对 geometry 的最终读取同时跨越三处：0x24-byte geometry entry提供 material、palette、start/base等字段；`Model+0x88`表提供当前geometry的 draw count；`ModelResourceHandle+0x190`表提供 per-geometry vertex declaration。后者被写入 `Context+0x890`，并不是拥有整套 geometry 的单一 binding对象。

先前一次性 `Capture native static carrier` 同时犯了两个错误：把 BgObject静态场景当作 ModelRenderer输入来源；并在 builder只检查 `Model+0x38` 的情况下，额外要求 `Skeleton == null && BoneList == null`。两次旅馆/海都实测均得到 `no-static-carrier-found`，且旧日志没有记录总调用数和拒绝原因，不能区分“没有进入builder”和“被额外条件过滤”。该探针已删除。

替代探针 `Profile ModelRenderer inputs` 已完成使命并删除。两秒实测统计了所有真实 `0x281DD0` 输入：

- builder调用总数和唯一 Model数；
- `Model+0x38`、Skeleton、BoneList八种组合的命中数；
- 最多16个 `Model+0x38 != 0` 候选的 Model/resource/geometry/slot、world CB和同步复制的transform数据。

结果为 `Calls=165095 / UniqueModels=313 / T0-S1-B1=165095 / TransformCandidates=0`，其余七种组合均为零。即当前实机全部 ModelRenderer输入都有 Skeleton/BoneList，且 `Model+0x38` 始终为空。自然非 skinned carrier路线据此终止；不再寻找 Model工厂或静态场景 donor。

随后对 `0x281AE0 -> 0x281DD0 -> 0x283320` 的静态追踪把 geometry输入边界拆开了：

- caller `0x281AE0` 在进入 geometry循环前将 `Model` 的一个原生 buffer写入 `Context+0x888`，并将 `ModelResourceHandle` 的同一个原生 buffer写入 stream 0/1（`Context+0x8C0/+0x8D0`）；
- 每次 `0x281DD0` 调用只把当前 geometry 的 vertex declaration写入 `Context+0x890`；
- `0x283320` 及其 pass helper接收 material/pass参数和 `count/start/base` 标量，从上述 Context状态生成各 view的命令；
- 底层 `0x23B920` 会将 vertex declaration和紧随其后的16-byte stream bindings复制进命令分配区。

因此 `0x281DD0` 的 pass展开并不要求一个额外的静态 `Render::Model` carrier。Underpaint当前真正缺少的是：把自有 D3D11 VB/IB安全包装成游戏的 Kernel buffer，并提供匹配的 vertex declaration/stream binding。现有目标捕获增加一条 `SubmissionGeometry`，只同步记录 `Context+0x888/+0x890/+0x8C0` 及两个原生对象的有限qword快照，用于映射 wrapper中的 D3D resource和stride/offset；不增加新的 consumer/executor tracer。

## 新运行时探针

每次命中只记录目标角色 Slot 1 / MaterialIndex 2 / `charactertransparency.shpk`：

- build cycle、时间戳、原生 caller RVA、线程；
- `ModelRenderer*`、`OnRenderModelParams*`、`Model*`、`ModelResourceHandle*`、geometry index/entry/hash、`Material*` 和 `MaterialResourceHandle*`；
- 当前线程 `Context*`、view/sub-view/sort key；
- command arena 与 payload arena 调用前后的 base/used size，以及本次新增范围和有限 hash；
- builder 范围内每次 `AllocateCommand` 返回的地址、大小和 builder 返回时的有限 hash；
- `OnRenderMaterial` 返回值，以及完整 0x48-byte `Params2` 调用前后 hash、变化 offset 和 qword 快照。
- 当前 geometry 的原生 index-buffer wrapper、vertex declaration、四个 stream-binding qword pair及有限对象快照。

这些字段已经依次完成 producer、consumer、executor 和重复提交验证；下文保留各轮证据。

## Debug DLL 的人工步骤

1. 准备一个可选中的 PC，推荐自己的角色。
2. 只保留一件容易识别的 `CharacterTransparency` 透明装备，并记下物品名和装备槽。
3. 暂时关闭该 donor 的 Penumbra 模型/材质替换。
4. 选择安静普通场景，固定镜头和姿态，避开水体、雨淋、湿身、Gpose 和持续动作。
5. `/eh 3d` 打开 Underpaint Demo，展开 `Transparent draw correlation`。
6. 选中 donor，确认摘要显示 `slot 1` 且 material/texture 数量合理；如需只读基线，先点 `Clear EventHorizon logs`，再点 `Arm transparent capture`。
7. `Arm one native duplicate` 会执行一次真实的额外原生提交，只用于复现实验，不应在普通游戏期间启用。
8. 当前透明衣服重复提交和 Model input profile均已完成，不再点击 duplicate或寻找静态摆件。下一次只需选择原 donor、清空日志并点一次 `Arm transparent capture`，提供其中的 `SubmissionGeometry` 行。

下文按采集轮次保留了当时的待验证问题；最新结论和下一步见文末。

## 2026-07-18 producer 实机结果

同一日志包含透明衣服和非透明替换装备两次采集：

- 透明衣服的 Slot 1 有 `skin.shpk`、`character.shpk`、`charactertransparency.shpk` 三个材质，目标材质稳定为 index 2；探针命中 10 次。
- 替换装备的 Slot 1 没有 `charactertransparency.shpk`，探针命中 0 次，证明 model/material/SHPK 过滤没有串到同角色其他材质。
- 每次命中的 model、material、geometry index 2、geometry entry 和 geometry hash 完全一致。
- producer 稳定形成两种形状：View 30 / SubView 11 每次新增 3520 bytes、12 次 allocation；View 1 / SubView 9 每次新增 1056 bytes、6 次 allocation。
- 两种形状中 allocation 都以 176-byte 块与附属块交替出现。View 30 有 6 个 176-byte 候选 command，View 1 有 3 个；这强烈暗示一次目标材质展开已经生成多份 pass-specific draw command，而不是一个稍后才被无限展开的单 item。
- `AllocationBase/AllocationUsedSize` 始终没有变化；本路径的有效输出全部位于 command arena。
- `OnRenderMaterial` 返回寄存器稳定为 `0xC9`，但调用者仍不使用它。`Params2` 只有 `+0x40/+0x41` 改变，flags 从低位 0 变为 `0xC96`；可选输出描述保持全零。

FFCS 和 IDA 随后确认 `Context.PushBackCommand` 会把 `{SortKey, Command*}` 写为 16-byte group；`ImmediateContext.PreprocessCommands/ProcessCommands` 稍后消费排序后的 group，且命令按 SortKey 而不是生产顺序执行。下一版窄探针因此只增加：

- 在目标 builder 范围内记录真正传给 `PushBackCommand` 的 command 地址、type、SortKey、view 和 allocation size；
- 在 `PreprocessCommands` 输入中按同一 command 指针定位排序后的 buffer/group index；
- 对 draw command 直接解析 count、start index、base vertex、instance count，并保留 bounded payload hash/qword 快照。

这一轮结束时，下一次采集的唯一目标是把 producer 的 6/3 个 pushed command 逐项对应到现有 Stage A/C draw range。该目标已在后续轮次完成。

## 2026-07-18 command consumer 实机结果

第二轮日志取得 10 次 build、46 条 consumer 记录。其中 45 条是有效的一一关联，另 1 条明确暴露 frame-arena 地址复用：旧 command 地址后来对应 `Count=36` 的其他命令，且 GroupSort 已不再等于原 ProducerSort。后续探针因此把 command type 与 SortKey 一并作为生命周期校验，并在发现复用或执行完成后移除旧地址。

有效结果稳定重复：

- 所有 pushed command 都是 type 6，即原生 `DrawIndexed` command；
- 所有命令的 draw range 都是 `Count=2280 / StartIndex=12560 / BaseVertex=0`，与目标材质现有 Stage A/C draw 完全一致；
- 原先称为 View 30 的 3520-byte build 实际 push 六个命令：四个进入同一 745-entry 主 buffer，排序后 group index 固定为 201、312、366、370；另外两个分别进入 View 32 和 View 33 的 14-entry buffer，index 都为 5；
- 原先称为 View 1 的 1056-byte build 实际 push 三个命令，进入 View 65 的 48-entry buffer，sub-view 7/8/9 的 group index 固定为 6、23、38；
- 主 buffer 的四个命令按 SortKey 排列为 push 0、2、1、3，证明不能使用 builder 内的 push 顺序推断最终 pass 顺序；
- D3D 侧稳定看到同一 range 的一个 Stage A 和两个 Stage C draw，因此九个原生命令中至少六个属于当前 A/C capture 范围之外的 view 或辅助提交。

这一轮确认 builder 直接生成完整的多 view、多 pass `DrawIndexed` command 集合；当时剩余的证据是主 buffer 四个命令与 Stage A、两族 Stage C 及辅助路径的映射。该映射已在下一节完成。

下一版仍不扩建通用 tracer，只 hook type 6 执行前已经存在的状态准备 helper，并只对已登记 command 生效。它记录 command 执行时间以及当时的 RT、depth-stencil、VS/PS、blend/depth/rasterizer、topology、scissor 和 index-buffer 状态，可直接与紧随其后的 `DrawIndexed` 时间及 Stage A/C 资源状态对应。

## 2026-07-18 command executor 实机结果

第三轮日志取得 20 次 build、55 条有效 consumer 和 55 条 executor 记录。场景中出现了额外的 View 66/67/68，说明辅助 view 集合会随场景变化；主 View 30.12 的形状仍逐帧稳定。

主 view 四条 command 与实际 draw 的时间和状态已经闭环：

- push 0 / Sort `0xC4075C7F`：四个 G-buffer RT 加 depth，稳定对应目标 `StageA` draw；
- push 2 / Sort `0xC8075C7F`：单 RT，稳定对应第一族目标 `StageC` draw；
- push 3 / Sort `0xCB7FFFFE`：同一单 RT，稳定对应第二族目标 `StageC` draw；
- push 1 / Sort `0xCB075C7F`：进入 executor 并准备完整 GPU 状态，但本次没有落到目标 geometry 的 D3D draw，保留为条件式或辅助 command，不为它强行命名。

因此已经确认：一次 `ffxiv_dx11.exe+0x281DD0` geometry/material builder 调用，在同一线程的 `Graphics::Kernel::Context` 中自动生成 Stage A、两族 Stage C 和辅助提交；插件不需要分别伪造 A/C packet。

基于第三轮结果，当时的下一步是受控的首次写入 PoC。Debug UI 的 `Arm one native duplicate` 只在目标角色 Slot 1、MaterialIndex 2、`charactertransparency.shpk` 且第一次 builder 调用实际生成主 View 30.12 command 时消费一次 arm 状态。它先执行正常 builder，再在原 hook、原线程、原参数和原隐式 context 中额外调用一次原 builder；不修改 Material、transform 或已生成 command。日志额外记录：

- 是否实际执行重复提交；
- 第一次调用结束时的 command arena 边界和 push 数量；
- 两次 builder 返回值与 `OnRenderMaterial` 调用次数；
- 重复调用新增 command 的 push index、地址、sort/view、消费和执行状态。

该轮人工采集只需点击 `Arm one native duplicate`，验证第二次 builder 调用是否在原有六条主 build command 后再生成同形的六条 command，并分别再次进入 Stage A、两族 Stage C 和相同辅助 executor 路径。最终成功结果见后文。

首次 PoC 采集没有执行重复调用：25 个 build 全部为 `Applied=false`，60 条 command 恰好是五帧正常路径的 `5 × 12`。原因是 builder 入口 context 为 View 30 / SubView 11，而生成的主 command 才标记为 View 30 / SubView 12。过滤已改为检查第一次 builder 调用实际产生的 command 集合；不再把入口 SubView 当作输出 pass。

## 2026-07-18 native duplicate PoC 实机结果

修正触发条件后的采集成功执行一次重复 builder 调用，且没有崩溃、错误日志或明显视觉变化：

```text
SubmissionDuplicate Cycle=1
  Applied=true
  OriginalPushCount=6
  BuilderReturn=0x2
  DuplicateReturn=0x2
  BoundaryView=30.11
  BoundarySort=0xB153F905
  MaterialCalls=2
```

第一次和第二次调用分别生成六条地址不同的 command：

| 作用/输出 | 第一次 | 第二次 | Sort / View |
| --- | ---: | ---: | --- |
| Stage A 主命令 | push 0 | push 6 | `0xC407725F` / 30.12 |
| Stage C family 1 | push 2 | push 8 | `0xC807725F` / 30.12 |
| 条件式/辅助主命令 | push 1 | push 7 | `0xCB07725F` / 30.12 |
| Stage C family 2 | push 3 | push 9 | `0xCB7FFFFE` / 30.12 |
| 辅助 view | push 4 | push 10 | `0xB153F905` / 32.11 |
| 辅助 view | push 5 | push 11 | `0xB153F905` / 33.11 |

主 buffer 中两组命令按相同 SortKey 相邻排列：Stage A group index 221/222，Stage C family 1 为 345/346，条件式命令为 400/401，Stage C family 2 为 405/406。View 32 和 View 33 的两组命令也分别以 index 5/6 相邻。Cycle 1 的 12 条 command 全部被 consumer 定位，并全部进入 executor。

executor 观察到每一对 command 使用相同的 RT/depth-stencil、VS/PS wrapper、depth/stencil/blend/rasterizer state、viewport/scissor、index buffer 和 draw range。D3D 捕获中目标 draw 也明确成对：

| D3D family | 第一次 sequence | 第二次 sequence | 关键一致证据 |
| --- | ---: | ---: | --- |
| Stage A | 32 | 33 | 相同 VB/IB、draw range、VS/PS、constant hash、resources |
| Stage C family 1 | 41 | 42 | 相同 VB/IB、draw range、VS/PS、constant hash、resources |
| Stage C family 2 | 50 | 51 | 相同 VB/IB、draw range、VS/PS、constant hash、resources |

两组 command 的地址、内部 allocation 指针和整体 payload hash 不同，证明 builder 重新生成了 packet；而最终绑定状态及 constant 内容相同，符合“相同输入产生相同渲染”的预期。没有明显视觉变化不是失败证据：相同几何、transform、材质和 GPU state 紧邻重画，depth/stencil 与透明混合可以让最终像素保持不变或差异不可见。

整次捕获汇总为 25 个 build、64 条 consumer 记录、66 条 executor 记录和 97 个 D3D draw snapshot。总数中 consumer/executor 的两条差异来自其他正常 build 的观察覆盖；成功 PoC 所属 Cycle 1 的 12 条命令在 producer、consumer 和 executor 三侧完整闭环。

## 当前决策与下一步

重复提交 PoC 已达到 [native-transparent-submission-plan.md](native-transparent-submission-plan.md) 规定的首个成功条件：同一次高层调用可以由游戏自动生成完整 Stage A/C，而不是复制最终 packet。现阶段应停止继续扩展 command/executor tracer，也不应尝试直接修改 frame-arena 中已经生成的 command。

当前 skinned donor 的 transform 输入分析已经完成，结论见上文。它不能作为简单、独立的 transform carrier，但不影响 `0x281DD0` 继续作为 Underpaint 原生多 pass builder。

正式后端下一阶段不再构造非 skinned `Render::Model` 载体。当前最小输入边界是 caller安装到线程 Context的 geometry状态，加上已验证的 material/pass参数和 draw range：

```text
Underpaint-owned Kernel VB/IB wrappers
匹配的 vertex declaration 与 stream stride/offset
Underpaint-owned geometry draw range
由 Underpaint现有路径提供的 world/current/previous常量
安全的同帧 resource lifetime 与释放边界
```

下一次采集只回答一个问题：`Context+0x888/+0x8C0` 中的 Kernel buffer wrapper如何对应现有 D3D侧 IB/VB，以及16-byte stream binding中哪一项是buffer、offset和stride。若对象快照足够，就直接追其构造/释放函数并实现 Underpaint-owned wrapper；若仍不够，只在同一目标 hook中补一个更窄的字段验证。仍然不能用 command packet patching、临时改共享对象后恢复、或跨帧持有当前 hook临时指针绕过所有权问题。

## 2026-07-19 native geometry input 实机结果

`SubmissionGeometry` 与同次目标 `DrawIndexed Count=2280 / StartIndex=12560` 已逐字段对应：

- `Context+0x888 = 0x2454B9B4F90` 是 Kernel IndexBuffer；其对象 `+0x48 = 0x245B9C4CAE0`，正好等于 D3D侧目标 IB。
- stream 0/1均引用 Kernel VertexBuffer `0x24570217660`；该对象 `+0x40 = 0x2454782C320`，正好等于 D3D侧目标 VB。
- 每个16-byte stream binding是 `{KernelVertexBuffer*, packed}`，其中 `packed = (byteOffset << 8) | stride`。`0x2AC6814` 解为 offset `0x2AC68=175208`、stride `0x14=20`；`0x2D9B818` 解为 offset `0x2D9B8=186808`、stride `0x18=24`，均与 D3D捕获完全一致。
- VertexDeclaration保存28-byte、7元素描述，每元素四字节 `{stream, offset, type, usage}`；本次描述为 `00-00-13-00 00-0C-3C-01 00-10-3C-07 01-00-1C-02 01-08-24-0F 01-0C-24-03 01-10-1C-08`。

IDA随后确认了正式资源入口：`0x2255A0/0x21DC10` 创建并填充104-byte native VertexBuffer，`0x2257F0/0x21E240` 创建并填充112-byte native IndexBuffer，`0x225A20 -> 0x235720` 从四字节元素描述取得带缓存和引用计数的 VertexDeclaration。三类资源都通过标准引用计数释放，不需要伪造wrapper或把裸 D3D pointer塞入共享对象。

下一版显式 `Arm custom native triangle` PoC在目标主view的 `0x283320` hook内执行：先保留donor原提交，再创建独立native VB/IB/VertexDeclaration，暂时替换当前线程 Context的 `+0x888/+0x890/+0x8C0`，以 `vertexCount=3/startIndex=0/indexCount=3` 再调用一次同一pass builder，并在返回前恢复全部原 Context字段。它不修改 Model、Material、骨骼、donor buffer或已生成command。首次实测证明资源创建和hook触发均成功，但因曾将第5参数误判为baseVertex而传入 `3/0/0`，indexCount为零，未生成draw；现已依据原调用稳定的 `580/12560/2280` 修正参数语义。成功标准首先是日志出现 `CustomGeometrySubmission ... CustomRange=3/0/3`，且后续 D3D捕获出现使用新VB/IB、Count=3的 Stage A/C命令；肉眼是否明显可见不是首要判据。

## 2026-07-19 independent native geometry 成功结果与迁移

修正 `indexCount` 后，实机日志闭环确认插件自有几何进入了完整原生提交：

- native VB wrapper `0x2456FDD1128` 对应 D3D VB `0x2454B951220`；
- native IB wrapper `0x241D9A7DB20` 对应 D3D IB `0x2454B954EA0`；
- VertexDeclaration `0x2422E3AD770` 使用已确认的双 stream、7元素布局；
- builder 收到 `CustomRange=3/0/3`，在主 View 30.12 生成 push 6/7/8/9，并在辅助 View 32/33 生成 push 10/11；
- D3D侧分别出现 Stage A sequence 34、Stage C family 1 sequence 43、Stage C family 2 sequence 52，三者均为 `Count=3`，且都绑定上述插件自有 VB/IB；
- donor原提交未被替换，builder返回后原线程 Context的 IB、VertexDeclaration和两个 stream binding均恢复。

这已经证明“原生几何 command入口能统一驱动 Stage A/C”的核心判断。它没有证明衣服或人物 Model 是正式载体：当前衣服过滤只负责把验证调用放进一个已知有效的 material/pass context。

已将以下所有权迁入相邻独立仓库 `ffxiv-underpaint`：

- Kernel VertexBuffer / IndexBuffer / VertexDeclaration 的创建、初始化、引用计数释放；
- 插件 positions/indices 到原生双 stream 布局的转换；
- 当前线程 `Graphics::Kernel::Context` 几何状态的安装和无条件恢复；
- 在原 hook、原线程、原生命周期内同步调用 pass builder 的最小边界；
- renderer销毁时回收仍存活的原生几何。

EventHorizon侧已经删除资源工厂、wrapper字段、stream打包和native release代码。第一次迁移后实测结果未退化：Stage A sequence 34、Stage C sequence 43/52，以及辅助 Stage C sequence 54 均继续出现 `Count=3`，且绑定迁移后 Underpaint 创建的同一组插件 VB/IB。

### 无目标提交现场

继续静态追踪确认：

- `ResourceManager.GetResourceSync` 可以由资源路径推导 category、type 和 CRC；`.mtrl` 加载完成后，`MaterialResourceHandle` 会自行加载 SHPK/纹理并在 `+0xC0` 建立 `Render::Material`。因此独立持有材质资源可行。
- 但 `0x281DD0` 不是“只给 Material 即可调用”的入口。它读取 `Model.Materials`、`ModelResourceHandle` 的 geometry/material映射、bone/instancing buffers以及多组 model-owned资源；伪造 Model 仍是错误方向。
- 真正已验证的几何 pass展开入口是 `0x283320`。它接收 `0x281DD0` 已准备好的 `OnRenderMaterialParams2` 与当前线程 Context状态；此前的自有 VB/IB PoC实际也是在这一层完成重复提交。

据此，Underpaint 增加了一个仍为 internal 的一次性 standalone arm：

```text
EventHorizon 只创建测试三角形并 arm
  -> Underpaint 等待下一次 View=30 / SubView=11 的原生 pass builder 调用
  -> 保留原调用
  -> 临时安装 Underpaint-owned VB/IB/VertexDeclaration
  -> 用当前完整 material/per-instance context 再调用一次 0x283320
  -> 无条件恢复 Context
```

该路径不再读取目标角色，也没有 Slot、MaterialIndex 或 SHPK过滤。第一次实测确认 arm 会在 21ms 内命中并调用 `3/0/3`，但没有产生 `Count=3`：命中现场随后可见的原 draw 使用 stream strides `20/28`，而插件自有 vertex declaration/数据布局固定为先前已经成功验证的 `20/24`。因此“任意主视图现场都兼容”是错误假设；builder 被调用不代表不匹配的 vertex ABI 会生成命令。

修正后，arm 只在当前 Context 的两个 stream strides 为 `20/24` 时消费。它仍然不检查人物、装备或材质身份。日志 `StandaloneNativeSubmission` 额外记录源 VB/IB、vertex declaration、strides和原 range；若捕获窗口内不存在兼容现场，UI和日志会明确报告 `No compatible 20/24 native submission site`，而不会把一次无命令的调用误报为成功。下一次实机验收只需打开 `/eh 3d` 后点击 `Arm custom native triangle`，不需要选中角色或穿指定衣服。

第二次实测命中了 `SourceStrides=20/24`，源与插件甚至复用了同一个 native VertexDeclaration，但原 Stage A/C 捕获只看到另一组 `20/28、Count=3336` 的透明 draw。这证明透明 pass 分类不能用来判断任意 standalone现场是否消费了插件几何。现已增加一个与 pass分类无关的窄捕获：只在四种 D3D draw入口中匹配本次插件 VB和IB，最多记录32条命中。`NativeGeometryDrawCapture Matches=0` 将直接证明builder调用没有形成消费侧draw；非零时每条 `NativeGeometryDraw` 会给出pass、Count、range、shader、layout及全部VB/IB绑定。`StandaloneNativeSubmission` 中原来的 `Success` 同时改名为 `BuilderInvoked`，避免把“未抛异常”误写成“已生成命令”。

第三次实测得到 `Matches=6`：一条明确的 `Opaque` 和五条辅助 draw全部为 `Count=3`，统一绑定插件自有VB/IB。这已经闭环“无目标、无指定衣服、无材质过滤的兼容原生现场可以自动展开不透明及辅助命令”。当前主线转到实例输入。静态追踪确认 `0x281AE0` 会先调用 `Model.RenderModelCallback`，由回调写入 `OnRenderModelParams+0x10` 并准备 Context常量绑定，然后才进入builder；因此不能只浅拷贝 `OnRenderMaterialParams2`。下一版只为该次standalone提交记录 `OnRenderModelParams+0x10`、Model callback、`WorldViewMatrix/InstancingMatrix/PrevInstancingMatrix` native ConstantBuffer，以及六条最终draw的VS constant buffer和内容hash，用于确定可独立替换的最小transform边界。

这一步解决的是“谁负责找到可用原生提交现场”：现在由 Underpaint 自己负责。它尚未解决“材质和实例状态完全由插件独立拥有”；当前仍借用命中现场已经准备好的 material/per-instance context。正式公开后端前的剩余主线是把独立加载的 `MaterialResourceHandle/Render::Material` 接到可复制的最小 params 状态，或找到为非 skinned primitive 准备该状态的更高层原生入口。

## 2026-07-19 standalone transform 副本 PoC

上一轮实测把 standalone 现场的实例输入收窄为两块数据：`OnRenderModelParams+0x10` 指向176-byte object constant，Context 的 `g_InstancingMatrix`槽指向48-byte、三行 `float4` 矩阵；该现场没有 `g_WorldViewMatrix` 和 `g_PrevInstancingMatrix`。六条最终draw没有直接引用这两个donor buffer，而是由builder重新打包到各pass的上传constant中。

IDA进一步确认了资源构造方式：`CharacterBase.Initialize` 以 `CreateConstantBuffer(176, 2, 0)` 创建两块角色object constant；`ModelRenderer` 初始化以 `CreateConstantBuffer(48, 1, 7)` 创建并清零 `g_InstancingMatrix`。因此本轮不修改共享Model、角色constant或Context中原buffer内容，而是在同一hook、线程和生命周期内：

1. 完整复制32-byte `OnRenderModelParams` 和72-byte `OnRenderMaterialParams2`，只让后者副本指向前者副本；
2. 创建Underpaint自有的176-byte和48-byte native ConstantBuffer并复制donor内容；
3. 让model params副本指向独立176-byte buffer，在48-byte矩阵副本第一行W分量增加 `+2.0`；
4. 仅在重复调用builder期间将Context的instancing槽替换为副本，并在 `finally` 中与几何绑定一起恢复；
5. 若现场存在previous instancing，则从current复制同一偏移作为previous，确保首次提交 `Current == Previous`；当前已知现场为空时保持为空。

日志现在同时输出donor和offset constant的地址、source、hash及三行float值。下一次采集要验证：builder仍产生六条 `Count=3`；donor object/instancing hash不变；offset object内容相同但buffer身份独立；offset instancing仅第一行W相差2；最终六条draw的VS constant hash相对donor发生一致变化。肉眼位置变化是辅助证据，不作为唯一判据。

### 结果：48-byte instancing 假设被否定

实机采集显示donor `g_InstancingMatrix` 的三行全部为零；副本第一行W确实变为2，但没有形成可证明的物体位移。该buffer不是这个skinned现场可独立控制的world输入。上一节保留为失败实验记录，不再作为后端设计依据。

当前CN客户端的静态反向追踪给出了真实分界：

- `0x281DD0` 的non-skinned分支把 `Model+0x38` transform object拥有的constant buffer绑定到 `ModelRenderer.ConstantSamplerIds[1]`，即运行时World槽；同时把model-type scene key设为non-skinned值。
- 该transform object的更新函数 `0x266DD0` 填充128 bytes：64-byte current world/view矩阵和64-byte previous world/view矩阵；首次有效提交令previous等于current。
- `0x281DD0` 的skinned分支改设同一scene key的skinned值，并绑定 `BoneList` current/previous joint palettes。Human callback绑定的176-byte constant属于角色数据，不是world矩阵。
- `0x283320` 本身不读取Model、BoneList或transform object；它消费已经准备好的shader selection与TLS Context，把当前状态展开为各view/pass command。因此无需继续向角色job上移，B的独立输入可以直接安装在这个同步pass-builder边界。

## 2026-07-19 non-skinned world 输入 PoC

Underpaint现在在一次性arm命中的兼容 `20/24` 现场执行以下最小替换：

```text
保留 donor 原提交
  -> 复制 OnRenderModelParams (0x20)
  -> 复制 OnRenderMaterialParams2 (0x48)
  -> 复制 shader selection (0x28) 与 key values
  -> model-type key: skinned value -> non-skinned value
  -> Context World slot: donor -> Underpaint-owned 128-byte World CB
       current  = view-space translation Z +5
       previous = current
  -> 安装 Underpaint-owned VB/IB/VertexDeclaration
  -> 同步调用 0x283320(3, 0, 3)
  -> 无条件恢复 geometry 与 World槽
```

这一步不修改共享Model、Material、角色constant、Skeleton、BoneList、palette或history。复制的params、shader selection和key array只在原hook栈内存活；World CB和native geometry由Underpaint持有，不异步解引用frame/job指针。当前仍借用donor material和其他pass状态，所以它是“独立geometry + 独立non-skinned transform边界”的验证，不是最终独立material后端。

新日志记录：原/副本shader-selection地址、model-type key CRC、donor skinned value、副本non-skinned value、World槽ID、独立World CB资源/源内存/hash，以及current矩阵前三行。下一次实机采集可以验证两件事：

1. 当前material是否存在可由non-skinned key选中的shader permutation，并仍自动生成六条 `Count=3` command；
2. 这些command是否统一消费独立128-byte world输入，而不再依赖角色joint palette。

若builder不再生成command，唯一直接缺口就是当前借用material缺少non-skinned permutation；此时应进入独立不透明material加载/params构造，不应回到角色Model工厂、骨骼链或command packet patching。

### 实机结果

non-skinned world PoC成功。donor的World槽为空，而Underpaint自有128-byte World CB为单位矩阵加view-space Z `+5`；model-type key从skinned `0x9C14C8E9` 切为non-skinned `0x4123B1A3`。builder生成六条有效 `DrawIndexed Count=3`，全部绑定插件VB/IB，并统一出现同一个新的128-byte VS constant。目标材质具有可用的non-skinned shader permutation，B边界成立。

## 2026-07-19 owned material selection PoC

IDA确认 `0x281DD0` 中有两段可以直接复用、无需Model carrier的原生职责：

- `0x17E2E10 / 0x17E2FC0`：从 `ShaderPackage` 构造/销毁一次调用级shader selection。scene-key values来自当前frame arena，SHPK本身采用引用计数。
- `0x2F9940`：接收shader selection和 `Render::Material`，把material shader-key values接入selection，并把 `MaterialParameterCBuffer`、texture resources及sampler flags安装到当前TLS Context。

Underpaint现在只增加一个窄的 `OnRenderMaterial` 关联hook，用同一栈上params地址把当前 `Material*` 传到紧随其后的 `0x283320`；不记录跨帧临时指针。首次arm为该Material的 `MaterialResourceHandle` 增加Underpaint自己的引用并记录资源路径。实际重复提交执行：

```text
retained MaterialResourceHandle -> Render::Material -> ShaderPackage
  -> 原生构造独立 shader selection
  -> 按CRC复制当前view共有的scene-key values
  -> model-type key = non-skinned
  -> 原生material helper绑定material CB/textures/sampler flags
  -> Underpaint geometry + World CB -> 0x283320
  -> 恢复material constant、每个texture slot、World和geometry
  -> 销毁selection
```

这版仍在第一次arm时从现场捕获material resource，目的是先验证resource lifetime、独立selection和精确Context恢复。第二次arm会复用Underpaint已经持有的resource，不再采用当次source Material作为目标。日志输出source/owned Material、resource、SHPK、路径和 `MaterialCaptured`；连续两次arm应分别为 `true/false`，owned身份稳定且两次都生成六条有效 `Count=3`。通过后用日志中的稳定路径调用 `ResourceManager.GetResourceSync`，即可把首次捕获替换为显式材质加载，并随后放宽 `20/24` source过滤。

首次实机调用在提交前安全停止，错误为 `owned shader package has no material constant slot`。原因是把SHPK `Constants[].Slot`误当成TLS Context的运行时constant ID；原生material helper实际使用renderer/camera初始化后注册的另一套ID。修正版不再从SHPK猜编号，而是在首次source material已经完成原生绑定的Context constant数组中，以 `MaterialParameterCBuffer*` 精确反查真实ID并持久保存；Context的constant区域边界是 `0x940..0x1140`，共64槽。日志新增 `ConstantId` 用于实机确认。失败发生在调用material helper和builder之前，没有留下Context修改。

修正版连续两次实测成功。日志标签实际曾缩写为 `Captured`（现已改为明确的 `MaterialCaptured`）：第一次为true、第二次为false；两次owned Material/resource/SHPK/path完全相同，运行时material constant ID稳定为25，两次各生成六条有效 `DrawIndexed Count=3`。因此独立resource引用、selection重建、material helper绑定和Context恢复已经通过。

持有资源给出的真实游戏路径为 `chara/equipment/e0378/material/v0002/mt_c0101e0378_top_a.mtrl`。从运行中handle确认资源键为category `Chara`、file type `0x6D74726C` (`mtrl`) 和path CRC `0x56D3AB97`。当前版本改用 `ResourceManager.GetResourceSync` 以这组键显式加载，不再从source Material取得目标resource；日志新增 `MaterialLoaded`。首次应为 `MaterialCaptured=false/MaterialLoaded=true`，后续为 `false/false`。当前source Material仅剩两个临时用途：提供本次view的scene-key values，以及在Context中反查全局material constant ID。

显式加载实测通过：`MaterialCaptured=false / MaterialLoaded=true / ConstantId=25`，路径为无装饰的真实游戏路径，仍生成六条有效 `DrawIndexed Count=3`。目标material resource的来源已经与现场完全解耦。

下一版删除窄 `OnRenderMaterial` 关联hook和source Material参数。material helper调用前保存全部64个Context constant槽，调用后直接按目标 `MaterialParameterCBuffer*` 识别其真实运行时ID，再在提交结束后整体恢复；因此不再需要source material帮助定位ID。source vertex stride的 `20/24` 限制也已删除：当前只等待View 30/SubView 11且具有可复制176-byte model wrapper的调度现场，独立material selector、geometry ABI和World输入均由Underpaint提供。source shader selection只作为本view共有scene-key values的CRC映射来源；它可以是不同SHPK，也不再要求包含或选择skinned model-type key。

首次无stride过滤实测仍成功生成六条有效 `Count=3`，但调度顺序碰巧仍首先命中 `SourceStrides=20/24`，所以这次不能单独证明source ABI无关。下一版暂时强制只消费 `SourceStrides=20/28` 的View 30/SubView 11现场，直接复测此前在borrowed-material版本中无法生成command的条件。若独立material版本在该现场仍生成六条 `Count=3`，source geometry/material ABI依赖即可闭环；通过后删除这一临时测试过滤。

强制 `20/28` 的实机结果确认了ABI脱钩：日志中的source range为 `1383/232/3336`、source strides为 `20/28`，而提交仍使用Underpaint自有 `20/24` VB和三索引IB，builder正常返回并生成有效 `Count=3`。但这次共有九条有效indexed draw，而非此前六条；其中新增一条Semitransparent和两条Semitransparent Stage C。目标material、selector和geometry均未变化，差异来自整块复制的source `OnRenderMaterialParams2`。

静态复核 `0x283320` 后，剩余依赖已定位到参数本身：`Params2+0x38`携带geometry/view掩码，`+0x40`则由 `OnRenderMaterial` 根据Material、resource additional data、SHPK类型和Model callback生成，并被pass builder直接按位展开命令。当前版本不再复制 `+0x10..+0x37` 的可选callback输出，也不再沿用source `+0x40`。它保留调用级model/resource和 `+0x38`，按原生初始化规则重置其余字段，再对显式加载的目标Material同步调用游戏自己的 `OnRenderMaterial` 生成owned flags；随后才应用owned selector/material并进入 `0x283320`。调用期间改变的TLS rasterizer state、全部64个constant槽和目标texture槽均在 `finally` 中恢复。临时 `20/28` 过滤已经删除。

日志新增 `Flags=source->owned` 和 `MaterialIndex`。下一次采集需要确认：任意source stride现场都可触发；owned flags与source透明flags分离；有效 `Count=3` 回到目标不透明材质应有的一条Opaque加辅助draw集合，不再出现由source params引入的Semitransparent家族。此后唯一尚未独立拥有的边界是source `OnRenderModelParams`/Model wrapper及当前view共有scene-key values；材质pass决策本身已改由目标Material原生生成。
