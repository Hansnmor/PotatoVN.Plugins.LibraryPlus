# NextMoe 时代的全局重构评估（提案，未改动任何代码）

> **先读另一版**：只想了解「每个功能能不能换新办法」→ 看 `doc/FEATURE-REVIEW-nextmoe.md`（人话版，按功能逐条评估）。
> 本文是改代码时用的工程细节：逐文件分类、行号证据、目标架构、分阶段顺序与红线。

> 评审时间：2026-10-03 · 评审方式：4 路子代理只读深读（数据源链路 / 评分与持久化 / UI 层 / 宿主交互）+ 25 次真实 API 实测
> 本文只做判断与排序，**不含代码改动**。行号以 LF 实际行数为准（`Measure-Object -Line` 会漏空行，勿用）。

## 1. 为什么要做这次评估

kungal 的 HTTP API 历史上改过多次，插件为适配它反复改动。2026-09 kungal 全站 API 改为需登录（匿名一律 `401 code:205`），v1.5 于是改成「kungal 网页 SSR 抓取 + NextMoe API」双数据源。**现在前提又变了：kungal 的上游 NextMoe 开放 API v2 可直接调用**（`https://api.nextmoe.dev/v2`），它已经把 VNDB / Bangumi / ErogameScape / DLsite / Getchu / HowlongToBeat 对齐成一条记录。

于是插件里大量为「绕开 kungal 结构漂移」而存在的代码，从**必要成本**变成了**纯成本**。本次评估回答三件事：

1. 哪些代码现在可以直接删（零风险）；
2. 哪些功能的实现方式应该整体换掉（换完能省多少行 / 多少请求）；
3. 哪些东西**看着丑但绝不能动**（动了就丢数据或崩溃）。

体量：44 个源文件、约 8.5k 行（`SortPage.xaml.cs` 实测 **1772 行**、`KungalPhraser.cs` 855 行、`VolumeNormalizer.cs` 549 行、`LaunchGuardHelper.cs` 452 行、`GalgameClassifier.cs` 453 行）。

## 2. 结论摘要

- **模型病灶**：插件内部 DTO 仍是 kungal 形状（`Kungal*` + `KungalEnvelope` + `name/name_original` + `spoiler_level` + `galgame_count`），所以**每换一个源就要再写一个映射器**（现在已有 `KungalWebMapper` 237 行 + `NextMoeKungalAdapter` 347 行往同一个形状里灌）。这是「源改一次、插件改一遍」的结构性根因。
- **量化对比**：kungal 网页映射层有 **46 处** `KungalWebClient.Get(...)` 容错取值（全库另有 `AsStr` 40 / `AsDict` 9 / `AsBool` 3 / `Items(` 10），而 NextMoe 侧是强类型映射（0 处）。这 46 处就是「结构漂移」的账单。
- **可直接删除**（零风险，约 **−250 行 + 1 个 NuGet 依赖**）：`KungalClient` 死代码、`KungalEnvelope`、`ExternalRatings` 死数据路径、`PluginData.KungalGameData.AvgRating`、无调用方的单品角色接口、`NextMoeModels` 死字段。
- **可整体换掉**（收益最大）：**评分链路**（双客户端 + 宿主 token 反射 + 两套归一约定 → 一次 `include=ratings`）；**匹配层**（最坏 12 次请求 → 1 次 `refs=`）；**角色中文名装置**（≈230 行 bgm 兜底 → `localized`/`aliases`，已有实测证据支持）。
- **必须保留**：kungal 网页 SSR 的 `game_types`（分类第③步的唯一来源，NextMoe 无对应字段）；宿主 `IGalInfoPhraser` 的字段保护契约；COM vtable/RCW 纪律；守卫与音量规范化的全部权宜（经宿主源码核实**仍在解决真实问题**）。
- **最硬的既有缺陷**：评分**负缓存终身不失效**（`FetchedAt` 全库无读取点、UI 无「清理评分缓存」入口）。

## 3. 现状：三条链路与真实成本

### 3.1 搜刮链路请求预算（实测口径）

