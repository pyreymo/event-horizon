# 原生透明提交逆向计划

> 2026-07-19 状态：原生 builder 已证实能让插件自有 VB/IB 自动生成完整 Stage A、两族 Stage C 和辅助 view command。资源创建、生命周期、线程 Context 几何绑定与恢复现已迁入独立 `ffxiv-underpaint` 仓库。迁移后的实测仍稳定出现四条绑定插件 VB/IB 的 `Count=3` draw。入口随后下移到实际 pass builder `ffxiv_dx11.exe+0x283320`。第一次无目标实测暴露出一个必要约束：不能消费任意首个主视图调用；该调用的原 geometry 使用 `20/28` streams，而插件 geometry 是已验证的 `20/24` ABI，结果虽执行 `3/0/3` builder 调用却没有生成命令。现在 Underpaint 只消费下一次 `20/24` 兼容现场，并记录源 vertex declaration、streams 和 range；仍不要求目标角色、Slot、MaterialIndex 或指定 SHPK。该修正版待实机验收。旧近似后端保持冻结且行为未改。完整证据见 [transparent-draw-correlation.md](transparent-draw-correlation.md)。

## 文档目的

本文压缩记录 Underpaint 半透明路线在完成 Stage A/B/C 原型后的架构判断、后续逆向目标、第一阶段实施计划，以及需要人工配合的实机验证步骤。

它不是当前实现说明。当前实现仍见 [gbuffer-probe-findings.md](gbuffer-probe-findings.md)；本文描述的是停止扩展手写近似后端、转向原生透明提交协议的后续主线。

## 背景与已确认结论

对 `CharacterTransparency.shpk` 的定点分析和后续实机探针确认，原生半透明链路为：

```text
Stage A: 透明几何
         -> Semitransparent G0/G1/G2/G4 + depth

Stage B: deferred light volume
         -> LightDiffuse + LightSpecular

Stage C: 同一透明几何再次绘制
         + LightDiffuse / LightSpecular
         + 原始材质纹理和参数
         + 重新计算最终 alpha
         -> scene color 上的透明混合
```

Stage C 是原生材质体系的一部分，不是因为 Stage B 尚未分析完整而产生的临时补丁。当前实现中有明显补丁感的是 Underpaint 自己重画几何、手写近似光照公式的 Stage C，而不是 A/B/C 结构本身。

临时跳过 Stage A 的 A/B 实测表明，Stage A 不能删除：删除后，几何形状相关的表面、光照/阴影表现和时域稳定性不能正确更新。depth、各 G-buffer 分量和 stencil 在这些结果中分别承担的精确职责尚未完全拆分，不应把当前观察写成更精确的协议结论。

`.mtrl` 和运行时 `Material` 对研究具体材质仍有价值。它们可以把 SHPK 中“理论上可能选择的分支”收敛到某件具体装备实际使用的 node、shader keys、纹理、sampler 和材质常量。但它们主要解释 Stage A/C 的材质载荷，不能替代对 Stage B 或 CPU 提交协议的分析，也不能证明 Stage C 可以删除。

## 架构决策

### 冻结当前近似后端

现有 A/C 后端已经完成原型使命：

- 证明插件几何可以写入原生半透明 G-buffer 和 depth；
- 证明 Stage A 的存在会影响后续原生表现；
- 证明插件可以取得原生 light buffers；
- 证明 Stage C 必须重新提交几何才能产生插件自己的最终透明覆盖；
- 提供后续原生提交研究的 pass 定位、资源身份和矩阵基线。

后续只修会污染实验判断的问题，例如重复执行、矩阵错位、资源生命周期错误和错误的 pass 识别。不再投入：

- 完整 PBR 或 CharacterTransparency 材质复刻；
- 自定义透明排序器；
- 自定义 motion-vector pass；
- 自定义 TAA/history 补偿；
- 水、雾、折射等大量材质特例；
- 继续扩大手写 Stage C 材质系统。

临时 `Skip Stage A` A/B 开关在结论记录完成后删除，避免诊断选项继续累积。现有近似后端保留为实验对照，不再作为最终架构演进。

### 新的研究目标

目标不只是“找到一个 render item”或“找到 A/C 的共同调用函数”，而是：

> 找到原生绘制数据从 object/model/submesh/material 输入，经过分配、展开和入队，到 Stage A/C 消费阶段的完整生产与所有权协议。

理想情况下，插件只在 pass 展开前提交一次高层输入，由游戏生成后续派生状态：

```text
Geometry + Material + Object context + Transform
                     |
                     v
             Native submission/builder
                     |
        +------------+-------------+
        |                          |
        v                          v
   Stage A item/packet        Stage C item/packet
        |                          |
        + lighting / sorting / per-view state
```

如果只能分别伪造已经展开的 A/C packet，新实现只是把当前手工 A/C 从 GPU hook 移到 CPU packet 伪造，并没有解决架构问题。

## 逆向原则

### DrawIndexed 调用栈不是提交链路

实际实现很可能在更早的渲染准备阶段或另一线程生成临时数据，之后由通用 executor 消费。A/C 的 `DrawIndexed` 调用栈可能只在 executor 会合，完全不包含生产者。

