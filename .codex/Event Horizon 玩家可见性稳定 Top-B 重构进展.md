# Event Horizon 玩家可见性稳定 Top-B 重构进展

源计划：`.codex/Event Horizon 玩家可见性稳定 Top-B 重构.md`

## 2026-07-10：Phase 0 与 Phase 1 完成

### 迁移审计结论

- 当前正式 target set 仍由 `PlayerVisibilityLegacyTargetBuilder.Build(...)` 产生，CP-SAT worker 只消费影子 snapshot，结果从未接入正式 target set。
- 可直接复用：`PlayerKeepRules`、`PlayerKeepPlan`、`PlayerVisibilityPlan`、四类分类、`PlayerVisibilityTargetSet`、generation、`PlayerObjectIdentity`、`PlayerVisibilityReconciler`、`HiddenObjectTracker`。
- 需要改造：`ObjectCuller`、`Plugin`、`UpdateObjectArraysHook`、`CullingPerformanceTrace` 中的 CP-SAT 接线和统计。
- 应当删除：OR-Tools 依赖、native loader、probe、隐藏命令、optimizer、worker，以及只服务于该 worker 的 solver snapshot 和 motion tracker。
- 当前 native detour 在 original 后调用 `ObjectCuller.Tick(...)`，会推进现有 reconcile/fade/RenderFlags 执行；`Framework.Update` 负责动态刷新检查、每帧 tick 和定期完整 refresh。Phase 1 为保持正式行为不变，没有提前迁移这两条路径的职责。

### Phase 0

- 旧 CP-SAT 计划和旧进展文件已标记为废止，并链接到稳定 Top-B 新计划。
- 本进展文件已经建立。
- 明确停止旧计划 Phase 3 及后续内容。

### Phase 1 删除内容

- 删除 `Google.OrTools` NuGet 引用，并更新 `packages.lock.json`，移除全部 OR-Tools runtime 与 protobuf 传递依赖。
- 删除 `CpSatPhase0Probe`、`OrToolsNativeDependencyLoader`、`PlayerVisibilityCpSatOptimizer`、`PlayerVisibilitySolverWorker`。
- 删除仅供旧 worker 使用的 `PlayerVisibilitySolverSnapshot`、`PlayerVisibilityMotionTracker` 和 solver DTO。
- 删除 `/eventhorizon cpsat`、`/eventhorizon cp-sat` 隐藏命令。
- 删除 CP-SAT 周期日志、worker/solver 格式化和 culling trace 专属字段。
- 删除构造链中仅用于 native loader 的 plugin directory 参数。

### 保留边界

- 正式玩家可见性仍由 legacy builder 唯一生成；未加入 Top-B selector 或第二套影子求解器。
- 分类、规则预算、target set、generation、身份验证、reconcile、HiddenObjectTracker 保持原路径。
- fade、VFX、preview 代码和正式 target 生成语义未修改。
- detour 与 `Framework.Update` 的现有执行职责保持不变。

### 验证

- `dotnet csharpier format .`：通过，52 个文件检查/格式化。
- `dotnet build EventHorizon.sln -c Debug`：通过，0 error；运行中的 FFXIV 锁住旧 Debug 输出目录内的 OR-Tools DLL，产生 10 个旧文件清理 warning。
- 独立空 Debug 输出目录重新 build：通过，0 warning、0 error；确认没有生成 OR-Tools 或其 native runtime 文件。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning、0 error。
- `dotnet test EventHorizon.sln -c Debug --no-build` 与 Release：命令通过；当前 solution 没有测试项目或测试用例输出。
- 源码、项目文件和 lockfile 的残留扫描未发现 OR-Tools、CP-SAT、probe、optimizer、worker 或 solver 引用。

### 下一阶段入口

Phase 2 从新的纯同步 selection 数据边界开始：建立 selection snapshot、parameters、candidate 和 result，并实现确定性稳定 Top-B 选择器及单元测试。本阶段尚未实现任何 Phase 2 内容。
