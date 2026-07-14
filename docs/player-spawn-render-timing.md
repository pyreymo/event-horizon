# 玩家首帧闪烁与模型停止提交：调查交接

更新时间：2026-07-14  
涉及版本：EventHorizon 0.3.2.7、Penumbra 1.6.1.10

## 交接摘要

本次调查开始于两个现象：

- 新玩家出现时，EventHorizon 的 admission gate 仍不能稳定消除首帧闪烁。
- 少数玩家随后会完全没有身体模型，或者只剩装备、武器、时尚配饰；Penumbra “重绘全部”可以恢复。

当前结论分为三个等级：

### 已确认

1. 旧 admission gate 在新玩家已有 `GameObject`、但 `DrawObject` 仍为 null 的窗口写入 `GameObject.RenderFlags |= 0x1802`。
2. 该写入发生在 `UpdateObjectArrays` 返回后，并可早于 `SetDrawObject` 10–30ms。
3. 坏对象后来有非空 Human、Skeleton、Models 和有效 render callback 地址，但 1000ms 内一次 ModelRenderer callback 都没有。
4. 因此故障位于 Human/CharacterBase 建立或更新到场景选择之间；Penumbra 的材质 hook 位于其下游，不是“完全没有 callback”的直接发生点。
5. Penumbra 的 redraw 不重建 DrawObject。它分两个 Framework 更新置位再清除 `RenderFlags.Model (0x2)`，由一次原生显隐边沿恢复对象。
6. 禁用已经运行中的 Penumbra 或 Customize+ 不能恢复坏对象；禁用 admission gate 后，现场暂时没有再次出现坏对象。

### 强烈怀疑，但尚未直接证明

旧 gate 在 Human 建立期写入复合 RenderFlags，使游戏错过或延后了一次必要的 Human 更新。Penumbra 同时在 `CreateCharacterBase` 和 `Human.UpdateRender` 周围安装模型资源、装备元数据上下文，因此会改变这段生命周期的执行环境和时序。

### 尚未确认

- `0x1802` 具体触发了游戏中的哪一个 native 分支。
- 坏对象生成时，`Human.UpdateRender` 是没有调用、调用次数不足，还是调用时已经带着不完整状态。
- Penumbra 的 collection/meta 解析是否放大了问题，还是只改变时序使 EventHorizon 的错误更容易暴露。

当前代码已经删除 admission gate 及其后续补丁，只保留 `UpdateObjectArraysHook` 的拓扑变化通知。首帧闪烁问题尚未解决，但不再用会破坏 DrawObject 生命周期的方式处理。

## 已确认的生命周期

### 对象表不是 DrawObject 生命周期

IDA 中的 `GameObjectManager::UpdateObjectArrays` 负责从 character pool 重建：

- `IndexSorted`；
- GameObjectId 排序数组；
- EntityId 排序数组。

对 character pool，它把角色放入偶数槽，把关联对象放入相邻奇数槽。该函数没有创建 Human、没有调用 `SetDrawObject`，也没有进入 ModelRenderer。

FFCS 确认：

| 成员 | 位置 | 含义 |
|---|---:|---|
| `GameObject.DrawObject` | `GameObject + 0x100` | 当前连接的场景 DrawObject，可为 null |
| `GameObject.SetDrawObject` | vtable `+0x88` | 把已创建的 DrawObject/Human 连接到 GameObject |
| `GameObject.SetReadyToDraw` | vtable `+0x110` | GameObject 的绘制准备阶段 |

因此，“对象能从 Dalamud ObjectTable 查询到”不能推出“Human 已建立并完成第一次更新”。

### 现场确认的顺序

同一个玩家 `1006AB26` 的 trace：

```text
14:00:19.208  SetReadyToDraw before/after
                DrawObject = null

14:00:19.208  UpdateObjectArrays: appeared
                DrawObject = null

14:00:19.208  admission: hold published
                held = true

14:00:19.218  SetDrawObject: before
                DrawObject = null, held = true

14:00:19.218  SetDrawObject: after
                DrawObject = 0x1D01CB5FDB0, held = true
```

其他玩家也出现相同模式，`SetDrawObject` 比 admission hold 晚约 10–30ms。

这条证据只确认以下关系：

```text
GameObject 已进入对象表
  → EventHorizon 已写 admission 隐藏状态
  → Human 尚未通过 SetDrawObject 连接
```

`CreateCharacterBase` 相对 `UpdateObjectArrays` 的精确位置没有在同一次 trace 中采集，不能写成已确认的串行调用栈。

### DrawObject 非空以后发生了什么

完全不显示的 `1003F031` 最终状态包括：

- `RenderFlags = 0`；
- Human DrawObject 非空；
- `LoadState = 0x03`；
- Skeleton 非空；
- 14/18 model slot 非空；
- RenderModel/RenderMaterial callback 地址与正常玩家一致。

但采样结果是：

```text
1003F031, 1000ms:
  no render callbacks observed

正常玩家, 1000ms:
  每个有效 model slot 有 89–178 次 RenderModel
  每个有效 model slot 有 89–890 次 RenderMaterial
```