应从数据来源向上追：

```text
native draw
    -> executor 当前消费的数据/参数
    -> 容器身份和遍历方式
    -> 本帧写入该数据的位置
    -> builder / enqueue / allocator
    -> model/submesh/material/object 输入
```

调用栈用于识别 executor、模块和线程边界，但不能单独证明生产协议。

### 不预设固定队列形状

`base/count/stride` 只是最容易识别的一种实现。实际容器也可能是指针数组、分块 arena、material bucket、链表、树、变长 packet 或每 pass 独立 command list。

第一阶段只要求确认 executor 当前消费的数据地址、容器或遍历上下文，以及它能追溯到的最高层来源。不存在稳定长期 item 时必须如实记录，不能为了填满预设结构而把临时参数误认为 render item。

### 不复制最终 packet

已经展开的 packet 可能固化或引用：

- world/previous-world constants；
- bounds、camera depth 和 sort key；
- bone palette 和 owner；
- pass flags 和 per-view 状态；
- 资源引用、临时 descriptor 和 frame-arena 指针；
- motion/history identity。

因此不能 `memcpy(packet)` 后只改 transform。若后续找到 builder，首次调用也必须发生在原生调用的同一线程、同一 frame/build context 和同一隐式状态范围内，由 builder 重新生成所有派生数据。

## 第一阶段：只读 TransparentDrawCorrelation

### 目标

对一件简单的原生透明装备建立只读 draw-correlation tracer：关联 Stage A/C draw，记录两边 executor 的消费上下文，并确定两者能够追溯到的共同 object、model、submesh 和 material 身份。

本阶段不写入或复制任何队列数据，不调用候选 builder。

### 当前可复用基础

- Underpaint 已 hook `Draw`、`DrawIndexed`、`DrawInstanced` 和 `DrawIndexedInstanced`，并能识别半透明 G-buffer 区段及后续单 RTV Stage C 区段。
- EventHorizon 已有 DEBUG-only 文件日志，记录线程 ID 并按日滚动。
- 现有 `CharacterBase.OnRenderModel` hook 可作为对象进入 render scheduling 的上界。
- FFCS 已公开 `CharacterBase.Models/Materials`、`Model.SlotIndex/Materials`、`Material.MaterialResourceHandle`、shader keys、材质 constant buffer 和 texture entries。
- `CharacterBase.OnRenderMaterial` / `ModelRenderer.OnRenderMaterial` 参数包含 Model、Material 和 material index，是第一轮建立高层材质身份的重要观察点。
- 旧的半透明 donor 点选、readback 和 correlation probe 已删除，需要新建受控的一次性 tracer，但不需要新建渲染后端。

### 捕获模型

捕获由当前目标和手动 arm 启动。每次会话只运行有限帧并设置 draw/event 硬上限，正常游戏期间不持续记录。

会话开始时，在安全的 framework/game-thread 时点生成不可变 donor 快照：

```text
Object identity
CharacterBase*
Model* / slot
ModelResourceHandle*
Material* / material index
MaterialResourceHandle*
ShaderFlags / ShaderKeyValues
Material texture resource identities
```

目标相关的 `OnRenderModel` 和 `OnRenderMaterial` 事件记录单调序号、线程、Model/Material 身份和可取得的参数。D3D draw hook 分别在 Stage A/C 记录：

```text
draw type and arguments
VS / PS / input layout
VB / stride
IB / format
constant-buffer pointers and bounded hashes
SRV and underlying resource identities
thread / monotonic sequence
bounded native stack as module + RVA
```

所有指针型资源必须在捕获时转成不可变值或在读取期间正确保留引用，不能把 frame-owned 指针留给后续异步解引用。

### 关联证据

先用 Material/TextureResourceHandle 与实际绑定资源的交集过滤，再按以下证据组合为 A/C 候选评分：

- Material 和 MaterialResourceHandle 身份；
- model、slot、material index；
- VB/IB、stride/format 和 draw range；
- object/world constant-buffer 来源或内容 hash；
- shader identity、shader keys 和材质 SRV；
- callback、draw 的线程和相对序列。

共享 VB/IB 只能作为弱证据，因为不同对象和装备实例可能共享模型资源。任何唯一配对都不能仅依赖 index count 或 buffer 地址。

输出结构采用可选字段，不假设一定存在稳定 item 或 queue：

```text
TransparentDrawCorrelation
|- Object / Model / SubMesh / Material provenance
|- Stage A
|  |- executor stack
|  |- consumed source address, if observable
|  |- container/traversal context, if observable
|  `- D3D draw identity and bound resources
`- Stage C
   |- executor stack
   |- consumed source address, if observable
   |- container/traversal context, if observable
   `- D3D draw identity and bound resources
