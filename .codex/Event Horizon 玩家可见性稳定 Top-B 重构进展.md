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

## 2026-07-10：Phase 2 完成

### 文档修正

- 修复新计划中丢失的 `\(`、`\[` 数学分隔符和 `\lambda_t` 附近误生成的 Markdown 标题分隔线，仅修复排版，未改变公式语义。
- 将完全移动测试条件收紧为：有效旧目标数量不超过预算时全部保留；预算小于有效旧目标数量时只保留其中 `B` 个。

### 新增与修改文件

- 新增 `EventHorizon/Culling/Selection/PlayerVisibilitySelectionModels.cs`：candidate、snapshot、scored candidate 和独立只读 result。
- 新增 `EventHorizon/Culling/Selection/PlayerVisibilitySelectionParameters.cs`：集中默认参数与构造时不变量验证。
- 新增 `EventHorizon/Culling/Selection/PlayerVisibilitySelector.cs`：同步纯计算的软分、运动因子、保留奖励和确定性 Top-B。
- 新增 `EventHorizon/Culling/Selection/LocalSpeedSmoother.cs`：按时间间隔计算 alpha 的本地速度 EMA。
- 新增 `EventHorizon.Tests/EventHorizon.Tests.csproj`、`packages.lock.json` 和 `PlayerVisibilitySelectorTests.cs`：真实 MSTest 项目与 23 个测试。
- 修改 `EventHorizon/EventHorizon.csproj`：仅增加 `InternalsVisibleTo`。
- 修改 `EventHorizon.sln`：加入测试项目。
- 修改新计划文档：修复公式排版与移动预算测试条件。

### 最终默认参数

```text
RankCount                 = 8
RankStep                  = 3000
SoftScoreScale            = 1000
RestRetentionBonus        = 500
MoveRetentionBonus        = 23000
PredictionSteps           = 4
PredictionStepSeconds     = 0.2
PredictionGamma           = 0.85
DistanceSigma             = 30.0
MotionStartSpeed          = 0.5
MotionFullSpeed           = 4.0
LocalSpeedHalfLifeSeconds = 0.35
MaxTrustedLocalSpeed      = 50.0
```

运动阈值、半衰期和最大可信速度是 Phase 3 影子验证前的保守内部初值，不是实机调优结论，未增加配置 UI。

### 不变量与确定性保证

- 参数构造立即验证 `RankCount >= 2`、rank 层级严格主导静止保留奖励、完全移动奖励覆盖全部 rank/软分范围、预测参数范围、运动阈值顺序、EMA 半衰期和最大可信速度；非法值抛出带参数名和约束说明的 `ArgumentOutOfRangeException`。
- selector 在计算前复制 candidate 集合，验证 rank 范围和 `SourceIndex` 唯一性，不修改 snapshot 或调用方集合。
- 非有限 position 软分为 0；非有限 velocity 按零速度；任何非有限预测结果回退为 0；最终软分只在浮点误差边界 Clamp 到 `[0,1]`。
- 软分按 `MidpointRounding.AwayFromZero` 量化；`SoftPoints`、`BaseScore`、`AdjustedScore` 和 retention bonus 使用 `long`，排序不比较浮点数。
- 排序严格使用 AdjustedScore、旧目标标记、BaseScore、GameObjectId、EntityId、ObjectIndex、SourceIndex。
- result 使用 selector 内新建数组的只读包装，不引用 selector 工作列表或调用方 candidate 集合。
- EMA 使用 `alpha = 1 - exp(-ln(2) * dt / HalfLife)`；首样本为 0，非正 `dt` 不更新，非有限位置不更新，瞬移/超速样本按零速度处理并重建位置和时间基线。

### 测试

Debug 和 Release 均实际发现并执行 23 个测试，范围包括：

- 预算边界、全部选中、空选择、预算缩小；
- 重复输入确定性、完整稳定键排序、输入集合不变；
- 零 retention、静止 rank 主导、完全移动旧目标保留、旧目标缺失填补、retention 单调性；
- 正常/零/远距/相对运动软分、无效 position/velocity；
- rank 和参数不变量异常；
- EMA 首样本、不同采样间隔一致性、非正 `dt`、非有限位置、瞬移恢复；
- NaN、负值和正 Infinity 本地速度的确定行为。

### 验证结果

- `dotnet csharpier format .`：通过，58 个文件检查/格式化。
- `dotnet build EventHorizon.sln -c Debug`：通过，0 warning、0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning、0 error。
- `dotnet test EventHorizon.sln -c Debug`：发现并通过 23/23。
- `dotnet test EventHorizon.sln -c Release`：发现并通过 23/23。
- Debug/Release 插件与测试产物、两个 lockfile 均未发现 OR-Tools 或其 native runtime 文件。

### 边界确认与下一阶段入口

- Phase 2 代码没有接入 `ObjectCuller`、`Plugin`、`UpdateObjectArraysHook`、legacy builder、正式 target set、reconciler、RenderFlags、fade、VFX 或 preview。
- 没有后台 worker、日志、Dalamud/FFCS 访问或全局配置读取。
- Phase 3 的入口是由未来 Framework 适配层构造 selection snapshot，并仅做 legacy/Top-B 影子差异验证；本阶段未实现该调用、日志或正式接管。

## 2026-07-10：Phase 2.1 完成

### 修正内容

- `PlayerVisibilitySelectionParameters` 使用 `decimal` 计算 `MaxBaseScore = (RankCount - 1) * RankStep + SoftScoreScale`，并在构造阶段验证 `MaxBaseScore + MoveRetentionBonus <= long.MaxValue`。违反约束时立即抛出说明最大调整分会超出 `Int64.MaxValue` 的 `ArgumentOutOfRangeException`，不再推迟到 selector 的 `checked` 运算。
- `LocalSpeedSmoother` 增加只读状态 `HasVelocityEstimate`：首个有效样本仅建立基线；首个正时间间隔且速度可信的样本建立估计；非有限位置和非正时间间隔不改变状态；瞬移/超速重建基线并使估计失效；`Reset()` 清除速度、基线和有效状态。
- 没有改变 selector snapshot、默认参数、数学模型、分数量化或排序顺序。

### 新增性质测试

- 最大调整分溢出在参数构造阶段失败。
- 零距离零速度软分精确为 1。
- `DistanceSigma` 距离、静止时软分约为 0.5。
- 近距离软分严格高于远距离。
- 相同初始距离下，靠近严格高于静止，静止严格高于远离。
- 候选人数严格小于预算时全部选中。
- 重复 `SourceIndex` 抛出带参数名和重复值的明确异常。
- motion start、区间中点和 full speed 分别得到 0、`(0,1)` 和 1 的运动因子。
- `LocalSpeedSmoother.Reset()` 清除速度、基线和有效状态。
- 瞬移后从新基线用下一份正常样本重新建立有效速度估计。
- 既有首样本、非有限位置和非正时间间隔测试同时增加有效状态断言。

### 验证结果

- `dotnet csharpier format .`：通过，58 个文件检查/格式化。
- `dotnet build EventHorizon.sln -c Debug`：通过，0 warning、0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning、0 error。
- `dotnet test EventHorizon.sln -c Debug`：实际发现并通过 33/33。
- `dotnet test EventHorizon.sln -c Release`：实际发现并通过 33/33。

### 边界确认

- 本轮只修改 selection 参数、速度平滑器、单元测试和本进展文件。
- 未接入 Phase 3；未修改 `ObjectCuller`、legacy target、reconciler、hook、RenderFlags、fade、VFX 或 preview，正式玩家可见性行为不变。
