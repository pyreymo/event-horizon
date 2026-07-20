# Underpaint 原生基本图形材质契约计划

> 当前状态（2026-07-19）：优先验证最小 ModelRenderer 边界，不预设存在或必须构造统一 render item。当前实验只借用同步现场的 render thread、ModelRenderer、view/TLS Context 和 command arena；材质、SHPK selection、几何、World、instance constant 和 `OnRenderMaterialParams2` 均由 Underpaint 构造。`e0378` 暂时只作为已知能覆盖 `0x283320` 协议的测试 fixture，不是公开材质目标。只有该最小边界被明确否定后，才重新引入 bounds、LOD、history、object identity 或更高层 render job。

## 目标

Underpaint 是基本图形绘图库。原生后端只需要提供少量明确、稳定、由库控制的 primitive rendering contract，而不是成为任意 FFXIV Model、Material、角色或装备的渲染器。

第一阶段只完成一个内部 `OpaquePrimitiveProfile`：

```text
Underpaint positions / normals / colors / UV
    -> profile-owned vertex packing
    -> profile-owned material constants and texture defaults
    -> profile-owned current/previous transform
    -> matching native renderer material selection
    -> renderer-specific native pass builder
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

### 当前优先实验：最小 ModelRenderer 调用边界

本轮不横向调查 BG/Figure/Decal，也不先建立公共 render-item 模型。验证顺序是：

```text
合法的当前原生运行环境
  render thread + ModelRenderer + view/subview + TLS Context + command arena
        |
        +-- Underpaint-owned VB / IB / VertexDeclaration
        +-- Underpaint-owned current World == previous World
        +-- 显式加载的合法 ModelRenderer Material / SHPK
        +-- selection 从全零初始化，不复制 donor descriptor/cache
        +-- Params2 从全零构造，只写明确的 main/aux view masks
        +-- owned 176-byte instance constant和最小零值Model facade
        |
        v
  OnRenderMaterial
        |
        v
  0x283320 pass builder
        |
        +-- bounded PushBackCommand probe
        |     command count / SortKey / pass / view / VS / PS / descriptor
        |
        +-- existing bounded D3D draw capture
              final Count / shader / layout / constants / resources / state
```

历史版本已经证明同一 `e0378` fixture、20/24 geometry ABI、owned World和instance constant能生成完整command集合。本次只验证剩余donor语义是否必要：

1. shader-selection首字段不再从source selection复制。静态复核确认`OnRenderMaterial`只生成flags和可选callback输出，不负责选择descriptor；selection的`+0x00`可以保持为零，真正的descriptor由后续SHPK resolver根据`+0x08/+0x10/+0x18/+0x20/+0x24`返回；
2. `OnRenderMaterialParams2`不再复制source 0x48-byte wrapper；`+0x38/+0x44`使用当前实验明确允许的main/aux masks，其余输入和callback输出从零开始；
3. 不读取source Model、source geometry、source Material、bounds、LOD、history、sort record或object identity；
4. sort只观察builder在当前view环境生成的结果，第一版不另建culling/sort/history对象。

成功判据不是“代码走到builder”，而是同一次有界采集中同时出现：selection `+0x00`保持零且resolver返回非零descriptor、builder command数量/SortKey/view/shader合理、最终owned geometry draw执行、current/previous World相同。若失败，按最先失败的明确边界回退一个变量；不由一次失败推导完整render job必需。

2026-07-19实机已经越过resolver并稳定执行owned quad：一次有界捕获得到4条Opaque `DrawIndexed Count=6`和15条辅助draw，四条Opaque的VS、PS、input layout及7个SRV地址完全一致，证明零seed selection、resolver、`0x283320`和固定目标Material纹理均能独立工作。`OnRenderMaterial`传入的`materialIndex=0`不是装备slot；静态上它只会被转交给`Model.RenderMaterialCallback`，而owned零值Model facade没有该callback，因此当前路径中这个0没有选择头部或身体装备。

肉眼对照同时暴露了下一项残留：裸体时为白色发光方块，只穿头或身体时出现同一固定`e0378` atlas的不同着色，叠穿时可能在白色与atlas之间切换，头+身时稳定采用头部对应外观。这说明当前rendezvous的TLS Context不只是frame/view公共状态，还残留当前carrier已经安装的Character SHPK常量。owned路径已经替换World、`g_InstanceParameter`、目标material constant和目标material textures，但selected character permutation还会消费其他per-character绑定，例如customize/stain/character-lighting一类constant；当前每帧由哪个geometry/material调用先命中rendezvous，决定了这些未覆盖槽的来源。

这项结果不要求恢复装备slot追踪，也不要求先构造完整render item。下一步只反射当前selected VS/PS实际使用的constant/resource binding，将其分类为frame/view公共输入和Character专属输入；前者保留，后者用Underpaint-owned neutral constant完整覆盖。若所有实际使用的非view绑定均可独立提供，则继续保留`0x283320`最小边界；只有存在无法从Character之外表达的必需输入时才上移。

2026-07-20两组不同carrier的最终draw快照把残留进一步缩小：目标VS/PS、layout、World CB、256-byte实例CB、512-byte material CB以及SRV 0/1/2/3/5/6均保持稳定；只有VS slot 3的16-byte CB、PS slot 4的16-byte CB及SRV slot 4随carrier改变。VS/PS各自的frame/view CB仍按帧变化，属于预期公共状态。方块同时能够向场景投影阴影，证明builder已经把owned geometry扩展到辅助/阴影视图；无需另建shadow draw。

因此当前可直接控制的最小拦截面固定在`ApplyMaterial`之后、`0x283320`之前的TLS Context resource arrays：

```text
owned selection + owned Material
        |
        v