```

结论必须分为 `已确认 / 强烈怀疑 / 尚未确认`。

### 代码组织和控制面

- tracer 仅在 DEBUG 构建中存在，不进入正式配置或生产 API。
- D3D 层的一次性快照通过 `UnderpaintDiagnostics` 暴露给 EventHorizon；目标和材质身份由 EventHorizon 侧 tracer 管理。
- 日志使用现有 debug 文件，source 固定为 `TransparentDrawCorrelation`。
- Underpaint Demo 提供 `Arm`、`Cancel`、状态和当前 donor 摘要，不落配置。
- 捕获完成后自动停止；异常、超限和目标失效也必须安全停止并留下单条原因。

## 工作分工

### 可由 Codex 独立完成

1. 用 FFCS 源码和 MCP 补齐现有公开结构、callback 参数及资源身份映射。
2. 删除临时 Stage A A/B 开关。
3. 实现 DEBUG-only tracer、一次性 D3D 快照、目标快照、日志和 UI 控制。
4. 实现纯数据 matcher 测试，包括：
   - 共享 geometry、不同 material/object；
   - A/C draw 顺序变化；
   - 缺失可选字段；
   - 多个相似候选且无法唯一判定；
   - donor 失效和捕获超限。
5. 运行 CSharpier、Debug/Release 构建和现有测试。
6. 分析实机日志；FFCS 足够时直接收敛，FFCS 不足时再使用 IDA 定位 executor、遍历结构和写入者。
7. 把结果整理为独立、可交接的 `docs/transparent-draw-correlation.md`。

### 需要人工提前准备

- 一名可被当前目标选中的 PC，推荐自己的角色。
- 只保留一件容易识别的 `CharacterTransparency` 透明装备，并记录物品名和装备槽。
- donor 暂时关闭 Penumbra 模型/材质替换，使用原生资源。
- 选择安静的普通场景，避免水体、雨淋、湿身、Gpose、持续动作和其他透明角色遮挡。
- 准备两种仅相差 donor 装备的状态：装备可见，以及移除/替换该装备。
- 接收 Debug DLL 后由人工部署并重载插件。

当前没有连接的 IDA 数据库。第一轮不需要提前准备；只有 FFCS 和运行时日志不足时，才需要人工打开当前客户端 `ffxiv_dx11.exe` 对应 IDB 并连接 idalib-mcp。

### 需要人工实机验证

第一轮：

1. 固定镜头和角色姿态，选中 donor。
2. 打开 Underpaint Demo，确认 donor 摘要正确。
3. 装备可见时 arm 一次，等待 capture complete。
4. 只移除或替换该装备，在相同镜头再次捕获。
5. 提供两次日志、装备槽和物品名。
6. 确认捕获期间是否出现长卡、崩溃或持续日志；不要求人工解释 draw。

第二轮仅在第一轮日志被分析并生成 focused tracer 后进行：

- 恢复 donor，固定条件重复捕获两至三次，验证关联稳定；
- 按单变量步骤转动镜头或更换 donor，确认候选随目标变化；
- 若 executor 已定位，仍只验证读取结果，不进行 packet 写入。

## 验收标准和下一步决策

第一阶段完成必须满足：

- 同一 donor 的 A/C draw 可以重复关联，且证据不依赖单一 VB/IB；
- 至少取得共同 Material、draw range、geometry identity 和 object/world constants 来源；
- 记录两边 executor、线程、遍历上下文及可观察到的最高层消费来源；
- donor-off 对照会移除或改变对应候选；
- 捕获有界，非捕获期间无持续成本；
- 全程没有队列写入、packet 复制或 builder 调用。

根据结果只选择一个后续分支：

```text
同一 item 被 A/C 消费
    -> 追该 item 的 producer

A/C 是两份 pass-specific packet
    -> 分别追 producer，寻找共同 builder

没有稳定 packet
    -> 上移到 ModelRenderer / SubMesh / Material 展开层

只能分别伪造 A/C packet
或需要跨帧持有 frame-arena 指针
或必须从错误线程调用 builder
    -> 停止 native submission 实施，不把危险偶然行为产品化
```

若最终只能通过原生 `DrawObject/Model/SubMesh` 接入，应优先接受受限的原生后端，而不是为支持任意插件 VB/IB 再次下沉到 packet 伪造。

## 后续原生提交 PoC 的约束

本节不是第一阶段工作，只记录后续成功条件。

找到候选 builder 后，不能从任意 Dalamud callback 直接调用。第一轮 PoC 必须 hook 原生 builder，在原调用的同一线程、同一 frame/build context、同一隐式状态范围内使用相同输入再调用一次。

首次重复提交不修改共享 Material，也不立即偏移 transform，只通过 builder/packet/draw 数量和 GPU capture 证明二次提交。确认 builder 会重算 bounds、sort key、object constants、per-view 和 history 派生状态后，才传入偏移 transform。

只有当同一次高层提交能让游戏自动生成完整 Stage A/C，才进入后续顺序：

```text
原生 geometry + 原生 material + 原生 transform
原生 geometry + 原生 material + 自定义 transform
自定义 geometry + 原生 material
最小原生 Model/SubMesh 载体
最后才研究独立 material / texture parameters
```

透明排序、motion vector、TAA、水体、雾和阴影投射都必须逐项实测。进入原生 submission 会提高继承正确行为的概率，但不保证具体透明材质原本就参加所有 pass。
