# Event Horizon 玩家可见性 CP-SAT 优化进展

状态：已废止，仅保留为 CP-SAT 原型历史进展。
替代计划：`.codex/Event Horizon 玩家可见性稳定 Top-B 重构.md`

源计划：`.codex/Event Horizon 玩家可见性 CP-SAT 优化跟踪.md`

## 2026-07-09 22:13 +08:00

本轮步长：推进 Phase 0，不改现有玩家可见性主链路。

完成内容：

- 引入 `Google.OrTools 9.15.6755`，锁定 `Google.Protobuf 3.33.1` 和 OR-Tools runtime 依赖。
- 项目固定 `RuntimeIdentifier=win-x64`，避免默认 Release 包带入 Linux/macOS runtime。
- 新增 `CpSatPhase0Probe`，通过隐藏命令 `/eh cpsat` 或 `/eh cp-sat` 手动触发后台单线程 CP-SAT 最小模型求解。
- 探针使用 `TaskCreationOptions.LongRunning` 放到后台线程，solver 参数为 `num_search_workers:1 max_time_in_seconds:1.0`。
- 探针卸载时取消未完成任务；若后台求解稍后返回，Dispose 后不再写日志或回调 Dalamud 侧对象。
- 现有 `ObjectCuller`、`PlayerVisibilityPlan`、`PlayerVisibilityReconciler`、fade、preview 和 VFX 行为未接入求解器，也未改变。

验证结果：

- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `dotnet csharpier format .`：已运行。
- Release 包：`EventHorizon/bin/Release/win-x64/EventHorizon/latest.zip`，大小约 `22,543,128` bytes。
- 包内 OR-Tools 相关文件只保留 win-x64 根目录 DLL：`Google.OrTools.dll`、`Google.Protobuf.dll`、`google-ortools-native.dll`、`ortools.dll`、`libprotobuf.dll`、`abseil_dll.dll`、`highs.dll`、`libscip.dll`、`libutf8_validity.dll`、`re2.dll`、`zlib1.dll`、`bz2.dll`。
- 本机直接从 Release/win-x64 输出目录加载 OR-Tools 并求解最小模型：`status=Optimal objective=3 elapsedMs=25.9288`。

观察：

- 不指定 RID 的 Release 包曾达到约 `116,047,898` bytes，并包含多平台 `runtimes/*` native 文件；已通过项目级 `RuntimeIdentifier=win-x64` 收敛。
- `packages.lock.json` 被 restore 收敛到当前真实项目依赖图；旧的 `pictomancy/KamiToolKit/SharpDX` 记录不再出现，当前 solution 和 csproj 没有对应引用，工作区也没有 `ffxiv_pictomancy` 子模块目录。
- Phase 0 仍缺少一次真实 Dalamud 运行时验证：进游戏后执行 `/eh cpsat`，确认插件日志出现 `[CP-SAT Phase 0] Probe finished status=Optimal ...`，并观察首次求解耗时。

下一步建议：

- 完成游戏内 `/eh cpsat` 验证后，进入 Phase 1：建立 `BypassVisible / Competitive / ForceHidden / Unmanaged` 分类和独立目标集合状态，先继续使用旧排序生成目标集合。
- Phase 1 起步时优先做数据结构和执行状态边界，不同时删除 preview/fade/VFX；只有发现它们阻碍目标状态分离或线程所有权收敛时再按计划裁剪。

## 2026-07-09 22:20 +08:00

针对实机反馈处理：

- 诊断：Dalamud 能找到并尝试加载 `google-ortools-native.dll`，但它的二级 native 依赖没有随同进入 Windows loader 的搜索路径，实际失败点是 `google-ortools-native.dll` 的依赖解析。
- 本地复现方式：不修改 `PATH`，从 `EventHorizon/bin/Debug/win-x64` 显式 `NativeLibrary.Load` OR-Tools native 依赖链后再求解，结果 `status=Optimal objective=3`。
- 修复：`CpSatPhase0Probe` 在后台求解前，从 `PluginInterface.AssemblyLocation.DirectoryName` 按顺序预加载 OR-Tools native 依赖：`abseil_dll.dll`、`zlib1.dll`、`bz2.dll`、`re2.dll`、`libutf8_validity.dll`、`libprotobuf.dll`、`highs.dll`、`libscip.dll`、`ortools.dll`、`google-ortools-native.dll`。
- 仍保持隔离：预加载只发生在 `/eh cpsat` 探针后台任务中，尚未接入玩家可见性主链路。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。