ApplyMaterial
        |  写目标material CB与.mtrl直接纹理
        v
selected VS/PS resource reflection
        |  D3D CB/SRV slot -> native resource Id/CRC
        v
Underpaint覆盖非view的constant/texture/sampler绑定
        |
        v
0x283320
        +-- main opaque commands
        +-- auxiliary commands
        +-- shadow commands
        |
        v
scope统一恢复TLS Context
```

新增的一次性窄探针直接枚举已选VS/PS的`PVShader.ResourceEntry`，记录每个D3D slot对应的原生Id、SHPK CRC、大小以及当前Context资源地址。下一次采集即可把上述CB3/CB4/SRV4反查为可覆盖的原生slot，而不再通过装备部位或draw consumer间接猜测。

首次resource-map结果只包含rendezvous当时active auxiliary pass的VS资源（CB0 `id=24/CRC=F0BAD919`、CB1 `id=5/CRC=76BB3DC0`），PS不使用资源；它没有覆盖builder随后为main opaque及shadow切换的descriptor pass。探针已修正为遍历同一descriptor的全部16个pass，按实际VS/PS组合去重，并通过preview capture对象写入同一个debug文件。这样仍是一条有界记录，但能够覆盖最终draw使用的CB3、CB4和SRV4，而不是把active-pass局部信息误当成整个permutation契约。

完整映射确认剩余三项为：VS CB3 `id=35/CRC=4E0A5472`即`g_ModelParameter`，PS CB4 `id=37/CRC=5B0F708C`即`g_DecalColor`，PS texture register 4 `id=64/CRC=2005679F`即`g_SamplerTable`。离线反汇编确认当前VS只读取`g_ModelParameter.m_Params.x`；运行中原生值为`x=1`，因此owned默认为`(1,0,0,0)`。`g_DecalColor`的原生中性值为`(1,1,1,1)`。目标MaterialResourceHandle自身含color table，使用游戏原生`PrepareColorTable(0,0)`生成owned table texture；不再复制carrier的table。

这项工作的架构位置是**native material profile的shader-input装配层**，不是新的draw入口，也不是render-item层：

```text
Underpaint API / draw requests
        |
        v
owned geometry + instance transform + material profile
        |
        v
render rendezvous（只借当前thread/view/arena）
        |
        v
[当前层] 安装该profile声明的全部owned shader inputs
        |  material CB / instance CB / model CB / decal CB / textures / table
        v
0x283320 native pass builder
        |
        +-- main opaque
        +-- auxiliary views
        +-- shadow
        v
