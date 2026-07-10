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

## 2026-07-10：Phase 3 完成（代码与自动化验证）

### 新增与修改文件

- 新增 `EventHorizon/Culling/Visibility/PlayerVisibilityShadowController.cs`：shadow snapshot 适配、独立历史、churn、legacy 差异、rank histogram、状态和 trace。
- 新增 `EventHorizon/Culling/Selection/PlayerVelocityTracker.cs`：与游戏对象无关、按完整 identity 分桶的玩家向量速度 EMA。
- 新增 `EventHorizon.Tests/PlayerVisibilityShadowControllerTests.cs`：21 个 shadow/history/motion 测试。
- 修改 `LocalSpeedSmoother.cs`：增加独立的 `SmoothedVelocity` 向量 EMA，不改变 `SmoothedSpeed` 的瞬时速度大小 EMA 语义。
- 修改 `PlayerVisibilityPlan.cs`：将构造函数调整为 internal，供同程序集适配层和真实测试构造纯 plan 数据。
- 修改 `ObjectCuller.cs`：在 legacy target 构造后、legacy reconcile 前执行隔离的 shadow evaluation，并补齐 reset 入口。
- 修改 `CullingPerformanceTrace.cs`：refresh trace 增加可选 `PlayerVisibilityShadowTrace`；普通 tick 保持默认值。
- 修改 `Plugin.cs`：增加 2 秒冷却的 `[StableTopB Shadow]` 结构化摘要日志和 slow trace 中的独立 shadow 计时。

### 数据流与正式边界

```text
PlayerKeepPlan
→ PlayerVisibilityPlan
→ PlayerVisibilityLegacyTargetBuilder
→ PlayerVisibilityShadowController.Evaluate（只读 shadow）
→ PlayerVisibilityReconciler（输入仍是同一个 legacy target）
→ preview / TickVisibility（仍读取 legacy target）
```

- shadow 只接收 `Competitive` 条目；`SourceIndex` 是 plan entry 索引，选择后映射回完整 `PlayerObjectIdentity`。
- 限制人数时预算为配置限制的 `[1,100]` Clamp；不限制时预算等于 Competitive candidate 数量。
- 缺失位置的 Competitive 条目仍进入 selector，并使用非有限相对位置使软分确定为 0。
- shadow result 从未传给 reconciler、preview、fade、VFX、ShowTransitionBudget、swap 或 RenderFlags。

### identity、history 与 reset

- history 使用包含地址、GameObjectId、EntityId 的完整 `PlayerObjectIdentity`，地址不参与 selector 排序，只用于运动与历史连续性。
- reset 后未播种；第一次可执行 evaluation 仅从 legacy 中 `Competitive && DesiredVisible` 的完整身份播种一次。
- 首次 selector 成功后，后续 `WasPreviouslySelected` 只来自上一轮 shadow selected set，不再由 legacy 覆盖。
- 成功完成 selection、映射和统计后才原子替换 history；Unavailable/Failed 不提交新 history。
- manager 不可用、culling 关闭、duty/低人数 suspend、玩家未加载、Reset/Clear、dispose 和显式 resetRuleState 均清除 shadow history 与全部运动状态。

### 运动向量

- 本地标量速度继续对瞬时速度大小做 EMA；本地向量速度独立对瞬时速度向量做同 alpha 的 EMA，转向时不以向量长度替代标量值。
- 其他玩家 tracker 以完整 identity 为 key，首样本只建基线；可信连续样本更新向量 EMA；非有限/非正 dt 不污染；瞬移重建基线并使估计失效；每轮清理已离开 Competitive 集合的身份。
- 相对位置为 `OtherPosition - LocalPosition`；相对速度严格为有效的 other velocity（否则零）减有效的 local velocity（否则零）。
- 本地或其他玩家 tracker 暂时复用 `0.35s` half-life 与 `50.0` 最大可信速度，作为 Phase 3 内部初值，不增加配置 UI。

### trace 与 churn

- 状态明确区分 `Ready`、`Warmup`、`Unavailable`、`Failed`；本地速度未形成有效估计时仍执行 selector，但标记 Warmup。
- trace 包含 generation、candidate/budget/selected/previous、Retained/Entered/Left/MissingPrevious/ActiveReplaced、legacy only/shadow only/symmetric difference、本地速度状态、tracked count、motion/bonus、三份 8 档 histogram，以及 snapshot/selector/total ticks。
- churn 使用上一轮完整 shadow set、本轮完整 shadow set和当前完整 Competitive set计算，不依赖 selector 的当前候选 retained 数。
- histogram 每轮复制为独立只读数组，不引用复用工作区。

### 日志

- 每 2000ms 最多输出一条 `[StableTopB Shadow]`；Warmup、Unavailable、Failed 均在 `status` 中明确显示，Failed 使用 error 级别并受相同冷却限制。
- 日志不包含姓名、地址或对象 ID。格式示例（字段结构来自实际 formatter，数值为说明性示例，不是实机采样）：

```text
[StableTopB Shadow] status=Warmup generation=42 candidates=18 budget=10 selected=10 previous=10 retained=9 entered=1 left=1 missing=0 replaced=1 legacySelected=10 legacyOnly=2 shadowOnly=2 diff=4 localVelocity=False tracked=18 speed=0.000 motion=0.000 bonus=500 snapshotMs=0.080 selectorMs=0.030 totalMs=0.140 candidateRanks=[1,2,3,4,3,2,2,1] shadowRanks=[1,2,3,3,1,0,0,0] legacyRanks=[1,2,3,2,2,0,0,0] reason=n/a
```

### 异常隔离

- controller 内部捕获 snapshot/selector/mapping/statistics 异常并返回 Failed trace；`ObjectCuller` 另以仅包围本地位置读取和 shadow 调用的窄 catch 作为最后隔离层。
- Failed trace 保留异常类型与消息，不提交 history；下一步仍使用原 legacy target 调用 reconciler，并继续 preview、fade、VFX 和 visibility tick。

### 测试与验证

- 保留原 33 个测试，新增 21 个测试，总计 54 个。
- 新增覆盖：一次性 legacy seed、独立 shadow history、reset 后重播种、完整 identity 隔离、Left/Missing/ActiveReplaced、legacy symmetric difference、不可变 histogram、SourceIndex 映射、无限制预算、Warmup、Unavailable/Failed 不提交、完整 reset、输入只读、本地标量/向量 EMA、其他玩家速度方向/瞬移/identity 隔离、严格相对速度。
- `dotnet csharpier format .`：通过，61 个文件检查/格式化。
- `dotnet build EventHorizon.sln -c Debug`：通过，0 warning、0 error。
- `dotnet build EventHorizon.sln -c Release`：通过，0 warning、0 error。
- `dotnet test EventHorizon.sln -c Debug`：实际发现并通过 54/54。
- `dotnet test EventHorizon.sln -c Release`：实际发现并通过 54/54。
- 源码与产物扫描未发现 OR-Tools；未增加 worker、异步任务、命令、配置 UI 或正式切换开关；native detour 未修改。

### 尚待实机验证

- 尚未在本轮自动化环境中启动游戏确认约 2 秒日志节奏及真实 candidate/churn/耗时分布。
- 尚未基于实机 shadow 数据调整 motion threshold、EMA half-life、最大可信速度或 retention 参数；当前不得声称改善实际帧率或可见性稳定性。
- 本轮停在 Phase 3，不进入 Phase 4，不增加正式接管开关。