待实机复测：

- 重新加载 Debug/win-x64 插件后执行 `/eh cpsat`。
- 预期先看到 `[CP-SAT Phase 0] Loaded OR-Tools native dependencies from ...`，随后看到 `[CP-SAT Phase 0] Probe finished status=Optimal ...`。

## 2026-07-09 22:26 +08:00

实机状态：

- 用户反馈 `/eh cpsat` 实机验证已通过，Phase 0 可继续向 Phase 1 推进。

本轮步长：推进 Phase 1 的分类显式化和可观察性，不引入 worker，不接 CP-SAT 到主链路。

完成内容：

- `PlayerVisibilityPlan` 显式产出 `BypassVisible / Competitive / ForceHidden / Unmanaged` 分类。
- `BypassVisible`：当前旧规则结果中 `Keep + Exempt`，以及临时 preview 可见覆盖。
- `Competitive`：当前旧规则结果中 `Keep + Counted`，仍按旧排序和预算决定本轮目标可见状态。
- `ForceHidden`：当前没有命中 keep rule 的玩家，由本系统明确目标隐藏。
- `Unmanaged`：本系统不应处理的 PC 槽位，例如本地保留槽或非玩家预算竞争槽位。
- `PlayerVisibilityReconciler` 跳过 `Unmanaged`，不再为它们生成 show/hide/maintain action。
- 删除不再使用的旧入口 `PlayerKeepPlan.ShouldHide(...)`，避免后续同时维护两套目标判断语义。
- `CullingPerformanceTrace` 增加分类计数；慢帧日志现在会输出 `classes[bypass=... competitive=... forceHidden=... unmanaged=...]`，方便后续接 worker/CP-SAT 前确认输入规模。

保留行为：

- 仍由旧 `PlayerKeepPlan` 排序和预算结果决定 `Competitive` 中哪些玩家目标可见。
- 现有 RenderFlags 应用、show transition、fade、preview、hidden-player VFX、non-player visibility 路径未重构。
- `ObjectCuller` 仍在 Framework refresh 中构造 plan 并立即 reconcile/apply；worker 尚未引入。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。

下一步建议：

- Phase 1 继续：把“目标集合状态”从 `PlayerVisibilityPlan`/`PlayerVisibilityReconciler` 之间进一步独立出来，让后续 worker 只发布 immutable target result，Framework 线程负责采纳和执行。
- 在实机打开慢帧日志或临时降低阈值时，先观察分类计数是否符合预期：默认目标/焦点/队友/好友应落入 `BypassVisible`，占预算规则命中者落入 `Competitive`，无 keep rule 的普通玩家落入 `ForceHidden`。

## 2026-07-09 22:33 +08:00

本轮步长：继续 Phase 1，把目标集合从 plan/reconcile 之间拆出来。

完成内容：

- `PlayerVisibilityPlan` 现在只产出 `PlayerVisibilityPlanEntry`：对象身份、槽位、分类、规则决策和旧预算裁剪标记。
- 新增 `PlayerVisibilityTargetSet`，由当前 plan 生成本轮目标集合；第一版仍复用旧排序预算结果，所以 `Competitive` 的目标可见状态仍为 `!CutByBudget`。
- `Unmanaged` 不进入 `PlayerVisibilityTargetSet`，因此不会进入 reconcile/executor。
- `PlayerVisibilityReconciler` 改为接收 `PlayerVisibilityTargetSet`，不再直接消费 `PlayerVisibilityPlan`。
- `PlayerVisibilityAction` 改为携带 `PlayerVisibilityTarget`，执行层只面对目标集合，不再读 plan entry。
- `ObjectCuller` 只缓存 `latestPlayerVisibilityTargetSet` 和 `latestPlayerVisibilityReconciliation`，删除了已经不参与运行时的 `latestPlayerVisibilityPlan`。
- preview 刷新改为读取 `PlayerVisibilityTargetSet`，保持现有展示语义。
- 旧排序目标生成逻辑挪到 `PlayerVisibilityLegacyTargetBuilder.Build(...)`，后续 CP-SAT 只需要替换这一层的 target set 生成方式。
- `PlayerVisibilityTargetSet` 生成时复制 scratch list，成为稳定快照，不再持有后续会被复用清空的临时列表。

