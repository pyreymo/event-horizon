# TransparentDrawCorrelation / material submission probe 交接说明

日期：2026-07-18

## 本轮状态

旧的 Underpaint 手写 Stage A/C 近似后端已经冻结，只保留为实验对照。临时 `Skip Stage A` A/B 开关已经删除。`agent/stability-probes` 中已经验证过的 pass 退出注入、viewport/scissor、投影与 depth-bias 稳定性修复继续保留，因为它们用于避免旧后端污染后续实验判断，不代表继续扩展近似材质系统。

新的 `TransparentDrawCorrelation` 路径只在 Debug 构建中存在。普通 `Arm transparent capture` 仍严格只读；另有显式的一次性原生重复提交实验，默认不启用：

- EventHorizon 在 framework/game-thread 时点从当前目标的固定 Slot 1 生成不可变 donor 快照；
- hook `ModelRenderer.OnRenderMaterial` 的唯一直接调用者，并以 Slot 1 的 `Model*`、MaterialIndex 2 和 `charactertransparency.shpk` 三重过滤；
- Underpaint 对原生半透明 Stage A/C 的 draw 做有界快照；
- 捕获 VB/IB、draw range、VS/PS/input layout、VS/PS constant-buffer 指针及有限 hash、PS SRV 的底层 resource 身份、线程、单调时间和 native module+RVA stack；native stack 从反向调用边界的第 0 帧开始，无法展开时写入明确原因而不再留空；
- 捕获最多 4 个 Stage A 帧、128 个 draw、128 个目标 material build 或 15 秒；draw 预算在 A/C 两侧各保留一半，避免 Stage A 抢完预算；任一边界到达后自动停止；每个 shader stage 每个 draw 最多只 readback/hash 一个有限大小的 constant buffer，避免无界 GPU 同步；
- matcher 只使用 Slot 1 donor texture，并同时结合 geometry、draw range、constant hash 和共享资源评分，不以 VB/IB 或 index count 单独判定；
- 全程不写队列、不复制 packet、不调用候选 builder，也不跨帧保存可供异步解引用的原生指针。

日志 source 固定为 `TransparentDrawCorrelation`，写入现有 `debug-logs/event-horizon-*.log`。

## 静态分析结论

先用 FFCS 确认公开结构和 `Graphics::Kernel::Context`，再因调用者和控制流信息不足，使用当前 2026-07-18 客户端的 IDA 数据库补齐直接调用关系。以下 RVA 只对该客户端版本有效：

- `ModelRenderer.OnRenderMaterial`（`ffxiv_dx11.exe+0x281540`）只有一个直接调用点：`ffxiv_dx11.exe+0x281EB7`，位于 geometry/material builder `ffxiv_dx11.exe+0x281DD0` 内。
- builder 从 `ModelResourceHandle+0xE8` 的 0x24-byte geometry 表读取当前 entry，从 `entry+8` 取得 material index，再从 `Model.Materials[index]` 取得 `Material*`。
- 调用者不读取 `OnRenderMaterial` 返回寄存器。当前 Human callback 主要修改线程私有 graphics context 的材质/GPU 状态和 `Params2+0x40` 控制 flags；若 callback 填充可选输出描述，wrapper 会把 stack-local 的 `+0x08/+0x10/+0x18/+0x20` 复制到 `Params2+0x10/+0x18/+0x20/+0x28`。当前 Human 路径静态上未见它填充这些可选字段。
- builder 后续同时使用 model、material、geometry entry、camera/view 状态、skeleton/constant buffers，并进入不同的 command builder 分支。
- 这些分支最终调用当前线程 `Graphics::Kernel::Context.AllocateCommand` / `PushBackCommand`。因此当前最可信的输出容器不是长期 render-item 数组，而是线程私有 `Context` 的 command arena 和 payload arena。
- builder 的上一层是遍历 model geometry 的 render job；再上一层负责建立并提交异步 job。现阶段没有调用或复制任何 builder/packet。

## 新运行时探针

每次命中只记录目标角色 Slot 1 / MaterialIndex 2 / `charactertransparency.shpk`：

- build cycle、时间戳、原生 caller RVA、线程；
- `ModelRenderer*`、`OnRenderModelParams*`、`Model*`、`ModelResourceHandle*`、geometry index/entry/hash、`Material*` 和 `MaterialResourceHandle*`；
- 当前线程 `Context*`、view/sub-view/sort key；
- command arena 与 payload arena 调用前后的 base/used size，以及本次新增范围和有限 hash；
- builder 范围内每次 `AllocateCommand` 返回的地址、大小和 builder 返回时的有限 hash；
- `OnRenderMaterial` 返回值，以及完整 0x48-byte `Params2` 调用前后 hash、变化 offset 和 qword 快照。

下一次采集首先验证该 material build 是否稳定增加 command/payload arena，以及每次增加几项、地址范围和 payload 是否随 cycle 变化。只有得到这个容器证据后，才把消费侧探针从 `DrawIndexed` 上移到遍历这些 command 的 executor。

## Debug DLL 的人工步骤

