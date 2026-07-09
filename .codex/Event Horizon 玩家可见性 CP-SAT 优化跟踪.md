# Event Horizon 玩家可见性 CP-SAT 优化跟踪

状态：设计阶段，尚未实现
创建日期：2026-07-09

## 1. 目标

当前玩家显示机制依赖瞬时 top-N 结果，再由 show 节流器控制恢复速度。移动穿过人群时，距离、视口和预算边界不断变化，导致目标集合频繁抖动，而 hide 与 show 的执行速度又不对称，最终表现为人物短暂出现、立即隐藏，以及停下后陆续恢复。

新模型不再对瞬时隐藏意图做节流，而是直接求解一个稳定的目标可见集合。每轮同时评估当前状态和未来若干预测点，在效用接近最优的前提下尽量减少集合切换。求解器只输出目标状态，RenderFlags 由独立执行器负责。

## 2. 重构边界

本次重构的核心目标只有一个：

```text
稳定、可预测、低抖动的玩家目标可见集合。
```

所有非核心行为都服从这个目标。如果某个现有功能与新架构的线程职责、数据所有权或性能边界纠缠不清，可以整块舍弃，而不是为了兼容它继续扩大新架构复杂度。

可直接舍弃的范围包括但不限于：

```text
淡入淡出控制或相关设置
整个 preview panel / floating preview window
世界箭头
玩家预览高亮
隐藏玩家脚下 VFX
自定义 targeting-me nameplate / VFX 标记
针对这些功能的性能 trace
其他依赖 Framework 线程频繁扫描、投影、VFX 创建或 UI 刷新的辅助功能
```

舍弃原则：

- 如果功能需要 worker 访问游戏对象指针、Dalamud service、GameGui、NamePlate、VFX 或 ImGui 状态，舍弃。
- 如果功能要求 Framework 线程为非核心显示效果做额外高频扫描、投影、构建快照或 native 调用，舍弃。
- 如果功能要求目标集合、RenderFlags 应用状态、淡入淡出状态或 preview 状态互相反查，舍弃。
- 如果保留功能会迫使 CP-SAT 输入模型引入额外分支、兼容状态或异步回补队列，舍弃。

舍弃后只需要在本文档和后续 changelog/README 中说明该功能因玩家可见性架构重做被移除。第一版不做兼容 shim，不保留隐藏开关，不迁移废弃配置字段。

## 3. 基本模型

第一版固定参数：

```text
HorizonSteps = 8
PredictionStep = 200 ms
SolveInterval = 200 ms
Gamma = 0.85
Epsilon = 0.03
UtilityScale = 10000
```

对每个参与预算竞争的玩家 (i)，预测未来 (H) 步的可见效用：

$
u_{i,k}\in[0,1]
$

效用由规则优先级、距离和其他软保留规则合成。距离、位置和速度只用于预测未来效用，不直接作为求解变量。所有规则判断和对象身份读取必须在游戏线程快照阶段完成；worker 只能处理快照中的纯托管值。

第一版不使用视口效用，也不做未来视口预测。`WorldToScreen` / 相机状态属于游戏线程侧数据；如果只传当前是否在视口，worker 无法可靠判断未来位置是否仍在视口。为了避免把线程边界写虚，视口优先级先整块移除。后续如需恢复，必须设计成游戏线程生成的纯数据输入。

量化后的整数效用为：

$$
q_{i,k} =
\operatorname{round}
\left(
\text{UtilityScale}\cdot\gamma^k\cdot u_{i,k}
\right)
$$

决策变量：

$$
x_{i,k}\in{0,1}
$$

表示预测第 (k) 步是否显示玩家 (i)。

每一步满足预算约束。第一版只处理单位预算成本：

$$
\sum_i x_{i,k}\le B
$$

本轮实际只采用：

$$
x_{\cdot,0}
$$

其余预测结果只用于评估当前选择是否会引发后续反复切换。

## 4. 玩家分类

规则层先将玩家分为四类。

```text
BypassVisible
    最终胜出规则的预算策略为 Exempt，必须显示且不占预算。
    目标、焦点、队友、好友只是默认可能属于此类，不能写死。
    不进入求解器。

Competitive
    最终胜出规则的预算策略为 Counted。
    占用玩家预算，由求解器决定是否显示。

ForceHidden
    明确必须隐藏。
    不进入求解器，但会产生目标隐藏状态。

Unmanaged
    对象无效，或不属于当前玩家隐藏系统处理范围。
    不进入求解器。
    不产生任何 RenderFlags 操作。
```

硬规则冲突在分类层解决。求解器只处理 `Competitive` 玩家之间的预算竞争。

分类必须尊重当前配置里的每条 keep rule 预算策略。不得因为某条规则名看起来像“保护规则”就绕过 `PlayerKeepBudgetPolicy`。如果用户把目标/焦点、好友等规则改成占预算，它们就应进入 `Competitive`。

