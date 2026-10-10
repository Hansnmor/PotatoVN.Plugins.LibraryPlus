# LibraryPlus 插件接手开发指南（HANDOFF）

> 本文档面向**新接手的 AI**：读这一篇就能一条龙走完「理解项目 → 开发 → 构建调试 → 提交 → 发布」。
> 仓库：https://github.com/Hansnmor/PotatoVN.Plugins.LibraryPlus （分支 main）
>
> **版本号以 `PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj` 的 `<Version>` 为唯一权威**（git tag 与之对齐）。
> 本文档**不复写「当前版本」**——历史教训：分散记录必然漂移（曾同时出现「文档头部 v1.4.6 / §3 标题 v1.4.0 / csproj 1.5.0」）。
> §3 各条末尾的 `（vX.Y.Z）` 是该功能**落地版本**的历史事实，可以保留；「当前版本」一律看 csproj。
> 同理，**每条会漂移的环境或接口结论都带核对日期**；结论被推翻时不要删，移到文末附录（保留"当初为什么这么判断"）。
>
> **文档地图**：
> - `doc/HANDOFF.md`（本篇）= 上手路线 · 项目结构 · 构建/提交/发布流程 · 开发红线
> - `doc/v1.5-nextmoe-migration.md` = 数据源从 kungal API 迁到「kungal 网页 SSR + NextMoe API」的变更说明
> - `doc/PLAN-kungal-data-source.md` = kungal 数据源/评分融合的原始设计
> - `doc/search-issue.md` = 搜索框专项排查史（**只在要动搜索框时读**，§8.5 是定稿）
> - `doc/FEATURE-REVIEW-nextmoe.md` = 16 项功能「能否用 NextMoe 新办法实现」的评估（人话版）
> - `doc/REFACTOR-nextmoe-era.md` = 工程版重构评估（逐文件分类 · 目标架构 · 分阶段顺序 · 红线）
> - 官方脚手架文档：`doc/main.md`（必读第一份）及 `doc/ui.md` / `sidebar.md` / `dialog.md` / `data.md` / `parser.md`
> - 文末**附录：已失效的历史结论** = 仅作考古，不要照抄

---

## 1. 从哪里开始读（阅读顺序）

1. **`doc/main.md`（必读第一份）**：官方脚手架文档，明确写给 AI agent 看。讲清插件架构：`GalgameManager.WinApp.Base` 公开库（submodule 引入，**禁止修改**）、插件主类 `IPlugin`、HostApi（`IPotatoVnApi`）的获取与使用、各功能接口的注入方式。
2. **按功能需求查官方子文档**（都在 `doc/` 下）：
   - `doc/ui.md` — 自定义 UI（页面/面板/卡片注入）
   - `doc/sidebar.md` — 侧边栏按钮
   - `doc/dialog.md` — 对话框
   - `doc/data.md` — 插件数据持久化
   - `doc/parser.md` — 搜刮器（IParserProvider）
3. **本仓库自有设计文档**（与官方文档同目录，按需读）：
   - `doc/v1.5-nextmoe-migration.md` — 数据源从 kungal HTTP API 迁到「kungal 网页 SSR + NextMoe API」的变更说明
   - `doc/PLAN-kungal-data-source.md` — kungal 数据源/评分融合的原始设计
   - `doc/search-issue.md` — 搜索框完整排查史（**只在要动搜索框时读**，§8.5 是最终形态定稿）
   - `doc/FEATURE-REVIEW-nextmoe.md` / `doc/REFACTOR-nextmoe-era.md` — 「哪些功能能用 NextMoe 新办法做」的评估（人话版 / 工程版），评估结论未落地前先读这两份再动手
   - 文末**附录：已失效的历史结论** — 已被推翻的结论，仅作考古
4. **宿主源码**：`PotatoVN/GalgameManager`（submodule，只读参考；内部实现细节、反编译资料见宿主仓库文档 `PotatoVN/.kilocode/rules/project-info-galgamemanager.md`）。