保留行为：

- 暂未引入 worker，也没有接入 CP-SAT。
- 目标集合仍在 Framework refresh 内同步生成。
- show transition、fade、preview、hidden-player VFX、non-player visibility 路径仍保持现状。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。

下一步建议：

- Phase 1 继续小步：整理 `ObjectCuller.Update` 的阶段命名，使 `分类快照 -> 目标集合 -> reconcile -> apply` 的顺序在代码中更直观。
- 随后进入 Phase 2：建立不可变快照、generation、位置历史、速度预测、单 worker 和 latest-wins 提交机制。

## 2026-07-09 22:44 +08:00

实机状态：

- 用户补充探针性能测试，首次 native/solver 热身约 `44.759ms`，后续热路径稳定在 `0.4-0.6ms` 左右。
- 结论：Phase 0 性能风险目前没有明显问题，可以继续推进后台 worker 方向；后续仍需关注真实玩家集合模型下的结果年龄和求解成功率。

本轮步长：进入 Phase 2 的最小快照骨架，不启动 worker，不接 CP-SAT 到主链路。

完成内容：

- `PlayerVisibilityPlan` 改用 `Generation` 命名，并记录 `CreatedAtTickCount64`。
- `PlayerVisibilityPlan` 返回时复制 entries，避免返回对象继续持有会被下一轮复用的 scratch list。
- `PlayerVisibilityTargetSet` 继承同一代 `Generation` 和 `CreatedAtTickCount64`，并提供 `GetAgeMilliseconds(...)`。
- `PlayerVisibilityReconciliation` 继续携带目标集合的 generation 和创建时间，为后续异步结果采纳、过期丢弃、身份验证留出元数据通道。
- 慢帧日志的 culling trace 增加 `resultAge=...ms`，当前同步路径通常接近 0；接入 worker 后可直接观察结果采用年龄。
- `ObjectCuller` 内部计数器从 `nextPlayerVisibilityPlanRevision` 改为 `nextPlayerVisibilityGeneration`，语义与后续 latest-wins worker 更一致。

保留行为：

- 仍由旧排序预算生成 `PlayerVisibilityTargetSet`。
- 仍在 Framework refresh 内同步生成目标集合并立即 reconcile/apply。
- 未引入 worker、位置历史、速度预测或 CP-SAT 主链路求解。
- fade、show transition、preview、hidden-player VFX、non-player visibility 路径保持现状。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- 备注：首次并行跑 Debug/Release 时 Release 的 DalamudPackager 撞到 `EventHorizon.json` 文件锁；串行重跑 Release 后通过。

下一步建议：

- Phase 2 继续小步：建立纯托管的 solver 输入快照，开始包含 competitive 玩家、当前目标状态、预算和后续预测所需的基础字段。
- 再下一步引入位置历史/速度估计，并保持 worker 尚不采纳结果，先只做不可见的后台求解统计。

## 2026-07-09 22:48 +08:00

本轮步长：继续 Phase 2，建立第一版纯托管 solver 输入快照；仍不启动 worker，不改变显示行为。

完成内容：

