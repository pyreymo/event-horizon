# Event Horizon 玩家可见性 CP-SAT 优化进展

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

---
实机反馈：

游戏内加载 C:\Users\Administrator\Documents\Repos\event-horizon\EventHorizon\bin\Debug\win-x64\EventHorizon.dll

运行 /eh cpsat 报错，错误日志如下：

22:16:07.843 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:16:07.864 | 错误 | [EventHorizon] [CP-SAT Phase 0] Probe failed.
	System.TypeInitializationException: The type initializer for 'Google.OrTools.Sat.operations_research_satPINVOKE' threw an exception.
	 ---> System.TypeInitializationException: The type initializer for 'SWIGExceptionHelper' threw an exception.
	 ---> System.DllNotFoundException: Unable to load DLL 'C:\Users\Administrator\AppData\Local\Temp\tfnvcumw.0uc\google-ortools-native.dll' or one of its dependencies: 找不到指定的模块。 (0x8007007E)
	   at System.Runtime.InteropServices.NativeLibrary.Load(String libraryPath)
	   at System.Runtime.Loader.AssemblyLoadContext.LoadUnmanagedDllFromPath(String unmanagedDllPath)
	   at Dalamud.Plugin.Internal.Loader.ManagedLoadContext.LoadUnmanagedDll(String unmanagedDllName) in /_/Dalamud/Plugin/Internal/Loader/ManagedLoadContext.cs:line 219
	   at Google.OrTools.Sat.operations_research_satPINVOKE.SWIGExceptionHelper.SWIGRegisterExceptionCallbacks_operations_research_sat(ExceptionDelegate applicationDelegate, ExceptionDelegate arithmeticDelegate, ExceptionDelegate divideByZeroDelegate, ExceptionDelegate indexOutOfRangeDelegate, ExceptionDelegate invalidCastDelegate, ExceptionDelegate invalidOperationDelegate, ExceptionDelegate ioDelegate, ExceptionDelegate nullReferenceDelegate, ExceptionDelegate outOfMemoryDelegate, ExceptionDelegate overflowDelegate, ExceptionDelegate systemExceptionDelegate)
	   at Google.OrTools.Sat.operations_research_satPINVOKE.SWIGExceptionHelper.SWIGRegisterExceptionCallbacks_operations_research_sat(ExceptionDelegate applicationDelegate, ExceptionDelegate arithmeticDelegate, ExceptionDelegate divideByZeroDelegate, ExceptionDelegate indexOutOfRangeDelegate, ExceptionDelegate invalidCastDelegate, ExceptionDelegate invalidOperationDelegate, ExceptionDelegate ioDelegate, ExceptionDelegate nullReferenceDelegate, ExceptionDelegate outOfMemoryDelegate, ExceptionDelegate overflowDelegate, ExceptionDelegate systemExceptionDelegate)
	   at Google.OrTools.Sat.operations_research_satPINVOKE.SWIGExceptionHelper..cctor()
	   --- End of inner exception stack trace ---
	   at Google.OrTools.Sat.operations_research_satPINVOKE..cctor()
	   --- End of inner exception stack trace ---
	   at Google.OrTools.Sat.operations_research_satPINVOKE.new_SolveWrapper()
	   at Google.OrTools.Sat.CpSolver.CreateSolveWrapper()
	   at Google.OrTools.Sat.CpSolver.Solve(CpModel model, SolutionCallback cb)
	   at EventHorizon.Culling.Optimization.CpSatPhase0Probe.Run(CancellationToken cancellationToken) in C:\Users\Administrator\Documents\Repos\event-horizon\EventHorizon\Culling\Optimization\CpSatPhase0Probe.cs:line 82

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

---
实机反馈：

验收通过。

22:20:59.661 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:20:59.684 | 信息 | [EventHorizon] [CP-SAT Phase 0] Loaded OR-Tools native dependencies from "C:\Users\Administrator\Documents\Repos\event-horizon\EventHorizon\bin\Debug\win-x64".
22:20:59.720 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe finished status=Optimal objective=180 selected=2/3 model=10.665ms solve=35.480ms total=56.436ms wall=1.120ms parameters="num_search_workers:1 max_time_in_seconds:1.0"

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