`ForceHidden` 与 `Unmanaged` 不能合并。前者代表本系统明确要隐藏，执行器需要写入隐藏 flags；后者代表本系统不应处理，执行器既不隐藏也不恢复，避免破坏其他系统或游戏自身状态。

## 5. 优化目标

先直接计算不考虑切换时的理论最大效用：

$$
J^*=
\sum_{k=0}^{H-1}
\operatorname{TopBSum}
{q_{i,k}}
$$

这个直接计算只在第一版约束下成立：

```text
Competitive 玩家全部 c_i = 1
Competitive 内没有额外逐玩家上下界
每个 k 的预算相同
```

如果后续引入非单位成本、逐玩家硬上下界或每步不同预算，`J*` 必须改回用阶段一 CP-SAT 模型求解，不能继续用 `TopBSum`。

允许牺牲最多 3% 的预测效用：

$$
J(X)\ge J_{\text{threshold}}
$$

其中：

$$
J(X)=\sum_{i,k}q_{i,k}x_{i,k}
$$

$$
J_{\text{threshold}} =
\left\lceil0.97J^*\right\rceil
$$

定义切换数量：

$$
D(X) =
\sum_i|x_{i,0}-x_i^{\text{prev}}|
+
\sum_{k=1}^{H-1}\sum_i
|x_{i,k}-x_{i,k-1}|
$$

其中 $x_i^{\text{prev}}$ 是上一轮已经采用的目标状态，不是当前 RenderFlags。

使用一次 CP-SAT 求解：

$$
\min_X\quad M D(X)-J(X)
$$

并显式加入近似最优约束：

$$
J(X)\ge J_{\text{threshold}}
$$

其中：

$$
M=J^*-J_{\text{threshold}}+1
$$

这样可以精确表达：

1. 首先最小化切换数量；
2. 在切换数量相同时，最大化预测效用。

`J(X) >= J_threshold` 不能省略。`M` 的作用只是在近似最优可行域内表达词典序目标；如果不加这个约束，求解器可以选择切换更少但效用低于 97% 的方案。

因此每轮只需要一次 `CpSolver.Solve`。

## 6. 性能与线程模型

CP-SAT 只使用 CPU，不使用 GPU。主要风险不是平均 CPU 占用，而是在 Framework 线程同步求解造成帧时间尖峰。

第一版固定：

```text
NumWorkers = 1
MaxSolveTime = 2 ms
```

`2ms` 是实验初值，不是硬性结论。因为求解发生在后台线程，如果验收时 `OPTIMAL` 比例过低，应优先评估把上限提高到 `3-4ms`，避免大量 fallback 让目标集合长期不更新。

不得使用 CP-SAT 默认的自动多线程搜索，也不得在 Framework 线程调用 `Solve`。

运行结构：

```text
游戏线程（Framework.Update）
    收集不可变玩家快照
    完成所有 GameObject 指针读取
    完成所有 Dalamud service 读取
    完成规则分类
    提交最新快照
    接收已完成结果
    验证身份和时效
    更新目标状态
    立即应用 RenderFlags

专用单线程 worker
    只读取不可变托管快照
    构造预测
    计算 J*
    构造 CP-SAT 模型
    求解
    发布结果
```

worker 不得访问游戏对象指针或修改游戏状态。

worker 也不得访问：

```text
IObjectTable
ITargetManager
IGameGui / WorldToScreen
INamePlateGui
IFramework
ImGui 状态
任何 Atk / FFCS 指针
任何 VFX create/remove API
```

任意时刻最多存在一个正在执行的求解和一个等待处理的最新快照。新快照覆盖旧的待处理快照，不形成任务队列，采用 `latest-wins` 语义。

结果需要携带 generation 和时间戳。超过 500ms、全局状态已变化或身份无法重新验证的结果不得采用。

只有 `OPTIMAL` 结果可以更新目标集合。出现 `FEASIBLE`、超时、异常或 `INFEASIBLE` 时，保留上一轮稳定目标，并重新应用当前 `BypassVisible`、`ForceHidden` 和 `Unmanaged` 分类。

fallback 时：

- 当前 `BypassVisible` 立即恢复为可见目标。
- 当前 `ForceHidden` 立即写入隐藏目标。
- 当前 `Unmanaged` 从目标集合中移除，但不执行隐藏或恢复操作。
- 当前仍有效的 `Competitive` 尽量沿用上一轮目标状态；如果预算缩小，按当前量化效用截断。

目标集合、RenderFlags 应用状态、目标执行器、HiddenObjectTracker 均由游戏线程拥有。worker 只能发布 immutable result；采纳结果和任何状态 mutation 必须回到游戏线程。

当前代码里 `Tick` 同时可能来自 `Framework.Update` 和 `UpdateObjectArrays` detour 后。重构时必须统一这两条路径的所有权：

- native detour 路径不得构造快照、不得提交求解、不得等待 worker。
- native detour 路径不得推进执行器，不得修改目标集合，不得写 RenderFlags。
- native detour 只允许设置一个 dirty 标记，提示下一次 `Framework.Update` 做收敛。
- 目标状态和 RenderFlags 操作统一由 `Framework.Update` 拥有。

