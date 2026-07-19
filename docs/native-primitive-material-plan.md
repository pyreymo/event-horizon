# Underpaint 原生基本图形材质契约计划

> 当前状态（2026-07-19）：本计划取代“继续适配透明衣服材质”作为 Underpaint 原生提交后端的主线。`OnRenderMaterial -> ffxiv_dx11.exe+0x283320`、owned geometry、owned current/previous World CB、独立 shader selection、最小调用参数和 Context 恢复边界继续保留；装备 `e0378` 只保留为历史半透明管线证据，不再作为基本图形的材质、顶点协议或验收样本。

## 目标

Underpaint 是基本图形绘图库。原生后端只需要提供少量明确、稳定、由库控制的 primitive rendering contract，而不是成为任意 FFXIV Model、Material、角色或装备的渲染器。

第一阶段只完成一个内部 `OpaquePrimitiveProfile`：

```text
Underpaint positions / normals / colors / UV
    -> profile-owned vertex packing
    -> profile-owned material constants and texture defaults
    -> profile-owned current/previous transform
    -> native OnRenderMaterial
    -> native pass builder 0x283320
    -> native opaque G-buffer/depth and allowed auxiliary views
```

公开 API 仍以基本图形为中心，例如 triangle、quad、cube、sphere。`.mdl`、`.mtrl` 和 `.shpk` 只是实现 profile 时使用的资源协议，不进入第一版公开 API。

## 非目标

- 不支持绘制角色、装备、Human、Skeleton或任意游戏Model。
- 不承诺加载并绘制任意 `.mtrl`。
- 不把装备槽、角色实例常量或skinned permutation引入基本图形API。
- 不继续解释透明衣服的视觉闪烁。
- 不在第一阶段解决半透明、cutout、water、hair、glass或其他专用shader。
- 不逆向完整SqPack、全部SHPK或整个游戏材质系统。
- 不依赖运行中的Penumbra；Penumbra仅作为离线文件解析和必要时的资源导出工具。

## 文件关系和逆向边界

```text
.mdl
  geometry / submesh / vertex declaration / material slots
       |
       v
.mtrl
  SHPK名称 / material keys / constants / textures / samplers / flags
       |
       v
.shpk
  shader byte码 / inputs和resources / key universes / nodes / passes
       |
       v
runtime renderer/view keys + primitive instance inputs
       |
       v
具体VS/PS permutation和native pass commands
```

单独理解一件装备不能推广到其他材质。需要固化的是一个由 Underpaint 选择的最小 primitive contract，而不是某个任意资产的全部语义。

### 必须取得并逆向

第一轮候选集限制为 **2至3个rigid/static opaque场景物体**。对每个候选，只取得以下闭包：

1. 一个实际使用该材质的 `.mdl`
   - 只分析一个LOD、一个mesh、一个submesh；
   - 记录vertex declaration、stream strides、实际顶点语义、index格式和material slot；
   - `.mdl`只用于证明shader输入契约，不作为运行时donor。
2. 该submesh直接引用的一个 `.mtrl`
   - SHPK名称；
   - material shader keys；
   - constants及默认字节；
   - texture路径、sampler ID和flags；
   - additional data和material flags。
3. 该 `.mtrl` 指向的一个 `.shpk`
   - VS/PS输入与资源声明；
   - system、scene、material、subview keys；
   - 目标opaque node/permutation；
   - pass集合；
   - constant buffer、texture和sampler槽。
4. 该 `.mtrl` 引用的纹理
   - 第一轮只需要路径、格式、尺寸和用途映射；
   - 只有候选不能使用neutral/default绑定时，才读取必要像素内容；
   - 不分析无关mip、染色表或其他材质的纹理。

### 只需核对，不做全面逆向

- 当前客户端中 `OnRenderMaterial` 对所选profile生成的flags。
- `ModelRenderer` canonical scene/subview keys是否覆盖目标SHPK所需键。
- 目标permutation实际使用哪些frame/view constant slots。
- `0x283320` 是否在现有main-view rendezvous自动生成预期opaque和辅助view commands。
- stock material runtime对象是否可以只读复用；若必须修改参数，则改为owned material data，不修改共享资源。

### 明确排除的文件

- `chara/equipment/**`、`chara/human/**`及其他角色/装备 `.mdl/.mtrl`；
- `character*.shpk`、hair、skin、iris、glass、tattoo、occlusion等角色专用SHPK；
- skeleton、animation、physics、VFX、sound和collision文件；
- 与选定submesh没有直接引用关系的其他material和texture；
- 为了“也许以后有用”而批量导出的整个场景或整个SqPack。

## 候选材质筛选标准

候选必须同时满足：

- rigid / non-skinned；
- 普通opaque G-buffer/depth路径；
- 无alpha test、透明重绘、动画UV、风、角色湿润、染色表或骨骼依赖；
- vertex inputs尽可能少，优先Position + Normal + Color/UV；
- material constants少且有明确default；
- 纹理可以由简单neutral纹理替代，或允许纯色结果；
- 能从material defaults和runtime canonical keys锁定实际opaque node；不要求穷举整个通用SHPK；
- 主view与辅助view行为由native builder自动产生，不要求伪造完整场景Model。