> 理解宿主 View 层结构（详情页、主页搜索框等）时，可参考本机反编译资料 `E:\_Code\_hansnmor\_WORKSPACE\deepseek harness\_workspace\_decompile\`（搜索定位用，非仓库内容）。

---

## 2. 项目结构

```
仓库根
├── PotatoVN/                      # 宿主源码 submodule（只读！不得修改、不得提交其内部改动）
├── PotatoVN.App.PluginBase/       # ★ 插件本体（所有开发工作在这里）
│   ├── Plugin.cs                  # 插件主类：IPlugin + IParserProvider + IGalgamePageRightPanel
│   ├── Plugin_Ui.cs               # 侧边栏按钮「扩展库」→ 导航到 SortPage
│   ├── Plugin_RightPanel.cs       # 详情页右侧「综合评分」卡片注入（置顶移动逻辑）
│   ├── HostServices.cs            # 反射宿主 API 的封装（读宿主 DLL 内部类型/方法）
│   ├── SidebarSelectionHelper.cs  # 侧边栏选中指示器管理
│   ├── Controls/
│   │   ├── SortPage.xaml(.cs)     # ★ 扩展库页：排序/筛选/搜索/多选/批量按钮/统计
│   │   ├── Prefabs/               # 官方脚手架预设 UI 控件（一般不动）
│   │   └── Styles/                # 主题资源（FontSizes/TextBlock/Thickness）
│   ├── Helper/
│   │   ├── Kungal/                # kungal 搜刮器（KungalClient/KungalPhraser/KungalModels/KungalOpenHelper）
│   │   ├── Vndb/VndbClient.cs     # VNDB 官方 API（kana/vn，匿名 POST，rating 0-100 → ÷10）
│   │   ├── Bangumi/BgmClient.cs   # Bangumi v0/v1 API（v1 search 可匿名拿 R18，v0 byId 匿名 404）
│   │   ├── WeightedScoreHelper.cs # 加权评分计算（√n 权重）
│   │   ├── RatingCachePolicy.cs   # 评分缓存时效（有分 30 天 / 无分 7 天）+ 单款/批量删除
│   │   ├── RatingAutoFetcher.cs   # 新游戏入库自动采集评分（挂宿主 Added 事件，见 §3.11）
│   │   ├── SearchHelper.cs        # 搜索谓词（反射宿主 ApplySearchKey + 本地降级）
│   │   ├── GalgameClassifier.cs   # 内容分类（萌作/剧情作/拔作/其他）与形态分类
│   │   └── ExpectedPlayTimeHelper.cs # 预计时长估算
│   └── Models/PluginData.cs       # 插件持久化数据（RatingCache 等，经 LiteDB）
├── doc/                           # 文档（见 §1）
├── tools/                         # 开发辅助脚本（Python 探测/分析脚本）
└── .github/                       # 仅 copilot 指令，无 CI
```

---

## 3. 已开发功能总结

> 按功能域排列（**不按版本堆叠**——逐版本变更看 `git log` 与 GitHub release notes）；每条只写「是什么 · 入口在哪 · 数据来自哪」，实现细节见代码注释与对应文档。条末 `（vX.Y.Z）` 是落地版本。

### 3.1 Kungal 搜刮器（IParserProvider）
- `ParserId = 921470`；从 kungal.com 搜刮游戏信息（中文简介、标签、角色、评分等）。
- kungal 的 gid 存于 `Galgame.IdForPlugins[921470]`，用于后续功能（Kungal 打开、评分关联）。

### 3.2 扩展库页（SortPage，侧边栏「扩展库」进入）
- **多级排序**：主键 + 次键，可选 默认顺序 / 预计时长 / 游玩时间 / 游玩次数 / 我的评分 / **加权评分**，各自可降序；只作用于本页，不影响原生页面。
- **筛选**：状态（游玩中/玩过/搁置/抛弃/想玩/未标记）、类型分类（萌作/剧情作/拔作/其他）、形态（传统 ADV / 非传统 ADV）、时长区间（<10h / 10-20h / 20-40h / >40h / 未知），全部 AND 联动。
- **搜索**：工具栏「搜索」按钮展开输入框，实时过滤（名称/中文名/原名/开发商/标签），点页面空白自动收起，支持 Ctrl+F；原生观感（紧凑高度、× 清除按钮）。
- **多选模式**：勾选游戏后，批量操作只作用于选中项。
- **批量搜刮**：对当前筛选（或选中）游戏批量 kungal 搜刮。
- **批量计算评分**：批量拉取 bangumi/vndb 官方评分并生成加权综合评分；有确认对话框（可跳过已缓存）；与「批量搜刮」按钮互斥；完成后自动退出多选。

### 3.3 加权综合评分
- 数据源：bangumi 官方评分 + vndb 官方评分（独立于 kungal，直接打官方 API）。
- 公式：`final = (bangumi×√n_b + vndb×√n_v) / (√n_b + √n_v)`，保留 2 位小数；只有单一来源时直接显示该来源评分并标注来源。
- 展示：详情页右侧面板顶部卡片（综合评分 + 分项明细）；排序键「按加权评分」。
- 缓存：`PluginData.RatingCache`（LiteDB 持久化），批量计算时已缓存的条目可跳过。

### 3.4 在 Kungal 中打开
- 详情页「外部网站」菜单注入「在 Kungal 中打开」入口（仅对 kungal 搜刮过的游戏，有 gid 才显示）。

### 3.5 游玩记录：启动守卫 + 清除工具（v1.4.2）
- **启动守卫**（`Helper/LaunchGuardHelper.cs`，默认关闭、阈值默认 5 分钟）：防「点开测试几秒」的游戏顶到原生主页「最后游玩」排序最前。判定链：宿主 Messenger 钩子（`GalgamePlayedMessage`/`GalgameStoppedMessage`，WeakReferenceMessenger，挂载前校验与宿主同一实例）→ 收到停止消息立即结算；兜底为 30 秒轮询双通道活性（`TotalPlayTime` 每分钟滴答 + 进程探测）+ 2 分钟安静宽限期（宿主源码实证：进程探测不可靠，未配置 ProcessName/exe 改名时永远探不到）。**累计总时长 ≥ 阈值的游戏完全豁免**（老游戏回访不受影响）。试玩仅还原 `LastPlayTime` 时间戳并弹 InfoBar 说明，不删任何游玩时长。Steam 源不守卫。
- **清除游玩记录**（SortPage「更多」菜单）：清空勾选/筛选集的 PlayedTime 明细 + 累计时长 + 上次游玩时间，可选连 PlayCount；确认框警示不可逆与 Steam 覆盖/云同步合并风险；完成自动退出多选。
- 宿主关键事实（排障必读）：启动游戏时宿主无条件 `LastPlayTime = DateTime.Now`；`TotalPlayTime/LastPlayTime` 由 `PlayedTime` 字典派生（`MergeTime` 为 max 合并**只增不减**）；「重启后删除的记录复活」渠道 = PVN 云同步 pull（编辑游玩时长页的保存**无条件**触发同步任务，不检查同步开关）/ Steam 刷新覆盖 / `.PotatoVN\meta.json` 备份重新入库合并。

### 3.6 插件数据导出/导入（v1.4.2）
- `Helper/PluginDataIoHelper.cs`：PluginData 整体备份为带标识头的 JSON（app 标记 + schema 版本 + 时间戳），含页面设置、手动分类/形态覆盖、搜刮与评分缓存、守卫配置。导入校验标识与版本（不符明确拒绝），确认后 `Plugin.ReplaceData` 热替换并持久化，页面状态即时重放刷新。
- 动机：卸载勾选「删除数据」= 直接删 `plugin_data` 集合中本插件 Guid 那条记录，不可恢复。

### 3.7 非本地游戏过滤（「更多」菜单显示开关，v1.4.3）
- **「显示非本地游戏」**（`PluginData.DisplayVirtualGame`，默认关）：非本地游戏 = 库里有条目但本机无任何
  本地文件夹/Steam 源（`Galgame.IsLocalGame == false`，宿主称「虚拟游戏」）——云同步换机后只恢复元数据的
  记录即属此类。默认隐藏，与原生游戏页 `VirtualGameFilter` 默认行为对齐。
- 统一口径：`SortPage.VirtualGameVisible()` 同时用于 `FilterGame` 与完成度统计——口径不一致会出现
  「待玩总时长跟着开关变、完成度不变」。完成度原有语义保留（从未过滤源出发，绕开状态筛选与搜索词），
  只是叠加了本开关。
- 原「记录」按钮改名「更多」（`MoreToolButton`，菜单 Opening 处理器同步改名 `MoreMenu_Opening`），
  收纳显示开关、游玩记录工具与插件数据备份。

### 3.8 音量规范化（v1.4.5，默认关闭，默认档位 30%）
- **动机**：galgame 大多默认音量过大。功能开启后，在每款游戏**首次启动**时用 Windows Core Audio
  把该游戏进程的「应用会话音量」压到设定档位（纯系统级、可逆、零外部依赖，不改游戏内部音量）。
- **触发**：宿主「开始游玩」消息（`GalgamePlayedMessage`，与守卫同款 Messenger 校验挂载）为主动；
  兜底 `GalPropertyChanged(LastPlayTime)` 跳变（≤2min 近期判定 + InFlight 去重，两条路径并发只跑一路）。
- **进程匹配**：优先 `Galgame.ProcessName` → 回退 `ExePath` 文件名（宿主 `TryGetProcessFromName` 同款）；
  **bat/启动器场景兜底**：名称未命中时，凡「exe 真实路径在游戏目录（`LocalPath`）内 + 进程启动晚于本次启动」
  的会话都判定为该游戏进程（覆盖 bat → 唤起真正游戏进程、多层 fork 场景）。
- **「首次」判定**：`PlayCount==0 && TotalPlayTime<5`（对齐宿主 `MinPlayTimeRecordThreshold` 默认 5 分钟）
  才自动压；**已经玩过的游戏不自动压**（尊重用户手动调整）。
- **仅首次 + 移动检测**：成功后记录 uuid + 当时进程 exe 路径（`VolumeNormalizedGames`/`VolumeNormalizedPaths`）。
  之后启动短窗（8s）比对：路径一致 → 跳过；路径变化（移动位置后 Windows 把该进程音量重置回 100%）→
  重新压一次并更新记录。旧版记录（无路径）下次启动补压一次记录路径。
- **UI**：「更多」菜单 = 开关 + 档位（10%-100% 每 10% 一档，默认 30%）+ 全量清空记录；
  游戏封面右键菜单 = 「清空音量规范化记录」（只清当前游戏，下次启动重新压）。
- 结果 InfoBar：成功（含"检测到路径变化"）、无可用进程/目录、30 秒未找到会话；其余内部诊断走 Log。

### 3.9 角色中文名修复 + 日志功能（v1.4.6）
- **角色中文名失效根因**：kungal 把角色详情 `name` 字段从日文名改为简体中文名后，旧代码
  `IsJapaneseName(kc.Name)` 判定恒 false → bgm 抓取被整体跳过且静默无声（详见 §5 第 11 条）。
- **修复口径（用户拍板）**：角色**有 bgm 链接** → 直接信 kungal 自带中文名（零网络，镜像 bgm）；
  角色**无 bgm 链接** → **以 bangumi 为准**：用游戏 `Ids[Bangumi]` subject id 带 token 调
  `/v0/subjects/{id}/characters` 拿全角色，kungal 日文原名匹配（精确 + 相似度唯一兜底，吸收埼/崎异体字）
  → 抓 `bgm.tv/character/{id}` 网页取「简体中文名」，可覆盖 kungal 错误译名（如「神埼树」→「神崎五月」）。
- **带 token**：R18 条目 `/v0/subjects` 匿名 404，`BgmClient.Token = HostServices.GetBgmTokenAsync()`。
- **诊断日志**：搜刮时打「角色中文名·bgm兜底」+「角色中文名解析」（每角色来源），宿主日志可核对。
- **扩展库页「更多」→「打开当天日志」**：`HostServices.GetHostDailyLogPath()` 反射宿主
  `AppStoragePaths.LocalDataPath` 拼 `Logs\log{yyyyMMdd}.txt`（Serilog 滚动，Retained 7 天），
  `Process.Start(UseShellExecute)` 按 .txt 默认关联打开。
- **分类日志静默**：`ClassifyContent` 加 `silent` 参数——列表筛选/统计（刷新全库遍历）传 true，
  不再每次点分类刷「分类[拔作]」日志；默认 false 保留诊断（批量搜刮后核对命中路径用）。

### 3.10 网络与缓存健壮性（2026-10-03）

- **修复「搜刮慢」的根因（本轮收益最大）**：`KungalWebClient` 与 `NextMoeClient` 原先都写死
  `UseProxy = false`，但实测 kungal 直连已断（每次干等满 40 秒超时再回退 NextMoe）、NextMoe 直连慢 3~4 倍
  且抖动大 → 两者改为**跟随系统代理**（环境细节见 §5 第 11 条内的「环境」小节）。实测：单款游戏 ~25s → **~4s**；
  11 个角色批量水合 22.3s → **~1.2s**；kungal 网页抓取 40s 超时失败 → **1.9~2.6s 成功**。
- **评分缓存加了有效期**（`Helper/RatingCachePolicy.cs`）：有评分 30 天、「无评分」7 天。缓存命中时先显示旧值
  （不闪、不阻塞），过期则同时后台重拉自愈；批量计算评分改为「只跳过**仍新鲜**的」，不再被「当时没评分」的
  负缓存永久拦住。
- **新增「更多」→「清理评分缓存…」**：补上缺失的用户出口（以前只能靠删插件数据）。
- **导入备份不再被版本号卡死**：原判据是 `schema != SchemaVersion` 硬等值，日后一升版本号**所有老备份会被
  整体拒收**；现改为「只拒收比当前更新的备份」，并兜底补空集合、导入完成时提示实际导入条数。
- **宿主反射失败留日志**：7 处静默 `catch{}` 现在会打一次「宿主接口反射失败: XxxAsync()」（同一处只报一次、
  **不改变任何行为**）——宿主改内部接口时不再出现「功能悄悄消失、日志无痕迹」。
- 其它小修：形态轴改用独立 `FormKeyAll` 常量（原先借用内容轴常量，只是恰好都是 "All"）；
  宿主外部 id 改用 `Ids[(int)RssType.Vndb/Bangumi]` 并加越界检查；NextMoe 角色批量记录 `missing[]`。

### 3.11 新游戏评分自动采集 + 守卫会话判定重写 + 评分缓存清理联动（2026-10-10）

- **新游戏评分自动采集**（`Helper/RatingAutoFetcher.cs` 新增，修「添加游戏后不自动获取评分」）：
  评分此前只有「打开详情页」「手动批量计算」两个触发点，添加一款新游戏若不打开详情页就永远不会拉评分。
  修法挂在**宿主「游戏入库」事件**上：宿主 `AddGameAsync` 的顺序是「解析（写好 Bangumi/VNDB 外部 id）→
  落盘 → 发事件」，所以事件到达时外部 id 已可用，此刻即可拉分（无需等某次搜刮）。
  首版曾挂在「搜刮完成」事件上——时机不对且多绕一圈（实测：新游戏评分是在后来的批量搜刮时才写进缓存，
  而用户期望添加完就有）。触发后无新鲜缓存才后台拉（整体闸门防批量添加并发、失败静默），
  成功打日志 `新游戏评分采集: XXX bgm=… vndb=…`。
- **宿主事件跨代订阅**（`HostServices.SubscribeGalgameAddedCrossGen`，同日实测的硬事实）：
  宿主 **v1.10.2.0 把 `GalgameAddedEvent` 删除**，合并为 `GalgameMutated`（`Changes` 位标志）。
  按旧名 `GetEvent` 返回 null 且**不抛异常** → 静默失效（「新游戏纳入监听」从此消失）。
  新代码订阅新代 `GalgameMutated` 的 `Added` 位（反射构造精确委托，泛型委托**不支持协变**，
  必须 `Delegate.CreateDelegate`），旧宿主回退 `GalgameAddedEvent`。
  ⚠️ **影响评估要克制**：这条失效只影响「插件运行期间新入库游戏」的补挂（守卫/音量的**主触发路径是
  `GalgamePlayedMessage` 的 Messenger 通道，与该事件无关**，见 §3.8/§3.5），用户几乎无感——
  它是「技术债」而非「功能坏了」，别再夸大。
- **启动守卫会话判定重写**（修「玩着却被判试玩还原」，实测日志：观察后 2 分钟即还原，而游戏跑了 44 分钟）：
  - **旧判定的根因**：靠「TotalPlayTime 滴答 + ProcessName 进程探测」推断游戏是否还在运行。
    但 `ProcessName` 是**用户手动指定**的字段，实测 9 款游戏全为 null → 进程探测恒 false（永久失效）；
    滴答每分钟一次、宽限期只有 2 分钟，丢一次就误判「会话结束」。
  - **新判定：问宿主，不猜**。宿主为每次游玩创建 `RecordPlayTimeTask` 后台任务，它存在于
    `BgTaskService.GetBgTasks()` 期间 = 宿主认定游戏在运行；被移除 = 宿主认定已退出。
    `HostServices` 新增 `IsHostRecordingPlayTime`（三态：true/false/**null=查不到**，null 时**绝不结算**）
    与 `GetHostCurrentPlayMinutes`（宿主自己的本次计数 `CurrentPlayTime`，比插件用 TotalPlayTime
    增量推算更准——滴答有 `Thread.Sleep(60s)` 漂移）。结算只在「宿主确认退出」后发生；
    时长优先用宿主计数。旧判定已整体删除（`QuietGrace` 安静宽限不再参与判定）。
  - 验证记录：6 分钟游玩正确放行（滴答达标路径）；2 分钟游玩正确还原（新日志
    `宿主计数=2分钟 滴答增量=2分钟` 证明宿主计数链路可用）。
- **评分缓存清理联动多选**（「更多 → 清理评分缓存…」）：与「批量搜刮 / 计算评分 / 清除游玩记录」对齐——
  多选有勾选 → 只清**选中**的游戏；非多选 → 清**当前筛选**可见的游戏；确认框内勾选「同时清理范围外」
  才整库清空。`RatingCachePolicy` 新增 `Remove(game)` / `Remove(games)` / `HasAny(game)`；
  封面右键新增「删除该游戏的评分缓存」（单款、无确认框）。

---

## 4. 构建与调试

### 4.1 Debug 构建（日常开发用这个）
```powershell
dotnet build PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj -c Debug
```
产物：`PotatoVN.App.PluginBase\bin\Debug\net8.0-windows10.0.22621.0\A70ee3f8a-361a-450a-acff-5371e85808b4.dll`

### 4.2 加载插件（开发者模式）
1. 打开 PotatoVN（当前宿主版本 1.10.2.0+）。
2. 插件管理 → 开发者模式 → 添加插件 → 选择 **`PotatoVN.App.PluginBase\bin\Debug\net8.0-windows10.0.22621.0` 文件夹**（Debug 构建产物目录）。
3. 之后每次重新构建后验证：**完全退出 PotatoVN（含托盘图标）再启动**——插件不支持热重载，热重载会崩溃/不生效。

### 4.3 构建要点（务必知道）
- **命名空间 stamping**：构建时插件命名空间会被改为 `PotatoVN.App.PluginBase.Stamped_<hash>`（防插件间冲突）。若出现诡异的 XAML 解析错误/找不到类型，**删除 `bin` 和 `obj` 目录后重新构建**（增量构建残留会导致 stamp 不一致）。
- **宿主日志收不到 `Debug.WriteLine`**：诊断信息要显示在 UI 上（或让用户截图），不要指望宿主日志文件。
- 构建命令里**不要内嵌中文**（本机 PowerShell 读命令按 GBK，会解析失败）；中文内容一律写文件再传参（如 `git commit -F file`、`curl --data-binary @file`）。

---

## 5. 开发注意事项（经验约束，开发前必读）

1. **隔离红线**：插件只引用 `GalgameManager.WinApp.Base`；宿主 `GalgameManager.dll` 的 View/内部类型只能反射（参考 `HostServices.cs` 的写法）。**永不修改** `PotatoVN/`（宿主）和 `GalgameManager.WinApp.Base`。
2. **XAML 经 XamlReader 加载**（`XamlResourceLocatorFactory.PluginControlInit`），因此：
   - `ElementName` 绑定**不生效**——运行时取值一律代码直接赋值。
   - 控件默认尺寸跟随宿主主题（AppBarButton 高度、AutoSuggestBox 高度等以实测为准，别写死）。
   - `CommandBarLabelPosition` 枚举只有 Default/Collapsed（宿主 WinUI 较旧）。
3. **不能使用 `DispatcherQueue`**（宿主 WinUI 太旧会 MissingMethodException）；跨线程回 UI 用 `Plugin.HostApi.InvokeOnMainThread`。
4. **XAML 中别用 `IsChecked` 字面量**（旧 WinUI XamlParseException）；图标用 `FontIcon Glyph`（Symbol 枚举可能缺成员）。
5. **版本号**：改 `PotatoVN.App.PluginBase.csproj` 的 `<Version>`，与 GitHub tag 对齐。**本文档不复写当前版本号**（以 csproj 为唯一权威，避免多处漂移）。
6. 搜刮器/评分 API 细节（VNDB 匿名 POST、Bangumi v1 匿名可拿 R18、host token 经 `HostServices.GetBgmTokenAsync()`）见对应 Helper 文件头注释。
7. **⚠️ kungal HTTP API 已于 2026-09 全站改为需登录，v1.4.6 的直连方案已不可用（v1.5.0 已迁移，勿回退）**：
   任何匿名 `GET https://www.kungal.com/api/*` 现在都返回 `401 {"code":205,"message":"用户登录失效"}`。
   **已换成「kungal 网站 SSR 抓取（作品详情）+ NextMoe 开放 API v2（搜索/角色详情）」双数据源**，
   详见 `doc/v1.5-nextmoe-migration.md`；v1.4.6 时代的接口字段记录已移到文末附录「已失效的历史结论」。
   **排障提示**：若再遇「所有游戏批量搜刮都未匹配」，优先按新文档检查——
   ① kungal 网站是否改版导致 `__NUXT_DATA__` 解析失败（日志会打「kungal 网页数据不可用，回退 NextMoe API」）；
   ② NextMoe 令牌是否失效（日志会打「nextmoe 未登录」或 401）。
