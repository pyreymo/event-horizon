# Event Horizon 玩家可见性稳定 Top-B 重构

## 1. 状态与背景

本计划取代：

* `.codex/Event Horizon 玩家可见性 CP-SAT 优化跟踪.md`

原 CP-SAT 实现停留在影子求解阶段，没有接管正式玩家目标集合。已有分类、目标集合分离、快照和运动采样基础可以复用；OR-Tools、后台求解器和多步整数规划不再继续开发。

不得继续实施原计划 Phase 3 及后续的 CP-SAT 结果通道。

## 2. 目标

实现一个确定性、同步、可证明全局最优的稳定 Top-B 选择器。

模型应实现：

1. 本地玩家静止时，规则 rank 严格主导软距离分和旧目标保留奖励。
2. 本地玩家明显移动时，仍然有效的上一轮目标集合应保持稳定。
3. 上一轮目标失效或消失时，自动从当前候选人中填补空位。
4. 选择结果确定，不依赖求解超时、后台任务完成顺序或 CP-SAT 状态。
5. 选择器不得读取或写入 Dalamud、FFXIVClientStructs、RenderFlags 或其他游戏对象。
6. 第一版不加入状态机、每轮替换上限、多步可见决策或其他组合约束。

## 3. 候选人分类

继续使用现有分类：

* `BypassVisible`：直接显示，不参与预算。
* `Competitive`：参与 Top-B 预算选择。
* `ForceHidden`：直接隐藏。
* `Unmanaged`：插件不管理。

每名 Competitive 玩家仍然只使用胜出规则的 rank。不要把多条命中规则相加。

规则预算策略继续先于选择模型生效。

## 4. 数学模型

设当前 Competitive 候选集合为 (C_t)，预算为：

[
B_t=\min(B,|C_t|).
]

每名玩家 (i) 具有固定规则 rank：

[
r_i\in{0,\dots,7}.
]

定义规则层级：

[
P_i=7-r_i.
]

软分必须归一化到：

[
z_i\in[0,1].
]

基础分：

[
q_i=A P_i+S z_i.
]

上一轮正式采用的目标集合为 (Y_{t-1})。定义：

[
y_i=
\begin{cases}
1,&i\in Y_{t-1}\cap C_t,\
0,&\text{否则}.
\end{cases}
]

本地平滑运动强度归一化为：

[
m_t\in[0,1].
]

保留奖励：

[
\lambda_t
=========

\lambda_{\mathrm{rest}}
+
(\lambda_{\mathrm{move}}-\lambda_{\mathrm{rest}})m_t.
]

最终排序分：

[
\widetilde q_i=q_i+\lambda_t y_i.
]

目标集合为调整分最高的 (B_t) 名玩家：

[
X_t=\operatorname{TopB}_{i\in C_t}(\widetilde q_i).
]

## 5. 参数不变量

必须在代码中集中定义并验证：

[
0\le z_i\le1.
]

静止状态下，任意高一档 rank 的玩家必须压过低一档旧玩家：

[
\lambda_{\mathrm{rest}}<A-S.
]

完全移动状态下，任何仍然有效的旧玩家都不能被主动替换：

[
\lambda_{\mathrm{move}}>7A+S.
]

第一版建议使用整数尺度，例如：

[
S=1000,\qquad A=3000.
]

满足上述约束的具体 retention 参数应集中配置，不得散落在排序代码中。

不要使用当前候选集合的 `maxRank` 动态归一化。

不要对规则分和软分相加后执行饱和 Clamp。

## 6. 软分

软分使用预测相对距离，但未来信息只汇总为当前标量，不创建未来可见决策变量。

相对位置：

[
a_i=p_i-p_L.
]

相对速度：

[
w_i=v_i-v_L.
]

预测距离：

[
d_{i,k}=|a_i+w_i k\Delta t|.
]

距离函数：

[
f(d)=\frac{1}{1+(d/\sigma)^2}.
]

折扣汇总：

[
z_i=
\frac{
\sum_{k=0}^{H-1}\gamma^k f(d_{i,k})
}{
\sum_{k=0}^{H-1}\gamma^k
}.
]

计算结果必须 Clamp 到 ([0,1])，该 Clamp 仅用于处理浮点误差，不得用于掩盖错误的分数范围。

速度样本不可用时，使用零速度，不得产生 NaN 或 Infinity。

## 7. 本地运动强度

本地速度需要平滑，不能直接使用单帧位置差。

使用 EMA 或等价的确定性平滑方式得到：

[
\bar v_t.
]

通过两个固定速度阈值将其映射为：