native commands -> GPU
```

它的项目价值是闭合一个primitive profile的运行时依赖：只拥有VB/IB、World和`.mtrl`还不够，selected SHPK会从TLS按native Id读取额外资源；若没有这一层，API表面上是自定义绘制，实际颜色、闪烁甚至pass结果仍取决于当时碰巧作为rendezvous carrier的角色装备。完成这一层后，carrier只提供frame/view公共环境，不再提供物体语义；后续新增profile也可以由离线contract明确列出并安装所需binding，而不必重新逆向整条command链。

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

2026-07-19第二次BG实机在builder内部崩溃于`ffxiv_dx11.exe+0x23BB50`。该指令是`mov rdx, [rdx+0x20]`：command snapshot已经按当前pass从selection descriptor选出了VS对象，但结果为null，随后无条件读取该VS对象。静态回溯还发现并修正了两个旧PoC错误：

1. `Params2+0x38`由`OnRenderModelParams`上层调用传入，是当前render invocation的view/pass输入；代码却把它强制改成衣服实验的`0x01C00000`，要求BG node生成并不存在的command family。现在保留同步现场原值，只继续清零callback/pass输出`+0x40`。
2. `Params2+0x30`指向shader-selection对象；该对象首字段才是selection descriptor。旧代码少解引用一层，把source selection对象地址当成descriptor。现在复制`**(Params2+0x30)`，与游戏原生stack selection初始化的字段语义一致。

这两处修正收紧了合法输入边界：目标材质、keys、bindings和geometry仍为owned；`+0x38`被明确归入当前frame/view调用级公共输入，不能写成某个材质profile的固定mask。但第三次实机仍以完全相同的地址和寄存器形态崩溃，因此它们不是空VS的充分解释，不能再把“修正了可疑字段”等同于“关闭了崩溃”。

当前版本已硬性切换为selection probe-only：调用与builder相同的原生SHPK resolver，记录解析后的descriptor、当前active pass以及16个pass槽对应的VS/PS索引和对象指针，然后主动停止实例，绝不进入`0x283320`。探针只读取resolver和command snapshot在空VS解引用之前已经读取成功的descriptor/table，不解引用选出的shader对象。只有probe证明当前实际请求的pass有合法shader后，才允许重新开放builder。

2026-07-19安全probe实机结果为`ActivePass=1`，slot 0解析到非空`VS4/PS0`；pass 2的`VS5/PS3`也非空。这排除了目标BG材质、builder入口主pass permutation和resolver失败。反汇编显示`sub_140283AC0`先直接snapshot并push第一条command，随后在`0x283E73`通过`sub_1402EFBE0`生成第二条command；历史崩溃恰好发生在第二条snapshot。该snapshot既可能在selection状态被消费后回退到`Context+0x878/+0x880`的current VS/PS，也可能在builder内部切换pass后遇到descriptor空槽；入口probe本身不能区分二者。owned路径此前只安装了descriptor，没有安装current shader槽，这是需要先补齐的明确调用边界缺口。

后续安全实机命中了`ActivePass=6`，descriptor只提供pass 1和2。这直接推翻了“只缺current shader槽”的解释：`0x283320`内部的ModelRenderer helper明确要求第六类pass，而目标BG package没有这个协议。离线对照也显示`bg`、`bgprop`、`bgcolorchange`、`bgcrestchange`、`bguvscroll`和`crystal`均只有同样三类pass；只有`character`/`characterlegacy`覆盖ModelRenderer使用的六类pass。换另一个BG材质不会修复该入口。

当前实现保留snapshot安全闸门，并在进入builder前根据owned flags检查pass 6；不兼容profile会在任何command写入arena前明确停止，避免产生部分提交。不得跳过pass 6、把pass 1 shader冒充pass 6，或清除原生material flags来伪造成功。

静态定位到BG的对应链路：`0x290520`准备view、sorting和1至2个pass descriptor，随后调用`0x290E10`；后者遍历BG batch中的LOD/mesh/submesh/material，安装geometry/material bindings，调用同一个`0x1417E5700` resolver，再通过`0x23B920`生成command。它不是一个只接收VB/IB和Material的简单入口：当前输入仍包含BGInstancingRenderer、BG batch、resource tables和submesh记录。下一阶段只沿这条链向上确认是否存在可复制的per-instance/batch item；若必须伪造完整BgParts资源对象，则终止“复用BG builder”路线，基本图形继续使用Underpaint自有opaque backend，而不是退回Character材质。

继续向上追踪后，`0x290520`没有普通直接caller；它由BG renderer初始化函数`0x28EAC0`注册到全局render callback表。回调边界为五个参数，除renderer和view/index外，一个参数持有实例位置/排序所需数据（函数直接读其间接数据的`+0x20..+0x28`世界位置并与当前camera origin计算sort depth），另一个参数持有scene-key descriptor/value map。这说明可复制边界若存在，应在render job生成该回调记录的一层，而不是`0x290E10`内部。

`0x290E10`的读集进一步说明：它从多态BG对象的`+0x30`资源表读LOD ranges、36-byte submesh记录、vertex/index bindings、material数组、material resource、shader package、constants、textures和samplers。这只否定了“直接把`0x290E10`当成简单primitive API”，不能否定以下候选：

- BG/Model/Figure/其他renderer在resource traversal之后是否汇入共用render-item或pass expansion helper；
- `0x290E10`的分支callee（包括`0x293570`）是否已经是更小的单submesh/pass边界；
- 是否存在游戏自带的debug/figure/decal/primitive renderer，其输入天然就是基本图形；
- 能否合法构造共用render item并调用pass生产层，而不是patch已生成command packet。

此前提出的“横向对比Model/BG/Figure尾部call graph”暂停。它是在最小ModelRenderer边界尚未被否定时过早扩大范围；只有本节实验给出明确反证后才恢复。

2026-07-20的连续实机结果确认Character profile的三个额外语义绑定已经闭合：最终draw中的VS CB3 `g_ModelParameter`、PS CB4 `g_DecalColor`和PS SRV4 `g_SamplerTable`在穿脱装备后保持同一owned payload/resource。此前的白色方块和随装备变化的染色外观已经消失，方块保持世界位置并由builder自动进入阴影视图。

同一批日志也暴露了最后一个仍共享的material输入：第三次capture内，PS CB0 `g_MaterialParameter`在相同目标MTRL、shader和textures下从hash `AE2A985B1F29D192`切换为`8AEE006A6135AF5A`。这与用户看到的偶发黑帧相符，且比继续猜测pass或装备slot更直接。后端现已在首次加载目标Material时完整复制其native material constant buffer，并在每次`ApplyMaterial`之后将对应TLS constant slot替换为Underpaint-owned副本；scope仍负责无条件恢复原绑定。下一次实机只验证该hash是否稳定以及黑帧是否消失。

上述“复制一次native ConstantBuffer”实现经实机否定：最终draw的PS CB0仍在变化，且底层D3D buffer地址会轮换。至少可以确认，只替换TLS wrapper并向`ConstantBuffer.LoadSourcePointer`写一次不能形成持久immutable payload；该API参与按提交更新的native上传存储。修正后的所有权分成两层：首次从目标Material复制到managed immutable byte array，之后每次提交重新调用`LoadSourcePointer`并从该快照上传，绝不再次读取共享Material内容。

同轮最终draw还显示SRV3在两套资源间切换。Penumbra离线解析确认selected register对应`g_SamplerDecal`（CRC `0x0237CB94`）；目标MTRL只声明normal、mask和index纹理，本身没有decal，因此这个槽此前一直继承carrier。后端现在从`CharacterUtility`取得游戏长期持有的透明公共纹理，并显式安装到目标SHPK的decal native Id；Context scope同时保存和恢复该槽。它是stock immutable default，不包含角色或装备实例语义。

共享owned constants还可能被不同render worker同时调用`LoadSourcePointer`。当前internal PoC已将自定义提交临界区串行化；hook状态本身继续使用thread-static字段，因此不会把其他worker的原生command误判成当前自定义提交。这是当前规模下的安全约束，正式多实例后端应改为per-frame/per-worker upload ownership，而不是长期依赖全局串行化。

剩余的小范围位置抖动暂按独立问题处理。当前world数据本身稳定，提交会命中同一frame内不同view/Context rendezvous；抖动可能来自view时机与transform采样不一致，也可能包含TAA jitter。必须先关闭material constant黑闪，再用frame/view identity和最终world hash做一次有界对照，不把它与材质依赖混合修复。

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
| `0x283320` ModelRenderer pass builder | 当前第一优先最小边界；先验证显式owned输入是否足够，不要求完整render item |
| `0x290520 -> 0x290E10` BG builder | 不作为简单primitive API直接调用；保留其尾部callee作为共用pass层候选 |
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
| 2026-07-19 | BG builder崩溃隔离 | 两次修正后第三次仍在同一空VS读取处崩溃；停止推测式提交，改为resolver probe-only | 安全采集实际descriptor的16个pass槽，确定缺失shader的精确原因 |
| 2026-07-19 | BG current shader输入补齐 | probe确认active pass 1的VS/PS均有效；崩溃位于同一builder生成的第二条command snapshot，owned路径缺少`Context+0x878/+0x880`回退输入 | 实机验证两条command均生成、Context恢复且三角形可见 |
| 2026-07-19 | Model/BG协议错配确认 | 安全闸门捕获builder内部`ActivePass=6`；离线SHPK对照确认BG全家族只有三类pass，`0x283320`属于六类pass的ModelRenderer协议 | 停止`bg.shpk -> 0x283320`；静态审计BG的`0x290520 -> 0x290E10`最小batch边界 |
| 2026-07-19 | BG callback边界 | `0x290520`由`0x28EAC0`注册到render callback表；它在进入batch builder前已从实例记录和camera计算sort depth，并接收scene-key map | 定位生成该回调记录的render job，判断其是否可脱离完整BgParts对象构造 |
| 2026-07-19 | 决策纠正 | BG当前边界需要完整resource graph，但尚未审计renderer之间的共用pass/render-item层，不能由此判死原生提交路线 | 恢复研究实现；横向对比Model/BG/Figure等renderer的尾部call graph |
| 2026-07-19 | 乐观边界实验 | 暂停横向renderer调查；以已知合法的ModelRenderer fixture直接验证显式参数+当前view/TLS是否充分 | selection零seed、Params2零构造，采集builder commands与最终draw |
| 2026-07-19 | selection判据修正 | 首次实机在resolver前被本地检查拦截；静态确认`OnRenderMaterial`从不写selection `+0x00`，resolver也不读取该字段 | 删除错误前置检查，以resolver返回值验证独立selection |
| 2026-07-19 | 最小builder成立、Character常量仍污染 | owned quad稳定生成4条Opaque与15条辅助draw；shader/layout/SRV稳定，但外观随裸体/头/身体carrier改变，`materialIndex=0`并非装备slot | 只枚举selected permutation实际使用的绑定，owned覆盖剩余非view Character constants |
| 2026-07-20 | TLS污染收敛 | 两组draw仅有VS CB3、PS CB4和SRV4随carrier变化；owned方块可投影阴影，确认builder自动生成辅助/阴影视图commands | 用selected shader resource map反查native Id/CRC，随后在builder前安装owned neutral绑定并由统一scope恢复 |
| 2026-07-20 | resource-map探针修正 | 首次输出只反射rendezvous active auxiliary pass，无法解释最终opaque draw；同时Underpaint插件日志未进入专用debug文件 | 遍历descriptor全部有效pass并随preview capture导出，下一次采集直接获得完整native Id/CRC映射 |
| 2026-07-20 | Character profile输入闭合 | 完整descriptor映射确认污染为`g_ModelParameter`、`g_DecalColor`和`g_SamplerTable`；shader反汇编及原生值给出中性常量，目标MTRL可原生创建自己的color table | 实机确认外观不再随carrier变化，draw中的CB3/CB4/SRV4稳定且TLS在提交后恢复 |
| 2026-07-20 | semantic TLS验证 | 三个owned绑定在多轮capture中稳定，白块和装备染色漂移消失；PS CB0仍在单次capture内变化并对应偶发黑闪 | 将完整`g_MaterialParameter`复制为owned native CB，覆盖ApplyMaterial安装的共享绑定 |
| 2026-07-20 | material CB私有化 | target MTRL的material constant在首次装载时复制一次，后续提交不再重新导入共享buffer变化；Debug/Release构建通过 | 实机验证PS CB0 hash稳定、黑闪消失；随后单独定位小范围位置抖动 |
| 2026-07-20 | 一次复制方案被否定 | 最终draw仍显示PS CB0 payload及底层buffer变化；SRV3也在两套资源间切换，离线确认其为缺省`g_SamplerDecal` | 保存managed immutable material payload并逐提交上传；显式绑定公共透明decal；串行保护共享upload源 |