---
实机反馈：

性能补测结果，首次加载时间较长，后续计算速度正常。

22:37:19.327 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:37:19.341 | 信息 | [EventHorizon] [CP-SAT Phase 0] Loaded OR-Tools native dependencies from "C:\Users\Administrator\Documents\Repos\event-horizon\EventHorizon\bin\Debug\win-x64".
22:37:19.374 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe finished status=Optimal objective=180 selected=2/3 model=10.908ms solve=32.918ms total=44.759ms wall=0.420ms parameters="num_search_workers:1 max_time_in_seconds:1.0"
22:37:21.034 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:37:21.035 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe finished status=Optimal objective=180 selected=2/3 model=0.034ms solve=0.417ms total=0.452ms wall=0.329ms parameters="num_search_workers:1 max_time_in_seconds:1.0"
22:37:22.232 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:37:22.233 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe finished status=Optimal objective=180 selected=2/3 model=0.020ms solve=0.401ms total=0.422ms wall=0.329ms parameters="num_search_workers:1 max_time_in_seconds:1.0"
22:37:23.399 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:37:23.399 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe finished status=Optimal objective=180 selected=2/3 model=0.013ms solve=0.401ms total=0.416ms wall=0.330ms parameters="num_search_workers:1 max_time_in_seconds:1.0"
22:37:24.374 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:37:24.374 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe finished status=Optimal objective=180 selected=2/3 model=0.013ms solve=0.407ms total=0.421ms wall=0.336ms parameters="num_search_workers:1 max_time_in_seconds:1.0"
22:37:25.365 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe started on a background worker.
22:37:25.366 | 信息 | [EventHorizon] [CP-SAT Phase 0] Probe finished status=Optimal objective=180 selected=2/3 model=0.022ms solve=0.539ms total=0.563ms wall=0.464ms parameters="num_search_workers:1 max_time_in_seconds:1.0"

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

---
实机反馈：