候选优先级：简单静态BgParts/场景道具 > 通用地面或建筑材质 > 复杂环境材质。角色、装备和带特殊效果的材质不进入候选集。

## 文件取得能力和协作边界

当前机器可在游戏外直接访问CN客户端SqPack：

```text
C:\Program Files (x86)\上海数龙科技有限公司\最终幻想XIV\game\sqpack
```

本机已有：

- Lumina/SqPack读取能力；
- Penumbra `MdlFile`、`MtrlFile`、`ShpkFile`解析器；
- SHPK node/key/resource解析与DXBC反汇编能力。

因此，只要知道游戏虚拟路径，文件提取和分析不需要用户进入游戏。离线索引若不能方便地从海量资源中发现合适候选，才请求一次Penumbra Resource Tree协助。

候选发现已经使用 ResLogger2 `CurrentPathList.gz` 完成。SqPack索引本身只有hash，不能反查完整虚拟路径；路径列表提供名称，Lumina负责验证文件确实存在，Penumbra.GameData负责解析。当前不需要用户从Penumbra导出文件。

若需要用户导出，最小交付清单为：

```text
candidate-name/
  exact-paths.txt
  one-model.mdl
  referenced-material.mtrl
  referenced-package.shpk
  referenced-textures/        # 仅分析确认确实需要时
```

`exact-paths.txt`必须保留每个文件的原始游戏虚拟路径。不要导出整个角色、装备、场景或mod collection。候选最好是一个普通、静止、无发光/滚动/透明效果的场景小物体。

## 分阶段执行计划

### Phase 0：离线契约分析器

- 复用Penumbra.GameData解析 `.mdl/.mtrl/.shpk`，不重新发明文件格式。
- 输入一组明确虚拟路径，输出同一份机器可比较的contract report。
- 报告至少包含vertex declaration、material keys/constants/samplers、SHPK resources、匹配node、VS/PS和passes。
- 支持对2至3个候选做并排差异，快速淘汰复杂profile。

完成条件：给定文件闭包后，不启动游戏也能回答“这个submesh为什么选择这个opaque permutation，它要求什么输入”。

### Phase 0结果（2026-07-19）

离线比较集固定为以下三个普通室内家具模型：

| 模型 | LOD/mesh/material | 顶点/索引 | vertex contract | material |
|---|---:|---:|---|---|
| `bgcommon/hou/indoor/general/0517/bgparts/fun_b0_m0517a.mdl` | 1/1/1 | 184/564 | streams `8/12`，Half4 Position、Half4 Normal、Half2 UV | `fun_b0_m0517_0a.mtrl` |
| `bgcommon/hou/indoor/general/1015/bgparts/fun_b0_m1015.mdl` | 2/1/1 | — | 同一 `8/12` contract | `bg.shpk` opaque material |
| `bgcommon/hou/indoor/general/0393/bgparts/fun_b0_m0393_0a.mdl` | 2/1/1 | — | 同一 `8/12` contract | `bg.shpk` opaque material |

三者均为rigid，`BoneTableIndex=255`，不需要Skeleton、角色Model或装备数据。第一套profile选用0517：

```text
.mdl
  stream 0: Half4 Position, stride 8
  stream 1: Half4 Normal + Half2 UV, stride 12
  index: UInt16

.mtrl
  bgcommon/hou/indoor/general/0517/material/fun_b0_m0517_0a.mtrl
  Shader: bg.shpk
  Flags: 0x0000000D
  Material keys: none (use SHPK defaults)
  Material constants: 18, 188 bytes of supplied defaults
  Textures: diffuse + specular + shared dummy normal

.shpk
  shader/sm5/shpk/bg.shpk
  material parameter: 23 float4 / 368 bytes
  scene keys: 10
  material keys: 4
  subview keys: 2
  passes: 3
```

`bg.shpk`是大型通用包（56 VS、3850 PS、9244 nodes），但这不意味着需要先解释全部node。0517没有material-key override；实际提交只需把当前renderer/view canonical keys填入owned selection，再观察被选择的opaque node。若选择不能稳定复现，才对该一个node和对应VS/PS做进一步反汇编。

`bg.shpk`声明自己的10个scene keys，不包含角色/装备SHPK使用的model-type key。2026-07-19首次实机提交因此暴露了一个旧假设：代码在复制目标SHPK声明的canonical keys之后，仍强制写入角色model-type key并主动报错。该强制写入已经删除。正确规则是：selection构造器先安装目标SHPK defaults，只对目标SHPK实际声明、且能从当前renderer/subview找到的key覆盖当前值；不得要求两个不同SHPK拥有同名scene key。

原生 `ResourceManager.GetResourceSync` 的hash也已静态确认：`Crc32.FromBuffer`对完整虚拟路径执行标准反射CRC32并返回最终按位取反结果。0517 `.mtrl` 的hash为 `0x5D6A7B3E`，category为`BgCommon`；不再沿用运行时捕获的衣服资源常量。