8. **右键菜单触发的导航必须等 `MenuFlyout.Closed` 再执行（v1.4.4，性能红线）**：在 Flyout 的点击处理里（或仅排队一拍）同步执行 `NavigateTo`，页面构建会被卷进菜单关闭动画的病态布局——实测构建耗时从正常的 50-100ms 膨胀到 ~3000ms，且 AppBarButton 本地化文字延迟渲染（宿主 `HomeViewModel.GalFlyOutEdit` 有同款"延迟导航"处理与注释）。正确写法见 `SortPage.EditGame_Click`：记录待办 → 挂一次性 `Closed` 事件 → `Closed` 里再 `InvokeOnMainThread` 导航。左键 `ItemClick` 无此问题，可直接同步导航。
9. **宿主游玩记录数据流事实（排障必读，v1.4.2 起）**：启动游戏时宿主无条件 `LastPlayTime = DateTime.Now`；`TotalPlayTime/LastPlayTime` 由 `PlayedTime` 字典派生，`MergeTime` 是 max 合并**只增不减**；「删除的记录重启后复活」渠道 = PVN 云同步 pull（游玩时长编辑页的保存**无条件**触发同步任务，不检查同步开关）/ Steam 刷新覆盖 / `.PotatoVN\meta.json` 备份重新入库合并。
10. **Core Audio 应用会话音量（v1.4.5，音量规范化用，写错必踩坑）**：
    - 宿主 `GalgameManager/Models/BgTasks/GameMuteTask.cs` 的 `AudioHelper` 是**权威参照**（后台静音功能，已验证可用），接口定义与 IID 照抄它。
    - **`IAudioSessionEnumerator` 的 IID 必须是 `E2F5BB11-0570-40CA-ACDD-3AA01277DEE8`**——写错（如抄成别的）会因列集器按错误 IID 做 QI 而 `GetSessionEnumerator` 出参恒 null、静默失效。
    - COM 接口的 vtable 槽位要占齐：`IMMDeviceEnumerator.GetDefaultAudioEndpoint` 是第 2 槽（前面留 1 个占位）、`IAudioSessionManager2.GetSessionEnumerator` 是第 3 槽（继承自 IAudioSessionManager 的 2 个方法占前 2 槽）；`out` 参数直接声明为目标接口类型可避免 object→cast 产生第二 RCW。
    - **RCW 释放纪律**：所有 `Marshal.ReleaseComObject` 必须 try-catch + 置空（`ReleaseSafe(ref x)`），且在 finally 里抛 `InvalidComObjectException` 会逃过方法内 catch 冒泡到调用方——曾因此「压一次成功却报处理异常」。
    - 触发判定**不要用 `now <= known` 防回退**：消息路+属性路同秒并发会自相矛盾地误拦；改为「LastPlayTime 在最近 2 分钟」+ `InFlight` 去重即可。
    - 匹配进程取 `Process.MainModule?.FileName`（bat 场景记录真实游戏 exe 路径，用于移动检测）；`PlayCount` 只在单次游玩 ≥5 分钟才 +1，故「首次」= `PlayCount==0 && TotalPlayTime<5`。
