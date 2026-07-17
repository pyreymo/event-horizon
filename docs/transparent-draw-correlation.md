# TransparentDrawCorrelation 交接说明

日期：2026-07-17

## 本轮状态

旧的 Underpaint 手写 Stage A/C 近似后端已经冻结，只保留为实验对照。临时 `Skip Stage A` A/B 开关已经删除。`agent/stability-probes` 中已经验证过的 pass 退出注入、viewport/scissor、投影与 depth-bias 稳定性修复继续保留，因为它们用于避免旧后端污染后续实验判断，不代表继续扩展近似材质系统。

新的 `TransparentDrawCorrelation` 路径只在 Debug 构建中存在，且仅做读取：

- EventHorizon 在 framework/game-thread 时点从当前目标生成不可变 donor 快照；
- hook `CharacterBase.OnRenderModel` 和 `CharacterBase.OnRenderMaterial`，记录目标自己的 model/material callback；
- Underpaint 对原生半透明 Stage A/C 的 draw 做有界快照；
- 捕获 VB/IB、draw range、VS/PS/input layout、VS/PS constant-buffer 指针及有限 hash、PS SRV 的底层 resource 身份、线程、单调时间和 native module+RVA stack；
- 捕获最多 4 个 Stage A 帧、128 个 draw、1024 个 callback 或 15 秒；draw 预算在 A/C 两侧各保留一半，避免 Stage A 抢完预算；任一边界到达后自动停止；每个 shader stage 每个 draw 最多只 readback/hash 一个有限大小的 constant buffer，避免无界 GPU 同步；
- matcher 同时使用 donor texture、geometry、draw range、constant hash 和共享资源评分，不以 VB/IB 或 index count 单独判定；
- 全程不写队列、不复制 packet、不调用候选 builder，也不跨帧保存可供异步解引用的原生指针。

日志 source 固定为 `TransparentDrawCorrelation`，写入现有 `debug-logs/event-horizon-*.log`。

## 已确认的 FFCS 映射

- `CharacterBase.OnRenderModel(CharacterBase*, Model*)` 可作为目标进入 render scheduling 的上界。
- `CharacterBase.OnRenderMaterial(CharacterBase*, OnRenderMaterialParams*)` 的参数公开 `Model*` 和 `MaterialIndex`。
- `Model` 公开 `SlotIndex`、`ModelResourceHandle*`、`Materials` 和 `MaterialCount`。
- `Material` 公开 `MaterialResourceHandle*`、`ShaderFlags`、shader keys、material constant-buffer 和 texture entries。
- `TextureResourceHandle.Texture->D3D11Texture2D` 可与 D3D draw 当前绑定 SRV 的底层 resource 身份直接比较。

这些公开结构足以完成第一轮 runtime correlation，因此本轮没有使用 IDA。

## Debug DLL 的人工步骤

1. 准备一个可选中的 PC，推荐自己的角色。
2. 只保留一件容易识别的 `CharacterTransparency` 透明装备，并记下物品名和装备槽。
3. 暂时关闭该 donor 的 Penumbra 模型/材质替换。
4. 选择安静普通场景，固定镜头和姿态，避开水体、雨淋、湿身、Gpose 和持续动作。
5. `/eh 3d` 打开 Underpaint Demo，展开 `Transparent draw correlation`。
6. 选中 donor，确认摘要中的对象、model 和 texture 数量合理，点击 `Arm transparent capture`，等待状态变为 Complete。
7. 只移除或替换该透明装备，在相同镜头再次 arm。
8. 提供两次日志、物品名、装备槽，以及捕获期间是否出现长卡、崩溃或持续日志。

当前代码侧能安全先行的内容已经完成。下一步必须先取得上述 donor-on / donor-off 实机日志，才能判断 A/C 是同一 item、两份 pass-specific packet，还是应继续上移到 `ModelRenderer` / material 展开层。
