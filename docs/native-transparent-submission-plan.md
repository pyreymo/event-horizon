# 原生透明提交逆向计划

> 2026-07-19 状态：原生 pass builder `ffxiv_dx11.exe+0x283320` 已证实可消费Underpaint-owned geometry、显式加载的不透明Material、non-skinned current/previous World CB、owned `g_InstanceParameter`和owned shader selection。source geometry/material/SHPK selection/角色实例常量及pass/view mask均已脱离，canonical renderer/subview scene keys与受限mask重建已实机通过；Context恢复已集中为单一scope，并已建立internal持续rigid实例与frame/view去重。当前构建用调用栈上的最小零值Model facade替换最后的source Model wrapper，等待实机验收。正式API的主要阻塞是通用view policy和可靠延迟回收边界。旧近似后端保持冻结且行为未改。完整证据见 [transparent-draw-correlation.md](transparent-draw-correlation.md)。

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

## 2026-07-19 路线修正后的当前状态

本计划最初以透明衣服作为探针寻找共同builder；它不是Underpaint正式后端的目标载体。主线现已纠正为“给Underpaint提供自有几何、材质和transform的原生不透明提交”。旧近似后端保持冻结，透明Stage A/C命名和通用command tracer均不再扩建。

当前已经实机确认：Underpaint自有Kernel VB/IB/VertexDeclaration在任意命中的兼容 `20/24` 原生现场重复调用 `ffxiv_dx11.exe+0x283320` 时，游戏会自动生成一条Opaque和五条辅助view draw，六条全部 `Count=3`。该路径不依赖目标角色、Slot、MaterialIndex或 `charactertransparency.shpk`，证明高层原生command展开入口成立。

尚未独立拥有的是该现场的material/per-instance输入。上一版把现场中全零的48-byte `g_InstancingMatrix` 当作transform；实测副本虽按预期变化，但没有形成有效world变换，因此该判断已经撤回。当前客户端静态分析确认真正的分支是：

```text
0x281DD0 skinned branch
  -> shader model-type scene key = skinned
  -> BoneList current/previous joint palettes

0x281DD0 non-skinned branch
  -> shader model-type scene key = non-skinned
  -> Context World slot = Model+0x38 transform object 的 128-byte CB
     -> current world/view matrix (64 bytes)
     -> previous world/view matrix (64 bytes; 首次等于 current)

0x283320
  -> 不再读取 Model、BoneList 或 transform object
  -> 从 copied shader selection 与当前线程 Context 快照生成各 pass command
```

因此 B 的最小验证边界就在 `0x283320`：完整复制 `OnRenderModelParams`、`OnRenderMaterialParams2`、shader selection及其key value数组，把副本的model-type key由skinned改为non-skinned，并仅在同步重复调用期间把Context的World槽替换为Underpaint自有128-byte constant。该constant的current和previous都设置为同一个向前5单位的矩阵；几何、World槽和所有Context借用状态都在 `finally` 中恢复。实现不修改共享Model、Material、角色、骨骼、palette或history，也不跨帧保存临时native指针。

下一次实机采集只需点击一次 `Arm custom native triangle`。日志应显示不同的原/副本shader-selection地址、同一个model-type key、donor为skinned value而副本为non-skinned value，以及独立的128-byte World CB；其前三行预期为单位矩阵并在第三行W包含 `5`。需要确认六条 `Count=3` 是否仍生成并统一采用新world输入。若没有生成command，结论也很明确：当前借用material没有non-skinned permutation，下一步应换独立不透明material，而不是再追角色transform。

World输入实测已经通过：六条有效 `DrawIndexed Count=3` 统一绑定插件VB/IB及同一128-byte VS constant，目标材质也确实存在non-skinned permutation。当前material PoC不再复制donor shader-selection作为最终选择器，而是调用原生selection构造/销毁函数，以Underpaint持有的SHPK创建独立key存储；只按CRC迁入当前view共有的scene key，随后强制non-skinned。游戏的material helper负责安装 `MaterialParameterCBuffer`、textures和sampler flags，Underpaint在调用后逐槽恢复。