11. **角色中文名口径与 bgm 抓取定位（角色中文名功能，踩过坑）**：
    > **v1.5.0 起数据来源已变更**：角色中文名/立绘/剧透来自 kungal 网页的 `roster`
    > （`localized.zh-Hans`，实测带 `is_machine:false` 的人工翻译），角色简介与外链来自 NextMoe 批量水合。
    > `/api/galgame-character/{id}` 的字段语义**已失效**，原文见文末附录「已失效的历史结论」；
    > 但"不要用名字是否日文当开关"这条教训**仍然有效**，`KungalPhraser.FetchCharacterIntrosAsync` 的判定逻辑未被改动。

    - **不要把「名字是否日文」当开关**：旧代码用 `IsJapaneseName(kc.Name)` 决定要不要抓 bgm，kungal 把中文名
      填进 `name` 后该判定恒为 false → bgm 抓取被整体跳过、`cn` 恒 null、改名条件不成立，
      **功能静默失效（不报错、不打日志，只能看到"中文名没了"）**。
    - **最终口径（用户拍板，KungalPhraser.FetchCharacterIntrosAsync 内实现）**：
      - 角色**有 bgm 链接** → 直接用 kungal 自带中文名（镜像 bgm，零网络）；仅当 kungal 也没给中文
        （`name` 仍是日文）才抓单个 bgm 角色页补。
      - 角色**无 bgm 链接** → **以 bgm 为准**：用库内游戏 `Ids[Bangumi]` 的 subject id 带 token 调
        `GET /v0/subjects/{id}/characters` **一次拉全该游戏角色 (bgm角色id, bgm角色名)**，拿 kungal 日文原名
        （`name_original`/`name_ja`）在列表里**归一化精确匹配**（`NormalizeName`，不模糊防同名异角色错配）
        → 拿到 bgm 角色 id，再抓 `bgm.tv/character/{id}` 网页取「简体中文名」覆盖 kungal；bgm 无此角色/
        该角色网页无简体中文名 → 退回 kungal 中文名 / 保持原名。
      - **⚠️ 别信 characters API 的 infobox 有简体中文名**：实测 `GET /v0/subjects/{id}/characters` 返回数组，
        每项含 `id`/`name`/`infobox`，但 **infobox 里没有「简体中文名」**（简体中文名只存在于角色网页
        HTML）——我曾误按「infobox 有简体中文名」实现，结果整条游戏级兜底拿不到任何名，角色全退回
        kungal 中文名。正确姿势：API 只拿 (角色id, 角色名) 用于匹配，简体中文名一律对该角色 id 抓网页解析
        （`GetCharacterCnNameAsync`，匿名可访问）。
      - **bgm 必须带 token**：`/v0/subjects` 对 R18 条目（galgame 绝大多数）匿名 404，需宿主 Bangumi
        OAuth token（`HostServices.GetBgmTokenAsync()` → `BgmClient.Token`）才全量可见。逐角色网页抓取
        `bgm.tv/character/{id}` 是 HTML、匿名可访问、不需 token。
      - **纯汉字日文名不含假名**，`IsJapaneseName` 判不出来（如「沢渡真琴」「水瀬名雪」，中文圈库里很常见）。
        判断"库内角色名是否日文形态"统一走 `IsJapaneseLikeName`：含假名 **或** 与 kungal 的
        `name_original`/`name_ja` 完全相同（后者专门覆盖纯汉字场景）。
    - **bgm API 角色数据要点**：`GET /v0/subjects/{id}/characters` 直接返回**数组**（非分页对象），每项含
      `id`(bgm 角色 id)、`name`(主名，多为日文)、`infobox`(key-value 数组，但**不含**简体中文名，别指望它)。
      简体中文名只能抓 `bgm.tv/character/{cid}` 网页。bgm subject 搜索对罗马字/中文标题分词不友好，
      定位条目优先用库里已存的 subject id，别依赖搜索。
    - 本机网络/代理环境已**独立成条**（见本节第 12 条）——kungal / NextMoe / bgm 三家的直连可用性各自变过，
      改动网络相关代码前务必先看那条。
    - **bgm subject 是 R18 时匿名搜索也常查不到**（如轮舞曲Duo 116779）：搜「輪舞曲/Rondo/Duo」等均无结果、
      匿名 `GET /v0/subjects/116779` 返回 404，但网页 `bgm.tv/subject/116779` 是 200——条目真实存在，
      只是被 R18 + 匿名限制隐藏，别据此误判"bgm 没收录"。
    - **⚠️ 对 kungal 结构漂移的鲁棒性边界（用户拍板：维持「有链接信 kungal」）**：本功能对 kungal 有 4 个
      隐性假设，失效后果与发现难度不同——新增诊断日志（搜刮时「角色中文名·bgm兜底」「角色中文名解析」两行）
      就是为这类漂移准备的探针：
      | 假设 | 失效后果 | 发现难度 |
      |---|---|---|
      | `name` 非日文 ⇒ 可信中文名（有链接时直接信） | 若 name 被改成英文等会被**静默写错** | 需对比库内实际值 |
      | `name_original`/`name_ja` 是日文原名 | bgm 匹配不上 → 退回 kungal 名 | 日志可见 |
      | links 的 source 名不变（vndb/bangumi） | 角色被当「无链接」→ 走游戏级兜底 | 日志可见 |
      | 游戏级 `Ids[Bangumi]` 是 subject id | 兜底跳过 | 日志可见 |
      **kungal 又改结构时的快速排查**：tools/ 下跑 `probe_cnname_rate.py`（看 name 是否仍为中文）+ 对比
      `name/name_original/name_ja` 实际取值；然后看宿主日志「角色中文名解析」行判断断在哪一层。