- 新增 `PlayerVisibilitySolverSnapshot`，从当前分类快照生成 worker 将来可读取的纯托管输入。
- solver 输入目前只包含 `Competitive` 玩家；`BypassVisible / ForceHidden / Unmanaged` 继续留在 Framework 线程目标集合语义里处理。
- 每个 `PlayerVisibilitySolverPlayer` 携带对象身份、对象槽位、规则决策、上一轮目标可见状态、旧排序目标可见状态和旧预算裁剪标记。
- 上一轮目标可见状态按身份从上一个 `PlayerVisibilityTargetSet` 继承；若没有上一轮目标，则回退到旧排序目标，保证首轮仍有确定输入。
- solver 预算显式按配置计算：启用 `LimitVisiblePlayerCount` 时使用 `VisiblePlayerCountLimit`，未启用时预算等于当前 competitive 数量，避免把未启用限制误读成默认 30。
- `ObjectCuller` 缓存最新 solver snapshot，并在 Clear 时一并清空。
- 慢帧日志的 solver 段扩展为 `solver[input=... budget=... resultAge=...ms]`，可同时观察输入规模、预算和目标结果年龄。

保留行为：

- `PlayerVisibilityLegacyTargetBuilder` 仍然是实际目标集合来源。
- solver snapshot 当前只用于建模和 trace，不参与 reconcile/apply。
- 未引入 worker、位置历史、速度预测或 CP-SAT 主链路求解。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。

下一步建议：

- Phase 2 继续：给 solver 输入补充位置样本和简单速度估计，形成预测层所需的纯托管数据。
- 之后再引入单 worker latest-wins 骨架，先只做后台统计，不采纳结果。

## 2026-07-09 22:56 +08:00

本轮步长：继续 Phase 2，给 solver 输入补充位置样本和速度估计；仍不启动 worker，不改变显示行为。

完成内容：

- `PlayerVisibilityPlanEntry` 增加 `Position` 和 `HasPosition`，位置读取仍发生在 Framework 线程的 plan 构建阶段。
- `PlayerVisibilitySolverPlayer` 增加 `Position`、`VelocityPerSecond` 和 `HasPosition`，worker 后续只会读取这些纯托管值。
- 新增 `PlayerVisibilityMotionTracker`，按 `GameObjectId` 维护上一轮位置样本，并计算每秒速度向量。
- 速度样本超过 `1000ms` 或没有上一轮样本时归零，避免使用过旧数据做预测。
- motion tracker 会按当前 plan 中仍存在的对象裁剪历史，`ObjectCuller.Clear()` 时一并清空。
- solver snapshot 记录 `PositionSampleCount`，慢帧日志 solver 段扩展为 `solver[input=... budget=... pos=... resultAge=...ms]`。

保留行为：

- 位置和速度只进入 solver snapshot，不参与旧排序目标生成。
- `PlayerVisibilityLegacyTargetBuilder` 仍然是实际目标集合来源。
- 未引入 worker、CP-SAT 主链路求解、预测效用或结果采纳。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。

下一步建议：

- Phase 2 继续：引入单 worker/latest-wins 调度骨架，先只接收 solver snapshot 并发布统计，不采用结果。
- worker 统计阶段应先验证 result age、pending snapshot 覆盖次数和异常隔离，再进入 CP-SAT 模型实现。

## 2026-07-09 23:04 +08:00

本轮步长：继续 Phase 2，引入单 worker / latest-wins 调度骨架；worker 只发布统计，不产出或采纳目标集合。

完成内容：

- 新增 `PlayerVisibilitySolverWorker`，后台任务通过 `AutoResetEvent` 接收最新 solver snapshot。
- 提交语义为 latest-wins：任意时刻最多一个执行中的 snapshot 和一个 pending snapshot；新的 pending 会覆盖旧 pending，并累计 `PendingSnapshotReplacedCount`。
- `ObjectCuller` 在生成 `PlayerVisibilitySolverSnapshot` 后提交给 worker，但实际目标集合仍由 `PlayerVisibilityLegacyTargetBuilder` 生成。
- worker 当前只统计输入，不求解：记录 submitted、completed、replaced、exceptions、last generation、last input/budget/position/velocity sample、result age 和 worker 处理耗时。
- `PlayerVisibilitySolverPlayer` 增加 `HasVelocitySample`，区分“速度确实为 0”和“没有可靠上一帧速度样本”。
- `Clear()` 会清空 worker pending/统计并递增 epoch，防止清理前的后台结果晚到后覆盖新状态。
- `Dispose()` 会取消并唤醒 worker，等待后台任务退出后释放同步对象。
- 慢帧日志 solver 段扩展为 `worker[...]`，例如 `submitted=... completed=... replaced=... exceptions=... lastAge=...ms lastWorker=...ms`。