23:05:37.120 | 信息 | [EventHorizon] [Perf] Slow Plugin.OnFrameworkUpdate total=3.460ms dtr=0.000ms layoutGraphics=0.001ms highlight=0.000ms dynamicCheck=0.001ms tick=0.051ms refresh=3.405ms didRefresh=True tickTrace="total=0.050 guard=0.001 keep=0.000 plan=0.000 reconcile=0.000 preview=0.000 solver[input=40 budget=25 pos=40 resultAge=250ms worker[submitted=370 completed=370 replaced=0 exceptions=0 lastGen=370 lastInput=40 lastBudget=25 lastPos=40 lastVel=40 lastAge=0ms lastWorker=0.001ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=40 forceHidden=55 unmanaged=1] actions=99 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.048 playerActions=0.005 nonPlayer=0.031 pruneHidden=0.011 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=99]" refreshTrace="total=3.405 guard=0.020 keep=0.070 plan=3.266 reconcile=0.009 preview=0.000 solver[input=40 budget=25 pos=40 resultAge=0ms worker[submitted=371 completed=371 replaced=0 exceptions=0 lastGen=371 lastInput=40 lastBudget=25 lastPos=40 lastVel=40 lastAge=0ms lastWorker=0.004ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=40 forceHidden=55 unmanaged=1] actions=99 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.040 playerActions=0.002 nonPlayer=0.028 pruneHidden=0.010 pruneFades=0.000 hiddenVfx=0.000 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=99]"
23:06:17.453 | 信息 | [EventHorizon] [Perf] Slow Plugin.OnFrameworkUpdate total=2.404ms dtr=0.000ms layoutGraphics=0.001ms highlight=0.000ms dynamicCheck=0.001ms tick=0.049ms refresh=2.352ms didRefresh=True tickTrace="total=0.049 guard=0.001 keep=0.000 plan=0.000 reconcile=0.000 preview=0.000 solver[input=42 budget=25 pos=42 resultAge=234ms worker[submitted=534 completed=534 replaced=0 exceptions=0 lastGen=534 lastInput=42 lastBudget=25 lastPos=42 lastVel=42 lastAge=0ms lastWorker=0.001ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=42 forceHidden=53 unmanaged=1] actions=99 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.046 playerActions=0.004 nonPlayer=0.030 pruneHidden=0.011 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=99]" refreshTrace="total=2.351 guard=0.021 keep=0.070 plan=2.192 reconcile=0.008 preview=0.000 solver[input=42 budget=25 pos=42 resultAge=0ms worker[submitted=535 completed=535 replaced=0 exceptions=0 lastGen=535 lastInput=42 lastBudget=25 lastPos=42 lastVel=42 lastAge=0ms lastWorker=0.004ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=42 forceHidden=53 unmanaged=1] actions=99 pendingShow=0 pendingHide=1 previewActive=False tick[total=0.059 playerActions=0.007 nonPlayer=0.025 pruneHidden=0.010 pruneFades=0.016 hiddenVfx=0.000 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=99]"
23:08:27.861 | 信息 | [EventHorizon] [Perf] Slow Plugin.OnFrameworkUpdate total=2.426ms dtr=0.026ms layoutGraphics=0.001ms highlight=0.000ms dynamicCheck=0.000ms tick=0.050ms refresh=2.349ms didRefresh=True tickTrace="total=0.049 guard=0.001 keep=0.000 plan=0.000 reconcile=0.000 preview=0.000 solver[input=45 budget=25 pos=45 resultAge=235ms worker[submitted=1066 completed=1066 replaced=0 exceptions=0 lastGen=1066 lastInput=45 lastBudget=25 lastPos=45 lastVel=45 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=45 forceHidden=49 unmanaged=1] actions=98 pendingShow=0 pendingHide=1 previewActive=False tick[total=0.047 playerActions=0.009 nonPlayer=0.027 pruneHidden=0.010 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=98]" refreshTrace="total=2.348 guard=0.019 keep=0.068 plan=2.194 reconcile=0.011 preview=0.000 solver[input=45 budget=25 pos=45 resultAge=0ms worker[submitted=1067 completed=1067 replaced=0 exceptions=0 lastGen=1067 lastInput=45 lastBudget=25 lastPos=45 lastVel=45 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=45 forceHidden=49 unmanaged=1] actions=98 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.054 playerActions=0.002 nonPlayer=0.041 pruneHidden=0.010 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=98]"
23:10:38.002 | 信息 | [EventHorizon] [Perf] Slow Plugin.OnFrameworkUpdate total=2.982ms dtr=0.001ms layoutGraphics=0.001ms highlight=0.000ms dynamicCheck=0.001ms tick=0.055ms refresh=2.924ms didRefresh=True tickTrace="total=0.054 guard=0.001 keep=0.000 plan=0.000 reconcile=0.000 preview=0.000 solver[input=47 budget=25 pos=47 resultAge=250ms worker[submitted=1597 completed=1597 replaced=0 exceptions=0 lastGen=1597 lastInput=47 lastBudget=25 lastPos=47 lastVel=45 lastAge=0ms lastWorker=0.003ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=47 forceHidden=47 unmanaged=1] actions=97 pendingShow=1 pendingHide=11 previewActive=False tick[total=0.052 playerActions=0.012 nonPlayer=0.028 pruneHidden=0.011 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=97]" refreshTrace="total=2.923 guard=0.019 keep=2.782 plan=0.076 reconcile=0.009 preview=0.000 solver[input=47 budget=25 pos=47 resultAge=0ms worker[submitted=1598 completed=1598 replaced=0 exceptions=0 lastGen=1598 lastInput=47 lastBudget=25 lastPos=47 lastVel=47 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=47 forceHidden=47 unmanaged=1] actions=98 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.036 playerActions=0.002 nonPlayer=0.023 pruneHidden=0.011 pruneFades=0.000 hiddenVfx=0.000 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=98]"
23:10:55.258 | 信息 | [EventHorizon] [Perf] Slow Plugin.OnFrameworkUpdate total=2.862ms dtr=0.000ms layoutGraphics=0.001ms highlight=0.000ms dynamicCheck=0.001ms tick=0.051ms refresh=2.808ms didRefresh=True tickTrace="total=0.051 guard=0.001 keep=0.000 plan=0.000 reconcile=0.000 preview=0.000 solver[input=46 budget=25 pos=46 resultAge=250ms worker[submitted=1668 completed=1668 replaced=0 exceptions=0 lastGen=1668 lastInput=46 lastBudget=25 lastPos=46 lastVel=44 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=46 forceHidden=49 unmanaged=1] actions=99 pendingShow=0 pendingHide=5 previewActive=False tick[total=0.048 playerActions=0.009 nonPlayer=0.027 pruneHidden=0.011 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=99]" refreshTrace="total=2.808 guard=0.019 keep=2.655 plan=0.089 reconcile=0.009 preview=0.000 solver[input=46 budget=25 pos=46 resultAge=0ms worker[submitted=1669 completed=1669 replaced=0 exceptions=0 lastGen=1669 lastInput=46 lastBudget=25 lastPos=46 lastVel=46 lastAge=0ms lastWorker=0.004ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=46 forceHidden=49 unmanaged=1] actions=99 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.034 playerActions=0.002 nonPlayer=0.022 pruneHidden=0.010 pruneFades=0.000 hiddenVfx=0.000 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=99]"
23:11:40.238 | 信息 | [EventHorizon] [Perf] Slow Plugin.OnFrameworkUpdate total=2.045ms dtr=0.026ms layoutGraphics=0.001ms highlight=0.000ms dynamicCheck=0.000ms tick=0.051ms refresh=1.965ms didRefresh=True tickTrace="total=0.051 guard=0.001 keep=0.000 plan=0.000 reconcile=0.000 preview=0.000 solver[input=50 budget=25 pos=50 resultAge=250ms worker[submitted=1851 completed=1851 replaced=0 exceptions=0 lastGen=1851 lastInput=50 lastBudget=25 lastPos=50 lastVel=50 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=50 forceHidden=45 unmanaged=1] actions=98 pendingShow=1 pendingHide=1 previewActive=False tick[total=0.049 playerActions=0.010 nonPlayer=0.027 pruneHidden=0.011 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=98]" refreshTrace="total=1.965 guard=0.019 keep=0.072 plan=1.829 reconcile=0.008 preview=0.000 solver[input=50 budget=25 pos=50 resultAge=0ms worker[submitted=1852 completed=1852 replaced=0 exceptions=0 lastGen=1852 lastInput=50 lastBudget=25 lastPos=50 lastVel=50 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=50 forceHidden=45 unmanaged=1] actions=99 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.036 playerActions=0.002 nonPlayer=0.024 pruneHidden=0.010 pruneFades=0.000 hiddenVfx=0.000 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=99]"
23:15:41.393 | 信息 | [EventHorizon] [Perf] Slow Plugin.OnFrameworkUpdate total=3.739ms dtr=0.000ms layoutGraphics=0.001ms highlight=0.000ms dynamicCheck=0.001ms tick=0.064ms refresh=3.671ms didRefresh=True tickTrace="total=0.064 guard=0.001 keep=0.000 plan=0.000 reconcile=0.000 preview=0.000 solver[input=53 budget=25 pos=53 resultAge=234ms worker[submitted=2832 completed=2832 replaced=0 exceptions=0 lastGen=2832 lastInput=53 lastBudget=25 lastPos=53 lastVel=53 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=53 forceHidden=41 unmanaged=1] actions=98 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.061 playerActions=0.005 nonPlayer=0.029 pruneHidden=0.026 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=98]" refreshTrace="total=3.670 guard=0.019 keep=0.074 plan=0.133 reconcile=0.009 preview=0.000 solver[input=53 budget=25 pos=53 resultAge=0ms worker[submitted=2833 completed=2833 replaced=0 exceptions=0 lastGen=2833 lastInput=53 lastBudget=25 lastPos=53 lastVel=53 lastAge=0ms lastWorker=0.002ms ok=True]] previewTrace[n/a] classes[bypass=4 competitive=53 forceHidden=41 unmanaged=1] actions=98 pendingShow=0 pendingHide=0 previewActive=False tick[total=0.037 playerActions=0.002 nonPlayer=0.023 pruneHidden=0.011 pruneFades=0.000 hiddenVfx=0.001 hiddenVfxTrace[collect=0.000 project=0.000 show=0.000 prune=0.000 clear=0.000 hidden=0 visible=0 active=0 created=0 updated=0 skipped=0 removed=0 deferred=0] unaccounted=0.000 actions=98]"