12. **本机网络与代理环境（2026-10-03 实测，旧结论已变，别再照抄）**：
    - **三个客户端一律跟随系统代理，不要写 `UseProxy = false`**。系统代理 = Clash，实测端口是
      **`127.0.0.1:8888`**（旧文档写的 7889 已不通；`ProxyEnable` 在注册表 Internet Settings 里查）。
    - `www.kungal.com`：**直连已在 TLS 握手阶段必超时（20s+）**，必须走代理（代理 1.9~2.3s 返回 200）。
      迁移期「kungal 直连可达」的结论已失效——`KungalWebClient` 因此曾每次干等满 40s 超时再回退 NextMoe，
      丢掉类型投票、tag 剧透与 roster 立绘（批量搜刮尤其致命）。
    - `api.nextmoe.dev`：直连**可用但慢 3~4 倍且抖动大**（同一请求 0.95s / 6.23s，偶发握手卡死），
      走代理 0.67~1.6s 且稳定。`NextMoeClient` 原写死 `UseProxy = false`，实测导致
      「11 个角色批量水合耗时 22296ms」这类 20 秒级尖刺。
    - `bgm.tv` / `api.bgm.tv`：直连必然超时（DNS 被污染，解析到 `2a03:2880::/32`、`31.13.64.0/18`
      等非真实地址），必须走代理。`BgmClient` 一直是对的。
    - 跟随系统代理的好处：Clash 开着走代理、关掉（`ProxyEnable=0`）自动回直连，不会把某一次的网络观测写死。
    - **排障坑**：本机 Bash 环境自带 `http_proxy/https_proxy=127.0.0.1:3327`（沙箱代理），
      Python urllib / curl 默认会走它 → 用它测 bgm.tv 会得到**假阴性**（全超时，极易误判为"bgm 挂了"、
      "正则失效"）。测外网请显式指定 `curl -x http://127.0.0.1:8888`，或用 `--noproxy '*'` 做对照。
      另：`api.nextmoe.dev` 对**默认 Python UA 返回 403**，测它必须带浏览器 UA。