保留行为：

- worker 不访问 GameObject 指针、Dalamud service、GameGui、ImGui、NamePlate 或 VFX API。
- worker 不调用 CP-SAT，不生成目标集合，不修改 RenderFlags。
- solver 结果采纳、fallback 和状态验证尚未接入。
- 旧排序目标生成和现有执行器路径保持不变。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。

下一步建议：

- Phase 2 继续：实机观察慢帧日志中的 `worker[...]`，确认 `submitted/completed/replaced/exceptions/lastAge` 符合 latest-wins 预期。
- 若统计稳定，下一步进入 Phase 3 的不可见 CP-SAT worker 求解：先构造模型并发布求解统计，仍不采纳目标结果。

## 2026-07-10 10:26 +08:00

本轮步长：进入 Phase 3，接入不可见的 CP-SAT worker 影子求解和统计；求解结果仍不生成或采纳目标集合。

实机反馈结论：

- Phase 2 worker 在约 40-53 个 competitive 玩家、预算 25 的场景中累计完成 2833 次提交，`completed == submitted`、`replaced=0`、`exceptions=0`。
- worker 输入统计耗时约 0.001-0.004 ms，结果年龄为 0 ms；latest-wins 骨架可以继续承载真实后台求解。

本轮实现：

- `PlayerVisibilitySolverSnapshot` 增加本地玩家位置纯托管值，供 worker 计算其他玩家预测位置到本地参考点的距离；worker 仍不读取游戏对象或 Dalamud service。
- 新增 `PlayerVisibilityCpSatOptimizer`：固定 8 个预测步、200 ms 步长、`Gamma=0.85`、`Epsilon=0.03`、`UtilityScale=10000`。
- 量化效用第一版由 keep-rule rank 主项和预测距离软项构成；预测使用快照中的玩家位置/速度，本地参考位置在当前 8 步内保持不动。第一版仍不使用视口效用。
- 直接以每步 Top-B 计算 `JStar`，并加入 `J >= ceil(0.97 * JStar)`。
- 模型为每位 competitive 玩家、每个预测步建立可见变量和切换变量，加入逐步预算与绝对值切换约束，单次求解 `minimize M * D - J`。
- solver 固定 `num_search_workers:1 max_time_in_seconds:0.002`，只在已有专用 worker 中执行。
- OR-Tools native 依赖加载提取为可复用的 `OrToolsNativeDependencyLoader`；Phase 0 探针与真实 worker 共用同一加载状态。
- worker trace 增加 `cpSat[status vars constraints jStar threshold finalJ finalD stepSwitch model solve]`。

保留行为：

- CP-SAT 结果仅用于统计；`PlayerVisibilityLegacyTargetBuilder` 仍是唯一目标集合来源。
- 不发布 solver target result，不采纳 `x[:,0]`，不改变 RenderFlags、fade、show budget 或 reconciliation 行为。
- 超时/非可行状态的目标 fallback 尚未接入，因为当前没有结果采纳。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步建议：

- 实机观察 `cpSat[...]`：重点记录 `status`、`solve`、`lastWorker`、`replaced`、`exceptions`、`lastAge`，确认 40-60 人规模下 2 ms 上限的可行/最优比例和尾延迟。
- 同时观察 `finalJ >= threshold`、`finalD` 和 `stepSwitch` 是否合理；在数据稳定前不接入目标结果。

## 2026-07-10 10:49 +08:00

本轮步长：增加 CP-SAT worker 定期统计日志，避免只能等待超过慢帧阈值的 Framework 日志。

实现：