1. 准备一个可选中的 PC，推荐自己的角色。
2. 只保留一件容易识别的 `CharacterTransparency` 透明装备，并记下物品名和装备槽。
3. 暂时关闭该 donor 的 Penumbra 模型/材质替换。
4. 选择安静普通场景，固定镜头和姿态，避开水体、雨淋、湿身、Gpose 和持续动作。
5. `/eh 3d` 打开 Underpaint Demo，展开 `Transparent draw correlation`。
6. 选中 donor，确认摘要显示 `slot 1` 且 material/texture 数量合理；如需新日志，先点 `Clear EventHorizon logs`，再点 `Arm transparent capture`，等待状态变为 Complete。
7. 保持装备可见，固定条件重复采集一次，用于比较 container、allocation count 和 payload hash 是否稳定。
8. 提供日志，以及捕获期间是否出现长卡、崩溃或持续日志。

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

下一次采集的唯一目标变为：把 producer 的 6/3 个 pushed command 逐项对应到现有 Stage A/C draw range。确认映射后，才需要决定是否继续进入更低的 GPU 状态 executor。

## 2026-07-18 command consumer 实机结果

第二轮日志取得 10 次 build、46 条 consumer 记录。其中 45 条是有效的一一关联，另 1 条明确暴露 frame-arena 地址复用：旧 command 地址后来对应 `Count=36` 的其他命令，且 GroupSort 已不再等于原 ProducerSort。后续探针因此把 command type 与 SortKey 一并作为生命周期校验，并在发现复用或执行完成后移除旧地址。

有效结果稳定重复：

- 所有 pushed command 都是 type 6，即原生 `DrawIndexed` command；
- 所有命令的 draw range 都是 `Count=2280 / StartIndex=12560 / BaseVertex=0`，与目标材质现有 Stage A/C draw 完全一致；
- 原先称为 View 30 的 3520-byte build 实际 push 六个命令：四个进入同一 745-entry 主 buffer，排序后 group index 固定为 201、312、366、370；另外两个分别进入 View 32 和 View 33 的 14-entry buffer，index 都为 5；
- 原先称为 View 1 的 1056-byte build 实际 push 三个命令，进入 View 65 的 48-entry buffer，sub-view 7/8/9 的 group index 固定为 6、23、38；
- 主 buffer 的四个命令按 SortKey 排列为 push 0、2、1、3，证明不能使用 builder 内的 push 顺序推断最终 pass 顺序；
- D3D 侧稳定看到同一 range 的一个 Stage A 和两个 Stage C draw，因此九个原生命令中至少六个属于当前 A/C capture 范围之外的 view 或辅助提交。

现在已确认 builder 直接生成完整的多 view、多 pass `DrawIndexed` command 集合。剩余唯一证据是：主 buffer 四个命令中，哪三个分别成为当前观察到的 Stage A、Stage C family 1、Stage C family 2，以及第四个落在哪个辅助目标。

下一版仍不扩建通用 tracer，只 hook type 6 执行前已经存在的状态准备 helper，并只对已登记 command 生效。它记录 command 执行时间以及当时的 RT、depth-stencil、VS/PS、blend/depth/rasterizer、topology、scissor 和 index-buffer 状态，可直接与紧随其后的 `DrawIndexed` 时间及 Stage A/C 资源状态对应。

## 2026-07-18 command executor 实机结果

第三轮日志取得 20 次 build、55 条有效 consumer 和 55 条 executor 记录。场景中出现了额外的 View 66/67/68，说明辅助 view 集合会随场景变化；主 View 30.12 的形状仍逐帧稳定。

主 view 四条 command 与实际 draw 的时间和状态已经闭环：

- push 0 / Sort `0xC4075C7F`：四个 G-buffer RT 加 depth，稳定对应目标 `StageA` draw；
- push 2 / Sort `0xC8075C7F`：单 RT，稳定对应第一族目标 `StageC` draw；
- push 3 / Sort `0xCB7FFFFE`：同一单 RT，稳定对应第二族目标 `StageC` draw；
- push 1 / Sort `0xCB075C7F`：进入 executor 并准备完整 GPU 状态，但本次没有落到目标 geometry 的 D3D draw，保留为条件式或辅助 command，不为它强行命名。

因此已经确认：一次 `ffxiv_dx11.exe+0x281DD0` geometry/material builder 调用，在同一线程的 `Graphics::Kernel::Context` 中自动生成 Stage A、两族 Stage C 和辅助提交；插件不需要分别伪造 A/C packet。

下一步是受控的首次写入 PoC。Debug UI 的 `Arm one native duplicate` 只在目标角色 Slot 1、MaterialIndex 2、`charactertransparency.shpk` 且进入主 View 30.12 时消费一次 arm 状态。它先执行正常 builder，再在原 hook、原线程、原参数和原隐式 context 中额外调用一次原 builder；不修改 Material、transform 或已生成 command。日志额外记录：

- 是否实际执行重复提交；
- 第一次调用结束时的 command arena 边界和 push 数量；
- 两次 builder 返回值与 `OnRenderMaterial` 调用次数；
- 重复调用新增 command 的 push index、地址、sort/view、消费和执行状态。

下一次人工采集只点 `Arm one native duplicate`。要验证的唯一关键证据是：第二次 builder 调用是否在原有六条主 build command 后再生成同形的六条 command，并分别再次进入 Stage A、两族 Stage C 和相同辅助 executor 路径。若形状不一致或出现异常，不继续修改隐式 context；若完整成组复制，才进入自定义 transform 输入的静态分析。