13. **网络策略：任何客户端都不要写死 `UseProxy`**（2026-10-03 的教训，见 §3.10）。kungal / NextMoe / bgm
    三家的直连可用性**各自变过、而且互相相反**（见本节第 12 条）。写死的结果是
    「从某天起搜刮奇慢或全失败，但代码看起来毫无问题」——本轮单款游戏从 ~25 秒降到 ~4 秒就靠这一条。
    一律**跟随系统代理**：Clash 开着走代理、关掉（`ProxyEnable=0`）自动回直连。改完用宿主日志核对单款耗时
    （正常应在 2~5 秒；若出现「kungal 网页抓取异常 ... Timeout of 40 seconds」，就是又被写死成直连了）。

14. **宿主事件/接口换代后，先评估「用户可感知影响」再动手**（2026-10-10 的教训，见 §3.11）。
    宿主 v1.10.2.0 把 `GalgameAddedEvent` 等事件合并为 `GalgameMutated`，插件按旧名订阅 → `GetEvent`
    返回 null 且**不抛异常** → 静默失效。但**代码层失效 ≠ 功能坏了**：
    守卫/音量的主触发走 `GalgamePlayedMessage`（Messenger，Base 公开消息，与事件无关），用户几乎无感；
    真正断的只是「新游戏补挂观察」这条兜底。**排查时先画清一个功能的全部触发路径**，
    别把「有一条路断了」说成「功能坏了很久」——本轮曾因此夸大影响并误改代码。
    订阅新代事件必须 `Delegate.CreateDelegate`（泛型委托不支持协变）；
    退订按订阅时实际用的那一代退（`_addedSubscriberNewGen`/`_addedSubscriberOldGen` 双字段）。
    升级宿主后先查日志「宿主接口反射失败」与「新游戏…已纳入监听」是否还在。