| 场景 | 请求构成 | 备注 |
|---|---|---|
| **有 gid 记忆**（常态） | 1 次 kungal 网页（~2s / 844KB / devalue 还原）+ 1 次 NextMoe 角色批量 | 网页失败时 +1 次 NextMoe 作品详情 |
| 规则②触发时 | +1 次 bgm 角色表（~2s）+ **N 次 bgm 角色页** | 日志实测：10 次抓取 7.3s、命中 **0** |
| **无 gid 记忆**（新游戏，最坏） | ≤3 个 vndb 前缀变体 ×（1 搜 + ≤3 候选详情校验）+ ≤3 次名称搜索 → **≈12 次** | 候选校验走网页主源，每次≈1MB |
| **NextMoe 为主的目标态** | `refs=`/搜索（1）+ 详情含 ratings/playtimes（1）+ 角色批量（1）= **3** | 要保 `game_types` 则 +1 网页 |

### 3.2 评分链路

```
[详情页] 缓存命中 → Compute(WSD:15)                    零网络
        未命中 → FetchRatingAsync(RP:160)
                 ├ VndbClient（POST /kana/vn，rating÷10）      ← 1 请求
                 ├ HostServices.GetBgmTokenAsync()（4 跳反射）  ← 宿主 token
                 ├ BgmClient v0 +Bearer（R18 可见）            ← 1 请求
                 └ 失败 → BgmClient v1 搜索兜底（可能命中另一个条目）← +N 请求
[批量]   串行 N 款 × 上面 2 请求（200ms 节流）→ 每款各写一次整包 JSON 落盘
[排序]   每次两两比较都重算开方（无派生值缓存）
```

### 3.3 UI 层

`SortPage.xaml.cs` 1772 行的核心病灶**不是长度**，而是**缺「页面状态 + 一致刷新」单点**：刷新有 **4 套并行实现**（`RefreshFilter()` 352 / `Range_Click` 649–657 手抄 / `RemoveGame_Click` 1765–1768 **漏了 Filter** / `OnHostPhrased` 100–115）；恢复重放序列在构造器与 `ImportData_Click` 重复两遍；批量搜刮与批量评分的锁定/进度/finally 恢复约 60 行逐字重复。

## 4. 逐文件复杂度分类（A = 源缺陷权宜 / B = 本质 / C = 宿主约束）

### 4.1 数据源链路

| 位置 | 类别 | 判断 |
|---|---|---|
| `KungalClient.cs:12-68` | A | **整段死代码**（无 `new KungalClient`）；仅 `HtmlToText`(:70-83) 活着，被 `KungalPhraser:583/590/824` 使用，AngleSharp 仅此处使用 |
| `KungalModels.cs:6-12` | A | `KungalEnvelope` 只有死客户端用 |
| `KungalModels.cs:49,60-69` | A | `ExternalRatings`/`KungalExternalRating`：**两个映射器都在填、全插件无人读**（adapter:34 注释自认） |
| `KungalModels.cs:111,32,26` | A | `KungalRating.Overall` 从未读；`Total` 只写不读；`ExactRefMatch` 是为绕开自由文本搜索设的信任标记 |
| `KungalPhraser.cs:296-455`（≈160 行） | A | 整套角色中文名装置：拉 bgm 全角色 → 原名/Dice 匹配 → 逐个抓 bgm 角色页正则取译名 |
| `KungalPhraser.cs:727-743` | A 且部分失效 | vndb 候选「取前 3 逐个拉详情比对」；API 回退路径下 `VndbId` 恒 null（`^v\d+$` vs 实测全 `r…`）→ 只有 `ExactRefMatch` 在起作用 |
| `KungalPhraser.cs:804-813` | A | `NormalizeVndb`/`VndbIdVariants` 是「kungal 带 v / 宿主不带 v」的产物；两变体归一后是**同一个** `refs=vndb:vNNN` → miss 时白花一次 RTT |
| `KungalPhraser.cs:155-159,843` + adapter:203 + mapper:86 | A | 把 `is_sexual` 还原成旧 kungal 的 `"sexual"` 类，只为让 `Category=="content"` 过滤保持 v1.4.6 行为 |
| `KungalPhraser.cs:589-591` | A | `kc.Intro` 单字段兜底，旧形状残留 |
| `KungalPhraser.cs:40-43,108-111,200-202,766-776` + `Plugin.cs:51` | A 命名债 | 日志/提示/`ParserName` 仍称 kungal，实际走 NextMoe（排障误导） |
| `NextMoeKungalAdapter.cs:40-48,80-107` | A | 5 分钟详情短缓存，动机是「校验式拉详情 + 取数」重复拉 1MB |
| `NextMoeKungalAdapter.cs:89-99` | A | web 主源 / API 回退的隐式降级（数据源应由「要哪些字段」显式决定） |
| `NextMoeKungalAdapter.cs:122-127` + `NextMoeClient.cs:169-172` | A | 单品角色接口**无调用方**（只用批量） |
| `NextMoeKungalAdapter.cs:155-175,224-229` | A | `ToCard` 两重载重复；`ExternalRatings` 映射无人读 |
| `KungalWebClient.cs`+`KungalWebMapper.cs`（482 行） | 大部分 A | 唯一不可替代产出是 `game_types`；白名单+记忆化+MaxDepth 是 6.9GB 事故后的护栏（注释自陈） |
| `NextMoeModels.cs:233-239,180,156-161,94-109,206-207` | A | `CatalogStats`/`Playtimes`/`Alias` 未引用；`Missing` 未读（**ids 批量缺哪个 id 是静默的**） |
| `BgmClient.cs:164-199,201-225,48-55` | A | 两个角色方法 + `Infobox`（从未读）只服务中文名装置 |
| `BgmClient.cs:110-138` | A/B | v0 对 R18 404 的搜索绕行；有 ratings 后可删 |
| `VndbClient.cs`（91 行） | A | 只为评分；`Token` 从未注入；`÷10` 是 VNDB 0~100 适配 |
| `KungalPhraser.cs:114-205` | **B** | `BuildResult` 字段保护（无条件覆盖字段必须回传、Tags 只能并集）——宿主合并契约 |
| `KungalPhraser.cs:208-235,602-665,667-684,575-592` | **B** | 拆票、角色锚点匹配、外链 id 解析、简介语言优先级 |
| `GalgameClassifier.cs`（453 行） | **B** | 阈值/词表/多源融合权重是真判断；旧规则 fallback 是「无数据源」兜底 |
| `KungalOpenHelper.cs`（364 行） | C | 反射宿主导航/视觉树/卸载反注册 |
| `SearchHelper.cs:22-39`、`NextMoeAuth` 全 | C | 反射宿主 `ApplySearchKey`；环回回调/DPAPI |