[
m_t\in[0,1].
]

阈值以下为 0，阈值以上为 1，中间使用线性插值或 smoothstep。

第一版不得增加 Moving、Settling、Stable 状态机。

## 8. 确定性排序

排序关键字依次为：

1. `AdjustedScore` 降序；
2. `WasPreviouslySelected` 降序；
3. `BaseScore` 降序；
4. 稳定身份键升序。

完全相同输入必须产生完全相同的输出。

不得使用对象地址作为首选稳定身份。需要复用当前项目中最可靠的玩家身份结构，并处理对象 ID 复用。

## 9. 执行架构

选择器实现为无副作用的同步纯函数：

```csharp
PlayerVisibilitySelectionResult Select(
    PlayerVisibilitySelectionSnapshot snapshot,
    PlayerVisibilitySelectionParameters parameters
);
```

选择器不得：

* 创建后台任务；
* 访问 Dalamud 服务；
* 访问游戏对象；
* 写 RenderFlags；
* 修改正式 target set；
* 记录日志。

Framework 线程负责：

1. 创建不可变 snapshot；
2. 调用同步选择器；
3. 验证 generation 和身份；
4. 更新 target set；
5. 调用 reconciler。

正式切换前，native detour 必须收敛为 dirty signal，不得在 detour 中执行快照、选择、reconcile 或 RenderFlags 写入。

## 10. 第一版非目标

第一版明确不实现：

* CP-SAT 或其他通用求解器；
* 后台 worker；
* 多步 (x_{i,k}) 决策；
* 97% 效用阈值；
* big-(M)；
* 每轮最多替换 K 人；
* 多规则命中奖励；
* 玩家相关生成成本；
* Moving/Settling/Stable 状态机；
* 动态学习参数；
* fade、VFX 和 preview 的额外重构。

## 11. 迁移阶段

### Phase 0：冻结旧原型

* 在旧 CP-SAT 计划顶部标记为废止。
* 新建本计划与独立进展文件。
* 确认当前正式 target 仍来自 legacy builder。
* 停止提交新的 CP-SAT 功能。

### Phase 1：删除 CP-SAT 专属基础设施

* 删除 OR-Tools 与 native runtime 依赖。
* 删除 probe、隐藏命令、native loader。
* 删除 optimizer 和 solver worker。
* 删除 CP-SAT 专属统计。
* 保留 legacy 正式路径。
* 编译并确认行为不变。

### Phase 2：实现纯选择器

* 建立 selection snapshot、parameters、candidate 和 result 数据结构。
* 实现固定 rank 基础分。
* 实现相对运动软分。
* 实现本地速度平滑和 retention bonus。
* 实现确定性 Top-B。
* 添加不变量校验和单元测试。
* 尚不接管正式 target set。

### Phase 3：影子验证

每个刷新周期同时计算：

* legacy target；
* stable Top-B shadow target。

记录：

* candidate count；
* selected count；
* retained count；
* entered count；
* left count；
* motion factor；
* retention bonus；
* rank histogram；
* legacy/shadow symmetric difference；
* selector elapsed time。

不得写 RenderFlags 或替换正式 target set。

### Phase 4：正式切换

* 增加临时内部切换开关。
* 新选择器成为 target set 唯一来源。
* legacy builder 仅保留为可选调试对照。
* 将选择、reconcile 和 RenderFlags 写入统一迁移到 Framework 线程。
* native detour 只标记 dirty。

### Phase 5：清理

* 删除 legacy shadow 对照。
* 删除临时切换开关。
* 删除不再使用的 snapshot、worker 和统计结构。
* 更新 README 和架构说明。

## 12. 必须通过的测试

1. 候选人数不超过预算时全部选中。
2. 相同输入重复执行得到相同集合和顺序。
3. `lambda=0` 时等价于按基础分取 Top-B。
4. 静止参数下，高一档 rank 的最低分高于低一档旧玩家的最高分。
5. 完全移动参数下，所有仍有效的旧目标均被保留。
6. 旧目标消失时，只填补缺少的名额。
7. 随着 (\lambda) 增大，保留的旧目标数量单调不减。
8. 软分始终位于 ([0,1])。
9. 空集合、预算 0、NaN 速度、瞬移样本均有确定行为。
10. 不再生成或打包任何 OR-Tools 文件。

## 13. 实施约束

每完成一个 Phase：

* 更新独立进展文件；
* 记录实际修改文件；
* 运行 Debug 和 Release build；
* 运行格式化与测试；
* 说明下一阶段，不得提前混入后续阶段；
* 不得声称已改善实际帧率，除非有实机对照数据。