- `Plugin.OnFrameworkUpdate` 增加轻量时间检查，每约 30 秒上报一次最近缓存的 worker/CP-SAT 统计。
- 日志格式为 `[CP-SAT] Periodic worker[...]`，复用现有 `FormatSolverWorkerStats`，包含 submitted/completed/replaced/exceptions、结果年龄、worker 总耗时及完整 `cpSat[...]`。
- 首次获得有效 worker 统计后会立即输出第一条，之后按 30 秒间隔输出。
- 定期上报不扫描玩家、不提交额外快照、不触发额外求解，也不改变慢帧日志原有行为。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- 实机收集若干条 `[CP-SAT] Periodic worker[...]`，重点比较 `status`、`solve`、`lastWorker`、`replaced`、`exceptions` 和 `lastAge`。

## 2026-07-10 10:51 +08:00

实机反馈：

- worker 连续出现 `completed=0`、`exceptions=submitted`、`cpSat[status=n/a]`，说明每次处理都在完成 CP-SAT 统计前抛出异常，不是 2 ms 求解超时。
- 首次失败约 0.508 ms，后续失败约 0.024-0.030 ms；结合代码路径，最可能是 worker 使用 `Assembly.Location` 推导出的目录并非 Dalamud 插件实际依赖目录。

针对实机反馈处理：

- 删除 worker 对 `Assembly.GetExecutingAssembly().Location` 的目录推导。
- 将已由 Phase 0 实机验证的 `PluginInterface.AssemblyLocation.DirectoryName` 从 `Plugin` 经 `UpdateObjectArraysHook`、`ObjectCuller` 显式传入 `PlayerVisibilitySolverWorker`。
- worker 捕获异常后保存 `异常类型: 消息`；慢帧和定期统计增加 `lastError=...`，仍由游戏线程负责日志输出。
- 已确认 Debug 输出目录存在 `google-ortools-native.dll`。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- 重新加载后预期 `completed` 开始增长、`exceptions` 保持 0，并出现实际 `cpSat[status=...]`。
- 如果仍失败，直接依据新增的 `lastError` 定位，不再依赖异常计数猜测。

## 2026-07-10 10:53 +08:00

实机反馈：

- native 目录修复有效：worker 已达到 `exceptions=0`，并持续完成所有提交，`lastError=n/a`。
- 2 ms 上限下观测到的 CP-SAT 状态全部为 `Unknown`，`finalJ/finalD/stepSwitch` 因无可用解均为 0。
- 约 15-30 个 competitive 玩家时，模型约 240-480 个变量、129-249 个约束；模型构建约 0.076-0.145 ms，求解调用实际约 2.763-4.111 ms。
- 即使 `budget == input` 的简单场景仍为 `Unknown`，说明 2 ms 上限不足以跨过当前模型的初始化/预求解开销，不能进入结果采纳。
- worker 仍无积压：观测期间 `replaced=0`，结果年龄为 0 ms。

针对实机反馈处理：

- 按计划中的实验预案，将后台单线程求解上限从 2 ms 小步提高到 4 ms；仍固定 `num_search_workers:1`。
- worker 增加累计 `OptimalCount`、`FeasibleCount`、`UnknownCount`。
- 慢帧和 30 秒定期日志增加 `statuses[optimal=... feasible=... unknown=...]`，用于直接评估成功率，而不是只观察最后一次状态。
- 仍不采纳 CP-SAT 结果，不改变目标集合或 RenderFlags 行为。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- 重新加载后收集至少两条 `[CP-SAT] Periodic`，检查 `statuses` 中 Optimal/Feasible 是否开始增长，以及 Unknown 占比。
- 同时观察 4 ms 配置下的 `solve`、`lastWorker` 和 `replaced`；若仍几乎全 Unknown，应先缩减模型或调整求解建模方式，而不是继续盲目提高上限。

## 2026-07-10 10:56 +08:00

实机反馈：

- 4 ms 上限下累计 437 次完成仍全部为 `Unknown`，`optimal=0 feasible=0 unknown=437`。
- 实际求解调用约 3.151-5.372 ms；约 15-36 个 competitive 玩家、240-576 个变量时均未得到可用解。
- `replaced=0`、`exceptions=0`、结果年龄 0-15 ms，线程调度和 latest-wins 没有问题，瓶颈集中在 CP-SAT 在短时限内尚未建立 incumbent。

针对实机反馈处理：