### 4.2 评分与持久化

| 位置 | 类别 | 判断 |
|---|---|---|
| `Plugin_RightPanel.cs:217-239,283-299,89-94,171-177` | A | token→v0→搜索三段回退、关键词生成、缺 bgm 补拉、VNDB 消费 |
| `WeightedScoreHelper.cs:15` | A | 签名即「两源」常量；单源退化是特例分支(:20-21) |
| `PluginData.cs:126-140` | A | `RatingData` 只有两槽，用 `BgmScore<=0` 当「缺失」魔法值 |
| `HostServices.cs:153-179` | A | 4 跳反射只为拿 bgm 分（`RP:222` 是唯一消费点） |
| `Plugin_RightPanel.cs:260-277` + `SortPage:1290` | A | id 来源三套写法（`Ids[0]`/`Ids[1]`/`(int)RssType.Bangumi`） |
| `WeightedScoreHelper.cs:10` | A | XML 注释仍称数据源是 `KungalData.external_ratings`，与实现脱节 |
| `WSD:23-25`、`PD:138-139` | **B** | √n 权重的隐含前提（同刻度）与缓存失效语义 |
| `WSD:30-35`、`SP:234-235` | **B** | 派生分未物化，排序每次重算开方 |
| `PluginDataIoHelper.cs:47,88` | C→**缺陷** | `data = Plugin.Data` 直接序列化使缓存形状成为对外格式；`:88` 硬等值校验见 §7 |

### 4.3 UI 层