### Phase 1：选择 `OpaquePrimitiveProfile`

- 只选一个最小候选。
- 把profile所需字段分为：frame/view公共输入、primitive material输入、primitive instance输入和输出。
- 明确哪些default可由Underpaint拥有，哪些stock runtime资源只能只读借用。
- 若候选仍要求角色或场景对象专用输入，直接淘汰，不为其补兼容层。

完成条件：profile不包含Character、Equipment、Skeleton、source Model或任意资产实例指针。

### Phase 2：稳定基本图形PoC

- 删除 `e0378` material和衣服vertex ABI硬编码。
- 使用profile精确打包一个triangle/quad，随后增加cube。
- 使用owned VB/IB/declaration、owned current/previous World CB和profile-owned constants/default textures。
- 在现有原生main-view时机调用 `OnRenderMaterial -> 0x283320`。
- 肉眼稳定显示，并用有界日志确认native opaque draw连续执行。

完成条件：静止和移动镜头下连续数百帧稳定；不依赖角色是否可见、穿什么装备或当前场景是否恰好绘制20-byte角色stream。

当前实现状态：

- 已把运行时材质从 `e0378/characterlegacy.shpk` 改为0517 `bg.shpk`；
- owned geometry改为Half4 Position、Half4 Normal和UV；首个PoC把文件中的Half2 UV扩为已验证的Half4 runtime declaration，等待实机后再决定是否补Half2 runtime format映射；
- 已删除source `stride=20`和source 176-byte constant的rendezvous过滤，当前只借用main-view render thread、TLS Context和builder调用时机；
- 176-byte default object constant仍是Underpaint-owned `OnRenderModelParams+0x10`兼容输入，但不再按`g_InstanceParameter`查找或绑定到目标SHPK。它是否可完全删除，需要一次BG材质callback实机结果；这不是角色donor依赖。

### Phase 3：收敛internal后端

- 将提交核心与 `OpaquePrimitiveProfile` 分离。
- 移除source stride=20和176-byte角色constant的rendezvous过滤。
- 保留frame/view/TLS Context和command arena作为合法原生运行环境。
- 完成多实例、同geometry复用、transform history、场景切换、删除及延迟释放。
- 删除衣服专用capture、日志和按钮。

完成条件：内部API只表达geometry、profile、instance和transform。

### Phase 4：单独研究半透明primitive profile

Opaque稳定后，才根据已经确认的Stage A/后续重绘管线证据选择一个简单半透明contract。透明衣服只用于验证pass家族是否完整，不再提供运行时material、vertex ABI或实例输入。

## 当前保留、替换和删除

| 部分 | 决策 |
|---|---|
| `0x283320` 原生pass builder | 保留 |
| 同线程、同Context、同view时机 | 保留 |
| owned VB/IB/VertexDeclaration | 保留，改由profile定义布局 |
| owned current/previous World CB | 保留 |
| shader selection与canonical keys | 保留，改由profile/SHPK报告驱动 |
| Context集中保存恢复 | 保留 |
| `e0378`衣服 `.mtrl` | 删除 |
| 衣服 `20/24` vertex payload | 删除 |
| 固定176-byte角色 `g_InstanceParameter` | 从通用核心删除；只有profile实际声明才创建对应constant |
| source stride/角色constant rendezvous过滤 | Phase 3删除 |
| 同步G-buffer Copy+Map readback | 永久禁止 |

## 验收标准

第一版 `OpaquePrimitiveProfile` 完成必须满足：

- 文件contract可由离线报告完整复现；
- Underpaint不修改任何共享Model、Material、角色或场景对象；
- 基本图形使用owned geometry、transform和所有per-instance可变数据；
- commands由原生builder生成，不patch packet；
- quad/cube肉眼稳定显示数百帧；
- 相机运动、首次出现、瞬移和重新出现时current/previous正确；
- 场景切换、隐藏、删除和插件卸载无资源提前释放或Context污染；
- Debug/Release和现有tests通过；
- 文档记录profile的精确支持边界，不宣称任意Material或vertex layout。

## 进度记录

| 日期 | 阶段 | 结论 | 下一步 |
|---|---|---|---|
| 2026-07-19 | 方向重置 | 原生提交入口和owned实例链保留；衣服材质不再是后端验收条件 | 建立离线contract analyzer并筛选2至3个static opaque候选 |
| 2026-07-19 | Phase 0完成 | 离线枚举、`.mdl/.mtrl/.shpk`解析和三候选对照完成；选定0517 `bg.shpk` profile，无需用户导出 | 实机验证BG profile的material callback、permutation和稳定可见性 |
| 2026-07-19 | Phase 2实现中 | 衣服材质/vertex ABI和carrier过滤已从提交路径移除；Debug/Release构建通过 | 用户显示preview并采集一次bounded draw capture |
| 2026-07-19 | BG scene-key修正 | 首次实机在提交前失败：旧代码强制向`bg.shpk`写入角色model-type key；已改为只覆盖目标SHPK声明的canonical keys | 再次实机验证material callback和实际opaque draw |