- 保持 8 步模型、4 ms 上限和单线程，不继续提高时限。
- 计算 Top-B `JStar` 时同时保留每一步的理论最优选择，并通过 `AddHint` 为全部可见变量和切换变量提供一个满足预算且达到 `JStar` 的完整初始解。
- 当 `competitiveBudget == competitivePlayers.Count` 时没有预算竞争，直接解析得到全员可见的最优解，状态记为 `OptimalByInspection`，`solve=0`；初始隐藏到全员可见的变化计入 `finalD/stepSwitch`。
- `OptimalByInspection` 计入累计 `optimal`，便于定期日志统一统计。
- CP-SAT 结果仍仅用于影子统计，不采纳目标集合。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- `budget == input` 场景应出现 `status=OptimalByInspection`、`solve=0`，累计 optimal 增长。
- `input > budget` 场景观察完整 Top-B hint 是否使 Feasible/Optimal 开始增长；若仍全部 Unknown，下一步缩减或重写 CP-SAT 模型结构。

## 2026-07-10 10:59 +08:00

实机反馈：

- 无竞争快速路径累计完成 641 次，`exceptions=0`、`replaced=0`、结果年龄 0 ms。
- `input=20-23` 且 `budget=input`，状态均为 `OptimalByInspection`，`finalJ=JStar`，`solve=0`。
- worker 总耗时约 0.023-0.026 ms，说明无竞争场景已不再承担 CP-SAT 初始化成本。
- 本批数据没有出现 `input > budget`，因此尚未实际运行带 Top-B hint 的 CP-SAT，不能据此判断求解成功率。

统计语义调整：

- `OptimalByInspection` 不再计入 solver 的 `optimal`。
- 累计状态增加独立 `inspected`，日志格式变为 `statuses[optimal=... feasible=... unknown=... inspected=...]`。
- 这样 `optimal/feasible/unknown` 只反映真正进入 CP-SAT 的样本，避免快速路径掩盖求解器表现。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- 需要在 `input > budget` 的拥挤场景收集定期日志；届时 `inspected` 与真正 solver 状态会分别累计。

## 2026-07-10 11:02 +08:00

实机反馈：

- 已获得真正超预算样本：累计 `unknown=131`，`optimal=0`、`feasible=0`；完整 Top-B hint 没有使 4 ms 内产生可用 incumbent。
- 超预算样本约 `input=27-31`、`budget=25`，8 步模型约 432-496 个变量、225-257 个约束。
- 模型构建约 0.143-0.161 ms，求解调用约 4.611-5.081 ms，状态仍全部 `Unknown`。
- 无竞争快速路径继续正常累计，`inspected=427`；`exceptions=0`、`replaced=0`，worker 调度不是瓶颈。

针对实机反馈处理：

- 不继续提高 4 ms 时限。
- 将预测步数从 8 缩减为 4，预测步长仍为 200 ms，形成 800 ms 预测窗口。
- 完整 Top-B hint、`JThreshold`、切换目标、单线程和其他参数保持不变，用于隔离模型规模的影响。
- 预计超预算模型变量和逐玩家切换约束约减半；仍只做影子求解，不采纳结果。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- 在同类 `input > 25` 场景比较 4 步模型的变量/约束数和 `statuses`。
- 若 4 步模型仍全部 Unknown，则不再继续压缩预测窗口，转而重写模型/求解策略。

## 2026-07-10 11:06 +08:00

实机反馈：

- 4 步模型累计真正求解 222 次，仍为 `optimal=0 feasible=0 unknown=222`；缩减预测窗口没有解决短时限内无 incumbent 的问题。
- 超预算模型约 208-280 个变量、109-145 个约束，模型构建约 0.077-0.113 ms，求解调用约 3.727-4.797 ms。
- `exceptions=0`、`replaced=0`，无竞争快速路径继续正常；停止继续压缩预测窗口。
- 多个不同输入规模的 `JStar` 固定为 `796650`，说明当前量化效用存在大量相同系数，模型具有较强对称性。

针对实机反馈处理：