| 位置 | 类别 | 判断 |
|---|---|---|
| `SP:1125-1580`（`BatchScrape_Click` 217 行 + `BatchRating_Click` 115 行） | A 为主 | 一个按钮里塞 4 件事（bgm tag 投票/kungal 抓取/角色与图片/缓存写回）；两个批量约 60 行近似重复 |
| `SP:1052-1123`、`SP:1448-1494`、`SP:956-962` | A | 导入后手写 8 步重放；「缓存」概念（增量/全量对话框、音量记录清理）漏进页面 |
| `SortPage.xaml:222-248` + `SP:927-953` | A/结构 | 音量规范化占菜单 3 个入口 + 30 行，与排序筛选无关（页面菜单当全局设置面板） |
| `SP:132-273,390-401,636-681,728-773` | **B** | 多级排序 + 四轴 AND 筛选 + 统计口径（不可压缩的语义） |
| `SP:441-575`（≈130 行） | **B** | 搜索框原生观感全链（展开/收起/焦点/点空白） |
| `SP:150-169,337-347,684-693,833-854,1596-1621`（≈80 行） | C | `ElementName` 失效 → 菜单选中态手工重放（6 份清单） |
| `SP:52-56,100-115,132-147,1181-1196,1590-1593,1677-1695` | C | 宿主事件跨线程、比较器方向、`Clear→None` 顺序、延迟导航——**红线** |

### 4.4 宿主交互与运行时功能

| 位置 | 类别 | 判断 |
|---|---|---|
| `HostServices.cs` 全文 + `LaunchGuardHelper:108-117` + `VolumeNormalizer:178-187` + `KungalOpenHelper:330-342` | C | **4 个独立反射入口**（后三处各抄了一份 `App.GetService<T>`） |
| `HostServices.cs:142-145,183-186,225-228,249-252,265-268,295-298,319-322,335-338,357-360,385-388` | C 缺陷 | **10 处静默 `catch{}`**；唯一有反射日志的是最 hack 的 `ClearLastError` |
| `HostServices.cs:345-361,370-389` | A | 同一属性反射两次，且额外硬编码宿主 Serilog 格式 `Logs\log{yyyyMMdd}.txt`（最脆耦合） |
| `HostServices.cs:22` | A | `PvnExceptionTypeName` **死反射项**（无引用） |
| `LaunchGuardHelper` 双通道活性/安静宽限/Steam 排除/LastKnown/移库复核 | **A 但有效** | 经宿主源码核实（`LastPlayTime=DateTime.Now`、`TotalPlayTime` 每分钟 +1、Stop 消息时机）**全部仍在解决真实问题**，不是过度设计 |
| `VolumeNormalizer:41,204-236,278-282,392-398` | **A 但有效** | InFlight 去重、路径变化重压、旧记录补压 |
| `VolumeNormalizer:497-599`（COM） | **B 红线** | IID/vtable 槽位/RCW 纪律；本次已复核 IID `E2F5BB11-…` 与宿主权威实现一致 |
| `HostServices.cs:120-128` | A 最脏 | `ClearLastError` 按字符串白名单识别宿主崩溃文案（宿主改文案即失效，且有掩盖新崩溃风险） |

## 5. 本轮新增的实测事实（决定方案可行性）

| # | 事实 | 影响 |
|---|---|---|
| 1 | **`refs=` 只解析 VN id（`v…`）与 bangumi 数字 id；喂它自己的 release id（`r66280`）进 `missing[]`**（实测：`refs=vndb:r66280`→items=0；`refs=vndb:v37651`→命中） | 插件的 `vndb:vNNN` 用法**正确**；`refs[]` 里的 `r…` 绝不能回喂查询。`^v\d+$` 过滤虽丢了 `VndbId`，但也避免了必然 missing |
| 2 | **列表面可内联 `ratings`**：`works?ids=351,49,756&include=ratings` 一次返回 3 部各 5 个源；`refs=` 批量同样可带 | **批量算分从 N×2 请求压到 1 请求/100 部** |
| 3 | **`include=ratings` 匿名 → 401**（`missing-credential`）；带密钥 200 | 评分换源后**依赖登录态**（现状：未登录也能出 vndb 分）→ 产品决策点 |
| 4 | **刻度一致**：NextMoe 的 vndb 分 = 官方 rating ÷10（8 项 6 项精确一致；2 项差 0.01–0.02，且其票数偏旧 779 vs 782、286 vs 299） | 换算就是 ÷10；迁移验收阈值取 `≤0.02` 并把差异归因于快照新旧 |
| 5 | 评分源覆盖（本机 10 款）：bangumi 9、vndb 8、erogamescape 8、nextmoe 4、HLTB 4、dlsite 2；**1 款仅单源、1 款零源** | 并源能显著增强稳健性；同时暴露「零源」需要 UI 语义 |
| 6 | 角色中文名：126 个角色中 **41 个（33%）有真译名**、68 个（54%）zh-Hans 等于原名、17 个无 zh-Hans；45 个「有锚点且 zh==原名」候选在 bgm 上**0 个另有译名**；正对照 10/10 与 bgm 逐字一致 | 「有链接信 NextMoe」**不丢译名**，该规则保留 |
| 7 | 规则②（bgm 游戏级兜底）在唯一有抓取记录的运行里 **10 次抓取命中 0**；独立复核同一 subject 的 26 个角色页只有 1 页有中文名（且是「娘→女儿」）；40/126 角色无 bangumi 锚点 | 收益极低、成本固定（~2s + 宿主 token）→ 建议**先量化再退休** |
| 8 | `game_types` 在 NextMoe **无对应字段**；tag `spoiler` 只在 VNDB 派生词表 | kungal 网页 SSR **不可整体删除**，只能缩到「只取 game_types（+spoiler）」 |
| 9 | kungal **直连已不通**（TLS 超时 20s+），系统代理 1.9–2.3s；NextMoe 直连可用但比代理慢 3~4 倍且抖动 | 两个客户端今日已改为跟随系统代理；批量场景必须走代理 |
| 10 | `IAudioSessionEnumerator` IID 与宿主权威实现逐字一致；音量记录三处写入均为整体替换（无 in-place） | 子代理 D 的两个不确定点解除，无静默丢失风险 |
| 11 | 宿主 `PhrasedEvent` 是**字段式事件**（`public event Action?`，`:40`）→ `GetField` 当前成立 | 反射可用；但宿主改成自定义 `add/remove` 即静默失效 |