所以“DrawObject 最终存在”不能否定初始化期故障。最终结构完整，只能证明内存对象后来被建立；它没有证明 Human 曾成功进入场景选择和 ModelRenderer。

## Penumbra hook 在链路中的位置

| Hook | 层级 | 实际行为 | 本案结论 |
|---|---|---|---|
| `CreateCharacterBase` | Human 创建 | 创建前后发布事件，安装 RSP/decal 等上下文，记录创建结果 | 与建立期直接相交，需观测 |
| `Meta.UpdateRender` | Human 更新 | 识别 collection，push EQP，调用 original，pop | 上游高价值观测点；未证明致因 |
| `Human.OnRenderMaterial` | CharacterBase 材质 callback | 临时替换 shader package 后调用 original | 坏对象未到达此层 |
| `ModelRenderer.OnRenderMaterial` | ModelRenderer 材质提交 | 临时替换 shader package 后调用 original | 坏对象未到达此层 |
| `ModelRenderer.UnkFunc` | ModelRenderer 内部材质路径 | 同类 shader replacement | 坏对象未到达此层 |
| `RedrawService` | Framework/RenderFlags | 一帧置 `0x2`，后续帧清 `0x2` | 已确认可以恢复坏对象 |

Penumbra 材质 hook 源码位于 [`ShaderReplacementFixer.cs`](https://github.com/xivdev/Penumbra/blob/1.6.1.10/Penumbra/Interop/Hooks/PostProcessing/ShaderReplacementFixer.cs)。它们只有在模型已经进入 renderer 后才执行。坏对象完全没有模型 callback，因此这些 hook 不可能解释“为什么一次 callback 都没有”。

## 旧 admission gate 的实际错误

旧实现的目标是：新玩家出现时，在可见性规划完成前先隐藏，避免首帧闪烁。

实际流程是：

```text
UpdateObjectArrays 发现新 PlayerObjectIdentity
  → 查询上一份 Framework applied target
  → 未明确可见则加入 admissionHolds
  → 立即并反复执行 RenderFlags |= 0x1802
  → 等 Framework 规划和 show budget 以后再释放
```

这里有三个已经足以否定该设计的问题：

1. **观察点不是初始化完成点。** hook 看见的是 GameObject 进入查询数组，不是 Human ready。
2. **写入的不是已验证的 admission 状态。** `0x1802` 包含 Model `0x2`、Nameplate `0x800` 和 FFCS 未命名的 `0x1000`；没有 native 证据表明这三个 bit 可以在创建期安全持有。
3. **detour 变成第二个状态执行器。** 它依据上一份 Framework 状态直接修改活对象，而规划、show budget、恢复账本都在 Framework 路径。

这也解释了主城回归：大量新身份先进入 hold，显示路径再受 show budget 限制；某些对象还可能在 hold 期间错过必要更新，导致本应显示的玩家长期没有模型提交。

旧 gate 与 Penumbra 的核心交叉时序是：

```text
GameObject/角色创建进行中
  ├─ Penumbra CreateCharacterBase 上下文
  ├─ EventHorizon 在 UpdateObjectArrays 后写 0x1802
  ├─ SetDrawObject 连接 Human
  └─ Penumbra Meta.UpdateRender 上下文
       └─ 游戏更新 Human 模型/装备状态

之后才可能进入：场景选择 → ModelRenderer → OnRenderModel/OnRenderMaterial
```

这里确认的是生命周期重叠；尚未确认 `0x1802` 在哪条 native 分支上改变了 `Human.UpdateRender` 的结果。

## Penumbra redraw 为什么能恢复

[`RedrawService.cs`](https://github.com/xivdev/Penumbra/blob/1.6.1.10/Penumbra/Interop/Services/RedrawService.cs) 的普通 redraw 队列分阶段执行：

```text
WriteInvisible: RenderFlags |= DrawState.Invisibility (0x2)
下一次队列处理
WriteVisible:   RenderFlags &= ~DrawState.Invisibility
```

它还会对玩家相邻槽中的 mount/ornament 做同样处理。

这说明：

- 坏对象不是永久损坏，原生 visibility transition 可以让它重新进入更新/提交路径。
- redraw 没有直接调用 `SetDrawObject`，不能据此声称 Penumbra 重建了 DrawObject。
- redraw 能修复也不能反推 Penumbra 是制造者；它只是 Penumbra 暴露出的恢复入口。

## 当前代码状态

已经删除：

- `PlayerAdmissionGate`；
- detour 内的 hard-hide；
- 为补救 gate 添加的 `SetDrawObject` suppression；
- ModelRenderer submission suppression；
- 姓名牌 suppression；
- “等待第一次 render callback 才释放”之类循环依赖。

仍然保留 `UpdateObjectArraysHook`，因为玩家槽拓扑变化需要及时触发重新规划。现在它只做：

```text
original UpdateObjectArrays
  → 比较远程玩家偶数槽的 PlayerObjectIdentity
  → 发布 topologyChanged

Framework.Update
  → 消费 topologyChanged
  → 绕过常规 200ms refresh 间隔
  → 正常规划和 reconciliation
```

删除的是错误的 admission 写入，不是对象拓扑通知功能。