- 保持 4 步、4 ms、单线程、完整 Top-B hint 和目标函数不变。
- solver 参数增加 `symmetry_level:0`，关闭当前高度对称模型的对称性检测。
- solver 参数增加 `cp_model_probing_level:0`，关闭短时限下可能占据主要启动时间的 probing。
- 该轮用于隔离 CP-SAT 预处理启动成本；仍不采纳结果。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- 比较关闭 symmetry/probing 后的 `optimal/feasible/unknown` 和 `solve`。
- 若仍为 100% Unknown，不再继续微调 solver 参数，改写切换变量和目标表达。

## 2026-07-10 11:10 +08:00

实机反馈：

- 关闭 symmetry/probing 后，真正求解累计达到 `optimal=10 feasible=190 unknown=0`；短时限内无 incumbent 的问题已解决。
- 常态超预算求解约 4.25-4.49 ms，worker 约 4.35-4.58 ms，结果年龄通常 0-15 ms，`replaced=0`、`exceptions=0`。
- 可行解满足阈值；示例 `JStar=795777`、`threshold=771904`、`finalJ=773267`，并输出 `finalD=6 stepSwitch=5`，表明求解器确实会用允许的效用余量减少切换，而非只返回 Top-B hint。
- 首个真正求解出现一次性冷启动尖峰：`model=18.336 ms`、`solve=110.837 ms`、worker 130.963 ms、结果年龄 125 ms。后续恢复正常，属于 OR-Tools 首次模型/求解初始化成本。

针对实机反馈处理：

- `PlayerVisibilityCpSatOptimizer` 增加最小模型 `WarmUp()`。
- worker 启动后、接收真实快照前，在后台完成 native 依赖加载和一次最小求解预热；不阻塞插件构造或 Framework 线程。
- 预热期间仍保持 latest-wins：游戏线程可以继续提交，worker 预热完成后只处理最新待处理快照。
- worker 统计增加 `init=...ms`，单独记录后台初始化/预热耗时，避免与真实快照的 `model/solve/lastWorker` 混在一起。
- 若预热失败，真实快照处理会重试初始化，并通过现有 `lastError` 暴露错误。
- 求解结果仍不采纳，不改变玩家显示行为。

验证结果：

- `dotnet csharpier format .`：已运行。
- `dotnet build EventHorizon.sln`：通过，0 warning，0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning，0 error。
- `git diff --check`：通过；仅有仓库现有换行符提示。

下一步观察：

- 重新加载后观察 `init`、首个真正 CP-SAT 样本的 `model/solve/lastWorker`，确认约 100 ms 的冷启动成本已迁移到后台预热。
- 同时确认预热期间即使出现 `replaced`，随后结果年龄仍快速恢复到目标范围；在验证前继续不采纳结果。

## 2026-07-10 11:13 +08:00

实机反馈：

- 后台预热耗时 `init=63.398 ms`，已从真实快照的 model/solve 统计中分离。
- 预热后的真实求解累计 `optimal=27 feasible=260 unknown=0`，继续保持 100% 可用状态。
- 常态模型构建约 0.073-0.141 ms，求解约 3.344-4.394 ms，worker 总耗时约 3.429-4.559 ms。
- 结果年龄通常 0-16 ms，`replaced=0`、`exceptions=0`；预热没有造成可见积压。
- 未再出现此前首个真实模型 `model=18 ms / solve=111 ms` 的冷启动尖峰，说明预热迁移有效。
- 可行解继续满足 `FinalJ >= JThreshold`；例如 `JStar=796650`、`threshold=772751`、`finalJ=774784`、`finalD=5`、`stepSwitch=4`。

阶段结论：

- 4 步、200 ms 步长、4 ms 上限、单线程、关闭 symmetry/probing 的影子求解在当前约 26-37 人输入规模下已达到稳定可用状态。
- Framework 线程不等待 solver，worker 无异常、无任务积压，结果年龄显著低于 300 ms 目标。
- 影子求解性能验证通过；仍未发布或采纳 solver 目标集合，因此玩家显示行为未改变。

下一步建议：

- 进入 Phase 3 的结果通道：worker 发布 immutable current-step result，游戏线程只读取并验证 generation、年龄和对象 identity，先统计 accepted/stale/invalid，仍不替换 legacy target set。