## 6. 目标架构（建议）

```
┌ 上游 ────────────────────────────────────────────────┐
│ NextMoeClient（唯一主上游）                            │
│  · 令牌桶/窗口限速 + 429/RateLimit-Policy 退避          │
│  · problem+json 统一错误 → 带 request_id 的日志         │
│  · refs= / ids= 批量车道（≤100）+ missing[] 显式处理    │
│  · ETag/If-None-Match + /catalog/changes 增量           │
└──────────────────────────────────────────────────────┘
        │ （补充源）KungalGameTypesReader（~40 行）
        │   只抓 api:work-ratings:{gid} → game_types + tag spoiler
        ▼
┌ 领域模型（源无关，替代 Kungal* DTO）──────────────────┐
│ WorkInfo { Id, Titles[], SourceIds{source→id}, Intros[],│
│            Tags[{name,source,tier,spoiler,isSexual,count}],│
│            Characters[], Companies[], Engines[], Links[] }│
│ RatingSet { Sources[{source, raw, scale, score10, votes, rank}], │
│             FetchedAt, DataVersion, IdsUsed }             │
└──────────────────────────────────────────────────────┘
        │
┌ 持久化（合并现 KungalData + BgmData + RatingCache）────┐
│ WorkCache[uuid] = { 搜刮结果 + 分类信号 + RatingSet }    │
│   · DataVersion：归一化/公式版本（改算法不必清缓存）      │
│   · IdsUsed：id 变了自动失效（替代「记得取消跳过缓存」）  │
│   · ShouldRefresh(entry)：TTL 唯一判定点（正 30 天/负 7 天）│
└──────────────────────────────────────────────────────┘
        │
┌ UI 层 ────────────────────────────────────────────────┐
│ SortPageState + ApplyAll()（唯一刷新入口）              │
│ BatchJob（统一进度/互斥/finally）                       │
│ LibraryFilter / LibrarySortDescriptors / LibraryStats（纯类，可单测）│
└──────────────────────────────────────────────────────┘
        │
┌ 宿主交互 ─────────────────────────────────────────────┐
│ HostReflection（4 处副本收敛为 1）+ 失败必留日志        │
└──────────────────────────────────────────────────────┘
```

三条设计取向：

1. **数据源选择应由「要哪些字段」显式决定**，而不是现在的「先试网页、失败回退 API」隐式降级（`adapter:89-99`）。字段级来源表：`game_types`/`spoiler` → kungal；其余 → NextMoe。
2. **刻度换算属于源适配层**（`scale` 是源的固有属性），加权层只吃 `score10`；卡片显示原始分（用户认「dlsite 4.82」）。跨源比较只在归一后做。
3. **缓存存原始多源分值 + 元数据，绝不存算好的结果**；派生分按 `(uuid, DataVersion, sources)` 在内存物化，让排序比较变字典查表。