首次arm仍从同步 `OnRenderMaterial -> 0x283320` 链取得一个有效 `Material*`，并给其resource handle增加自己的ref；这一步用于验证生命周期、selector重建和material binding，而不是最终API。日志新增source/owned Material、resource、SHPK、路径及 `MaterialCaptured`。第一次应为 `Captured=true`，同一插件生命周期再次arm应为 `false` 且owned身份/路径稳定。验证通过后，下一步将该已记录路径改由 `ResourceManager.GetResourceSync` 显式加载，届时可移除对source material和 `20/24` donor selector的要求。

第一次实机在material helper调用前暴露一个编号错误：SHPK constant slot不是Context运行时constant ID。修正版改为在source material已绑定完成的64个Context constant槽中按 `MaterialParameterCBuffer*` 反查真实ID，记录为 `ConstantId`，并以该ID保存/恢复。失败路径没有执行material helper或builder，也没有修改Context。

修正版两次实测已通过：日志中原缩写字段 `Captured=true/false`、owned resource/SHPK/path稳定、`ConstantId=25`，两次均生成六条有效 `Count=3`。日志标签现已改为 `MaterialCaptured`。根据resource handle确认的真实路径与键，当前版本进一步使用 `ResourceManager.GetResourceSync(Chara, 0x6D74726C, 0x56D3AB97, path)` 显式加载该不透明材质；第一次预期 `MaterialCaptured=false/MaterialLoaded=true`，之后为 `false/false`。验证通过后，剩余工作是让scene keys和material constant ID也脱离source material，再放宽source vertex ABI过滤。

显式加载实测已经通过，仍生成六条有效 `Count=3`。下一版已移除source Material关联hook和 `20/24` source ABI过滤：原生material helper调用后直接从64个Context constant槽识别目标CB所在ID，并整体恢复全部槽位；目标SHPK selector只按CRC吸收当前view共有scene keys，然后强制non-skinned。当前唯一仍借用的高层数据是 `0x283320` 调度现场的params/model wrapper及其中与view/pass相关的flags；geometry、material resource/keys/constants/textures、World current/previous均已独立。

无stride过滤的第一次实测仍命中 `20/24`，功能成功但对ABI独立性没有新增判别力。当前构建临时强制等待 `20/28` source现场；它正是早期borrowed-material版本调用builder却不生成command的对照条件。下一次只需arm一次，若日志为 `SourceStrides=20/28` 且仍出现六条有效 `Count=3`，即可删除临时过滤并把source geometry/material ABI视为已脱离。

`20/28` 对照已经通过：Underpaint自有 `20/24` geometry在该现场正常生成 `Count=3`，source geometry/material ABI由此闭环脱离。不过输出从六条变为九条，并出现Semitransparent及两条Semitransparent Stage C，暴露了最后一项source material污染：复制的 `OnRenderMaterialParams2+0x40`仍是source Material计算出的pass flags。

当前实现已删除临时stride过滤，并改为按原生调用规则重建material params：保留同步现场的model/resource与 `+0x38` geometry/view输入，清空可选callback输出和pass flags，再对显式加载的owned Material调用原生 `ModelRenderer.OnRenderMaterial`，由游戏生成owned `+0x40`，之后才调用material helper和 `0x283320`。期间的TLS rasterizer、constant和texture状态均恢复。日志新增source/owned flags及material index。下一次实测的判据是owned flags与source分离，且自有三角形不再继承source的Semitransparent命令族。

首次flags重建实测为 `0x15->0x15`，输出为当前测试material/view对应的一条Opaque与五条辅助draw，无Semitransparent；功能正确但仍缺source/owned不相同的判别样本。静态审计进一步确认 `Params2+0x38` low dword是上层pass/view request mask，`+0x3C/+0x3E`是source geometry index/alternate-builder dispatch，`+0x44`是辅助view bitmask。当前版本清掉后两项source geometry字段，暂时保留 `+0x38/+0x44`并明确记录；它们是下一阶段的source object/view边界，而不是材质协议。

