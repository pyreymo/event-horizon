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