## 7. 分阶段重构顺序

**阶段 0 · 零风险清理**（可立即做，纯删）
- 删 `KungalClient.cs:12-68`，`HtmlToText` 移出（→ 若简介是纯文本可退化为 `WebUtility.HtmlDecode` 并移除 AngleSharp 依赖）
- 删 `KungalEnvelope`、`ExternalRatings` 链路（`KungalModels:49,60-69` + `adapter:224-229` + `mapper:53,148-159`）、`KungalGameData.AvgRating`、`KungalRating.Overall`、`KungalSearchData.Total`
- 删无调用方的 `NextMoeKungalAdapter:122-127` + `NextMoeClient:169-172`，合并 `ToCard` 两个重载
- 修两个既有缺陷：`SortPage.xaml.cs:752` 用错常量；`PluginDataIoHelper.cs:88` 硬等值校验（改 `<=` + 显式 1→2 迁移，否则以后升 schema 会让**所有老备份整体拒收**）
- 预期：**−250 行**、去掉 1 个 NuGet 依赖、2 个 bug

**阶段 1 · 用实测把方案钉死**（本次已完成大半）
- ✅ refs 形式、列表面 ratings、匿名 401、刻度一致、中文名覆盖率、规则②产出、kungal 需代理
- 待补：`localized`/`aliases` 对「无外链角色」的覆盖率（决定提案「删中文名装置」能否全量）；简介是否纯文本；`/catalog/changes` 增量是否可用于缓存失效

**阶段 2 · 匹配层收敛**（收益明确、风险低）
- `MatchGidAsync` 三层 → 两层：`refs=` 反查命中即采信（1 请求）→ 名称搜索兜底
- 删候选详情校验循环（`KungalPhraser:727-743`）与 `VndbIdVariants` 双变体（`:804-813`），因此可删 adapter 详情短缓存（`:40-48,80-107`）
- 验收：一款有 bangumi+vndb id 的老游戏 + 一款只有名字的新游戏，含 R18 与剧透角色

**阶段 3 · 详情源主次反转**（有前置条件）
- NextMoe 详情为**主源**，kungal 网页只取 `game_types`（+tag spoiler）——`KungalWebClient`+`Mapper` 482 行可缩到 ~40 行读取器
- **前置**：`game_types` 必须有替代信号，或明确接受「分类第③步只对已抓网页的游戏生效」

**阶段 4 · 评分换源**（收益最大，须先定产品口径）
- `include=ratings` 一次拿全源（含 `vote_count`/`rank`/`distribution`），删除 `VndbClient`、`BgmClient` 评分方法、宿主 token 反射链
- 公式改「可用源上的广义加权平均」（单源自然退化）；权重策略必须定稿并写进常量 + `DataVersion`
- **必须同时做**：TTL（正/负缓存）+ `IdsUsed` + 「清理评分缓存」菜单 —— 否则沿用现状的「负缓存终身不失效」
- 验收：10 款语料逐源 diff，阈值 `≤0.02`（因快照新旧）；bangumi 侧旧值可能来自 v1 搜索命中的**另一个条目**，此类差异应判为「旧 bug 修复」

**阶段 5 · 角色中文名装置退役**（依赖阶段 1 的覆盖率实测）
- 取 `localized["zh-Hans"]`，缺失回退 `aliases[alias_kind=translation]`；保留「zh == display_name 判等」（B 类，必须留）
- 删 `KungalPhraser:296-455` + adapter:268-313 的一半 + `BgmClient` 角色方法（约 −230 行）
- 规则②：**先量化再退休**（跑一轮全库批量统计 `命中/抓取`），不要凭本次样本直接删

**阶段 6 · UI 层拆分**（与数据源解耦，可独立进行）
- `SortPageState` + `ApplyAll()` 单点刷新（把 4 套实现收成 1）→ 消掉 `RemoveGame_Click` 漏 Filter 这类一致性 bug
- `BatchJob` 统一批量协议（删 ~60–80 行重复）
- 抽 `LibraryFilter`/`LibrarySortDescriptors`/`LibraryStats` 纯类（约 250 行移出 code-behind，首次可单测）
- 拆 partial 文件（`SortPage.Sort.cs`/`.Batch.cs`/`.Flyout.cs`/`.Tools.cs`）——**不要拆成多个 XAML**
- 修 `_restoringSearch` 一次性标记导致「第一次真实输入被跳过刷新」的交互 bug