source shader selection依赖已删除。owned selection现在直接按CRC查询canonical `ModelRenderer.SceneKeys[20]`与`SubViewKeys[5]`，然后强制non-skinned model type；目标SHPK构造器default覆盖尚未解析的camera keys。实机得到 `CanonicalKeys=6+0`，仍生成一条Opaque与五条辅助draw，故当前目标material的canonical key替换已经关闭。Context保存/恢复已集中为单一scope。Underpaint同时增加internal持续rigid实例和 `FrameCounter+Context+view+subview` rendezvous去重，实例持有独立current/previous World CB并支持history reset；该能力仍受固定ABI、测试material、剩余Model wrapper以及 `+0x38/+0x44`限制，尚未公开。

`OnRenderModelParams+0x10` 的真实身份也已定位。角色回调 `0x433270` 把该指针绑定到CharacterUtility登记的Context constant ID 34；目标SHPK的constant表把ID 34映射为CRC `0x20A30B34`、size 11，即176-byte `g_InstanceParameter`。现场回调对象的同一指针也等于 `CharacterBase+0x270 CharacterDataCBuffer`。Human自己的 `CustomizeParameterCBuffer` 位于 `+0xBF0`，由另一虚函数单独绑定，因此二者不能再统称为object constant。`g_InstanceParameter`是颜色、环境、角色灯光、wetness、wind/previous wind、眼部及头部等per-instance语义，不是transform或frame/view公共输入。

当前构建不再复制carrier的176 bytes。它从目标ShaderPackage按CRC查询canonical runtime constant ID并校验11个`float4`，创建Underpaint-owned neutral `g_InstanceParameter`，显式初始化白色乘色/环境/角色灯光、无wetness的范围参数和默认head-up，其余扩展字段清零。在调用 `0x283320` 前，该constant同时写入复制的model params并安装到当前Context ID；统一Context scope会无条件恢复原值。下一次采集的验收点是日志 `InstanceCB[34/CRC=0x20A30B34]=Source=.../Owned=...` 两者资源与hash不同，所有有效 `Count=3` command绑定owned constant，且Opaque/辅助draw集合不退化。

owned instance constant实机验收通过：source/owned ConstantBuffer、backing storage和hash均不同，owned前三个`float4`为显式neutral值；六条完整owned indexed draw仍是一条Opaque加五条辅助draw，无异常或透明family。176-byte carrier实例输入据此关闭。

同次样本的source pass mask从上一轮 `0x01C00000` 变化为 `0x03C00000`，但最终command集合完全相同。IDA确认 `+0x38 & 0x03000000`只是主提交gate，`& 0x00C00000`是View 32+辅助提交gate，低10 bits按五组bit-pair请求当前第一版不支持的额外环境/view family；`+0x44`仅作为View 32+i枚举mask，builder还会检查对应SubView camera。当前构建不再复制二者，而是为internal第一版明确请求已验证的主gate、辅助gate和Views 32/33。日志改为 `PassMask=source->owned`、`AuxViews=source->owned`；这些值是受限实现策略，不是公开API或通用原生协议。

mask重建实机已经通过：`0x01C00000->0x01C00000`与`3->3`仍生成一条Opaque和五条辅助owned draw，故该source依赖关闭。最后的wrapper审计确认 `OnRenderMaterial(0x281540)`只需要Model `+0x28/+0x50`和model params `+0x18`，`0x283320`只额外需要Model `+0x40/+0x178`。当前构建不再浅复制source model params，而是创建调用栈内的零值最小Model facade与从零初始化的0x20-byte model params，仅安装owned `g_InstanceParameter`。它不伪造完整Model、不调用角色callback、不接触Skeleton，也不修改任何共享对象。下一次实机若仍得到目标opaque flags与相同六条完整owned draw，source object/model wrapper即可正式关闭。