15. **判断「游戏是否在运行」要问宿主，不要自己猜**（2026-10-10 的教训，见 §3.11）。
    插件能拿到的间接信号都不可靠：`ProcessName` 是**用户手动指定**字段（实测 9 款游戏全为 null，
    进程探测恒 false）；`TotalPlayTime` 滴答每分钟一次还带 `Thread.Sleep(60s)` 漂移，
    拿它当「活性」判据、再配一个小的安静宽限期，必然误判「用户玩着却判会话结束」。
    权威信号是宿主自己的后台任务：`BgTaskService.GetBgTasks()` 里存在该游戏的
    `RecordPlayTimeTask` = 游戏在跑；它被移除 = 宿主确认退出（移除时广播 `BgTaskRemoved`）。
    反射入口见 `HostServices.IsHostRecordingPlayTime` / `GetHostCurrentPlayMinutes`（本次游玩分钟数
    直接读 `RecordPlayTimeTask.CurrentPlayTime`，比增量推算准）。**查不到宿主服务时返回 null，
    调用方必须把 null 当「未知」而不是「已退出」**——宁可继续观察也不能凭猜测还原用户数据。

---

## 6. 开发完成 → 提交 GitHub

```powershell
git status                       # 确认改动范围（注意别包含 PotatoVN/ submodule 内部改动）
git add -A
# commit message：中文功能清单，只列条目不展开说明；用文件传参避免编码问题
git commit -F commit_msg.txt     # 格式：feat: vX.Y.Z 功能清单 + “- 功能条目”列表
git push origin main
```

- 每次发版打 tag，**tag 与 csproj `<Version>` 对齐**：
```powershell
git tag v1.4.2
git push origin v1.4.2
```
- remote：`https://github.com/Hansnmor/PotatoVN.Plugins.LibraryPlus.git`；凭据走 Windows 凭据管理器（用户 Hansnmor），push 时自动认证。

---

## 7. 发布 Release（构建 artifacts 产物 + 上传）

### 7.1 构建发布包
```powershell
dotnet build PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj -c Release
```
csproj 内置 `PackPlugin` target（仅 Release 执行）：自动清理共享 DLL 黑名单并压缩，产出
**`PotatoVN.App.PluginBase\artifacts\plugin.pvnplugin.zip`**（这就是要上传到 release 的插件包）。

### 7.2 创建 GitHub Release
本机**没有 gh CLI**，用 GitHub REST API（token 从 git 凭据管理器取）：

```powershell
# 1) 取 token（不打印明文）
Set-Content -Path "$env:TEMP\gitcred.txt" -Value "protocol=https`nhost=github.com`n" -NoNewline -Encoding Ascii
$tok = ((cmd /c "git credential fill < `"$env:TEMP\gitcred.txt`"" 2>$null) -split "`n" |
        Where-Object { $_ -like 'password=*' } | ForEach-Object { $_.Substring(9) })
Remove-Item "$env:TEMP\gitcred.txt"

# 2) release 正文写 UTF-8 JSON 文件（中文走文件，别内嵌命令）
#    {"tag_name":"vX.Y.Z","name":"vX.Y.Z","body":"# vX.Y.Z 功能清单\n\n- 条目...\n","draft":false,"prerelease":false}

# 3) 创建 release（返回 JSON 里有 id）
curl.exe -s -X POST -H "Authorization: Bearer $tok" -H "Accept: application/vnd.github+json" `
  -H "Content-Type: application/json" --data-binary "@release.json" `
  "https://api.github.com/repos/Hansnmor/PotatoVN.Plugins.LibraryPlus/releases"

# 4) 上传插件包
curl.exe -s -X POST -H "Authorization: Bearer $tok" -H "Accept: application/vnd.github+json" `
  -F "file=@PotatoVN.App.PluginBase/artifacts/plugin.pvnplugin.zip;type=application/zip" `
  "https://uploads.github.com/repos/Hansnmor/PotatoVN.Plugins.LibraryPlus/releases/<id>/assets?name=plugin.pvnplugin.zip"
```