**阶段 7 · 命名收口**（最后做，只重命名，勿与逻辑改动混在同一次 diff）
- `ParserName`、日志文案、`Kungal*` 类型名统一为 Work/NextMoe 语义，消除「日志说 kungal、实际走 NextMoe」的排障误导

## 8. 风险红线（绝不能"顺手优化"）

1. **COM IID / vtable 槽位**（`VolumeNormalizer:497-599`）：`NotImpl1/2` 看着像死代码，删掉即槽位错位。
2. **RCW 释放纪律**（`:347-350,359-365,412-425,476-484`）：禁止改 `using`、禁止把 `out IAudioSessionManager2` 改成 `out object` 再 cast。
3. **宿主字段保护契约**（`KungalPhraser:114-205`）：`Rating/ReleaseDate/ChineseName/OriginalName/Characters/ImageUrl` 被宿主**无条件覆盖**，必须回传现值；Tags 只能并集。
4. **宿主游玩语义只能依赖不能重写**（`LastPlayTime=DateTime.Now`、`TotalPlayTime` 每分钟 +1、Stop 消息时机）。
5. **反射参数个数/顺序**：反射不套用默认值（`RemoveGalgame(game,false)`、`GameParseType.All=int.MaxValue`）。
6. **UI 红线 9 处**（`SortPage:52-56,100-115,132-147,1181-1196,1590-1593,1677-1695` 等）：比较器方向、`Clear→None` 顺序、延迟导航、`Click/Checked` 而非绑定。
7. **`ClearLastError` 的字符串白名单**（`HostServices:120-128`）：放宽成「清空」会掩盖真实新崩溃。
8. **`IdForPlugins` 不能丢**（`KungalPhraser:107` 写、`KungalOpenHelper:219-226` 读）；`ENTITY_MERGED`/`redirects` 目前未处理——合并后旧 gid 会 404，而外链仍拼 `kungal.com/galgame/{gid}`。
9. **`KungalGameData`/`TypeVotes` 牵动三方**（`GalgameClassifier:57/64/74`、`SortPage:1352-1356`、`Plugin_RightPanel:262`）：字段只能加，不能随意删/改名。

## 9. 待你拍板的产品问题

1. **`game_types`（分类第③步）要不要保**？保 → 保留 kungal 网页路径（每款 1 次 1MB 抓取）；不保 → 可删 482 行，但分类对新游戏只剩 tag 热度 + 旧规则。
2. **评分换源后必须登录**（匿名 401）：接受「未登录就没有评分」，还是保留 VNDB 直连作匿名降级？（保留则阶段 4 的删除收益大打折扣）
3. **权重策略**：√n 在 6 源下会让 bangumi(1056)/vndb(779) 压倒 nextmoe(11)/HLTB(3)。建议 `w=√(min(votes,cap))` 或「固定源权重 × 置信度收缩」，需定稿并版本化。
4. **时长源策略**：`playtimes` 源间差异可达 3 倍（vndb 3382 / erogamescape 2400 / HLTB 1110 min）。要不要接进「预计时长」（**注意：插件目前从不写宿主 `ExpectedPlayTime`，这是新功能而非替代**），取哪个源/中位/区间？
5. **是否保留 VNDB+Bangumi 直连作为可配置 fallback**（决定 A 类第 1/2/3/6 项能否真正删除）。

## 10. 附录：实测脚本与数据（仓库外）

全部在 `D:\_Deepseek harness\_scratch\`（**不在 git 仓库内**，不会污染提交）：
`nextmoe_probe.py`（字段形状）、`coverage.py`→`coverage-report.md`（10 款覆盖率）、`bgm_compare.py`→`bgm-compare.md`（中文名对照）、`no_bgm_anchor.py`→`no-bgm-anchor.md`（锚点分布）、`rule2_yield.py`（规则②产出）、`rating_verify.py`（匿名 401 + 刻度对拍）、`refs_form_test.py`（refs 形式 + 列表面 ratings + 批量车道）、`check.py`/`reach.py`/`latency.py`（连通性与延迟）。

密钥读取自 `C:\Users\hansnmor\OneDrive\_hansnmor\nextmoe密钥.txt`，只在脚本变量内使用，未落盘、未写入仓库。