## 7. 运行时分层

```text
规则分类
    -> 构造不可变快照
    -> 后台预测与求解
    -> 保存目标集合
    -> 立即应用 RenderFlags
```

`HiddenObjectTracker` 只负责插件拥有的 RenderFlags 修改和安全恢复，不参与目标选择。

第一版移除 fade。执行器只做立即应用：

```text
TargetVisible = true
    立即恢复本插件添加的隐藏 flags。

TargetVisible = false
    立即写入隐藏 flags。

Unmanaged
    不写入，不恢复。
```

旧 `ShowTransitionBudget`、`Swap` 和 fade 状态全部移除。先验证 CP-SAT 目标集合本身是否稳定；如果仍有闪烁，优先定位优化器、异步结果采纳或分类输入，而不是让 fade 反向掩盖问题。目标集合稳定后，fade 可以作为纯执行层功能重新加入。

preview 不是核心行为。若保留，必须改为读取目标集合和分类快照；如果需要继续维护旧 `latestPlayerVisibilityPlan`、额外 fast refresh、世界箭头或 UI 选择反查，则直接移除整个 preview panel / floating preview window。

## 8. 调试指标

重点记录：

```text
CompetitiveCount
BypassVisibleCount
ForceHiddenCount
UnmanagedCount
VariableCount
ConstraintCount

SnapshotBuildMs
PredictionMs
ModelBuildMs
SolveMs
ResultAgeMs

SolveStatus
AcceptedResult
FallbackReason

JStar
JThreshold
FinalJ
FinalD
CurrentStepSwitchCount

PendingSnapshotReplacedCount
StaleResultDiscardCount
```

需要分别观察 Framework 快照耗时、worker 总耗时、求解成功率和结果年龄，不能只看 `CpSolver.WallTime()`。

性能统计不是核心行为。保留的 trace 只能服务于本架构验证；如果统计本身需要高频额外扫描或复杂跨线程状态，也可以删减到最小日志，甚至整块移除 UI 展示。

初始性能目标：

```text
Framework snapshot build < 0.2 ms
大部分 worker 求解 < 2 ms
结果通常在 300 ms 内采用
结果不得超过 500 ms
不产生求解任务积压
```

## 9. 实施顺序

### Phase 0：OR-Tools 验证

先用最小 Dalamud 插件验证：

* `Google.OrTools.dll`
* `Google.Protobuf.dll`
* `google-ortools-native.dll`

能够正确打包和加载，并测试单线程后台求解、插件卸载和首次求解开销。

### Phase 1：目标状态分离

建立 `BypassVisible / Competitive / ForceHidden / Unmanaged` 分类和独立的目标集合状态。暂时仍可使用旧排序生成目标集合，用于验证目标状态与执行状态已经解耦。

同时执行非核心功能裁剪审计：凡是阻碍目标状态分离或线程所有权收敛的 preview、VFX、世界箭头、淡入淡出配置、性能 UI，可在该阶段直接删除。

### Phase 2：快照、预测和 worker

建立不可变快照、generation、位置历史、速度预测、单 worker 和 latest-wins 提交机制。快照必须包含 worker 所需的全部纯托管数据，不允许 worker 回读游戏状态。

### Phase 3：CP-SAT 求解

实现 `JStar` 直接计算、`JThreshold`、切换变量和单次目标：

```text
minimize M * D - J
subject to J >= JThreshold
```

接入超时、状态验证和 fallback。

### Phase 4：执行器重构

建立无队列、立即应用的目标执行器。移除旧 `ShowTransitionBudget`、`Swap` 和 fade 状态。

### Phase 5：文档与用户说明

如果删除了非核心功能，更新本文档、README、changelog 和设置文案，明确说明它们被移除是为了保证玩家可见性核心架构的线程边界和稳定性。

## 10. 验收重点

* 直线穿过人群时，不再明显出现“显示一下又立即隐藏”。
* 停止移动后，不再长时间陆续恢复玩家。
* 低预算下，边界玩家切换次数明显低于旧模型。
* 每条 keep rule 的 `Counted/Exempt` 预算策略保持有效；目标、焦点、队友和好友等默认不占预算语义保持不变。
* Framework 线程不调用或等待求解器。
* worker 不访问任何游戏指针、Dalamud service、GameGui、ImGui、NamePlate 或 VFX API。
* native detour 只设置 dirty 标记，不修改目标状态或 RenderFlags。
* solver 非 `OPTIMAL` 时不采用部分结果。
* `ForceHidden` 会隐藏，`Unmanaged` 完全不碰，两者不可混淆。
* 第一版没有 fade；显示/隐藏稳定性只由目标集合和立即 RenderFlags 应用验证。
* 插件关闭、暂停或卸载时能够安全恢复所有修改。
* 若删除了 preview、VFX、世界箭头、淡入淡出控制或性能 UI，配置页、README 和 changelog 不再承诺这些功能存在。