- release 命名规范（历史）：name = tag = `vX.Y.Z`，资产名固定 `plugin.pvnplugin.zip`。
- **上传后必须校验资产字节**：响应 JSON 的 `size` 必须等于本地 zip 大小，发布前再下载回来比对 SHA-256。
  v1.4.3（2026-08-29）实测踩坑：本机网络环境下 `curl -F` 的 multipart 会被 uploads.github.com **原样落盘**
  （资产外多包一层约 215 字节的表单边界/头，zip 表面能打开但非原样字节，手工构造 multipart 也一样）。
  **必胜方案是裸字节上传**——不带任何表单包装，服务器无论解析与否，存的都是文件本身：
  `curl -X POST -H "Authorization: Bearer $tok" -H "Content-Type: application/zip" -H "Expect:" --data-binary @plugin.pvnplugin.zip "https://uploads.github.com/.../assets?name=plugin.pvnplugin.zip"`
- 发布后清理：删除临时 token 文件与 JSON 载荷文件。
- 验证：访问 release 页面确认 zip 资产可见、大小正常。

---

## 8. 一条龙流程清单（新功能开发）

1. 读本文档 → 读 `doc/main.md` → 按功能查 `doc/ui.md`/`doc/sidebar.md`/`doc/parser.md`/`doc/data.md` 等。
2. 明确功能落点（哪个接口注入、哪个文件改），遵循 §5 约束。
3. 写代码 → Debug 构建 → 开发者模式加载 → **完全退出重启 PotatoVN** 验证。
4. 确认功能 → `git add -A` → 中文功能清单 commit（`-F` 文件）→ `git push origin main`。
5. 升版本：csproj `<Version>` → `git tag vX.Y.Z` → `git push origin vX.Y.Z`。
6. Release 构建 → 校验 `artifacts/plugin.pvnplugin.zip` → API 建 release + 上传 zip（§7）。
7. 收尾：清理临时文件；如有新增功能/红线/环境结论，更新本文档——**不写版本号**（版本只在 csproj），
   会漂移的结论**带核对日期**，被推翻的结论移入下面的附录而不是删掉。

---

## 附录：已失效的历史结论（仅作考古，不要照抄）

> 这些结论在写下时都成立、且当时有实测依据，但**已被上游变更推翻**。
> 保留它们的用意：让接手者理解"当初为什么这么写"，避免重复验证同一个问题、或误以为某条现状是 bug。
> 每条都标注失效方向与失效时间。

### A. kungal 搜索/详情 HTTP API 的字段形状（2026-09 起失效）

失效原因：kungal 全站 API 改为需登录（匿名一律 `401 code:205`），v1.5.0 已迁移到「kungal 网页 SSR + NextMoe API」。
现行方案见 §3 / `doc/v1.5-nextmoe-migration.md`；以下是 v1.4.6 时代的记录：

- 搜索/详情接口的 `name` 是普通字符串（不是多语言对象），另有 `name_original`（原名/日文名）。
- 详情 `introduction` 是 `[{lang,intro,machine}]` 数组。

### B. kungal 角色接口 `/api/galgame-character/{id}` 的字段语义（2026-09 起失效）

失效原因：同 A（接口已需登录）；且 v1.5.0 起角色的中文名/立绘/剧透改由 kungal 网页 `roster`
的 `localized.zh-Hans` 提供。以下是 v1.4.6 时代的记录（**其中的教训仍然有效**，见 §5 第 11 条）：

- `name` 是**简体中文名**，`name_original` 是日文原名，`name_ja` 恒为 null。实测 10/10 与 bgm 角色页
  「简体中文名」完全一致、`name_original` 与 bgm 日文主名逐字一致——**kungal 已镜像 bangumi 角色数据**。
  但 kungal 并非全本地化：**部分角色（尤其冷门作）只有 vndb 链接、无 bgm 链接，kungal 给的中文译名
  可能与 bgm 不一致**（实例：轮舞曲Duo「神埼 イツキ」，kungal 给「神埼树」，bgm 收录的是另一译名）。
- **不要用「名字是否日文」当开关**：把中文名填进 `name` 后，`IsJapaneseName(kc.Name)` 判定恒为 false，
  整条 bgm 抓取会被静默跳过（不报错、不打日志，只表现为"中文名没了"）。这条教训在
  `KungalPhraser.FetchCharacterIntrosAsync` 里仍然适用。
- **纯汉字日文名不含假名**，`IsJapaneseName` 判不出来（如「沢渡真琴」「水瀬名雪」，中文圈库里很常见）；
  判断"库内角色名是否日文形态"统一走 `IsJapaneseLikeName`：含假名 **或** 与 kungal 的
  `name_original`/`name_ja` 完全相同（后者专门覆盖纯汉字场景）。

### C. 网络直连可用性（2026-10-03 起失效）

- 失效记录：**「kungal 直连可达」**——迁移期（2026-09）实测直连 1.2~2.6s 返回 200，因此
  `KungalWebClient` 与 `NextMoeClient` 都写死了 `UseProxy = false`；2026-10-03 复测发现 kungal
  直连已在 TLS 握手阶段必超时（每次干等满 40s 超时再回退），NextMoe 直连慢 3~4 倍且抖动。
  现行口径见 §5 第 12 条与 §3.10。
- 失效记录：**旧文档写的 Clash 代理端口 `127.0.0.1:7889`**——2026-10-03 实测已不通，实际是
  `127.0.0.1:8888`（以注册表 `Internet Settings\ProxyServer` 为准）。

