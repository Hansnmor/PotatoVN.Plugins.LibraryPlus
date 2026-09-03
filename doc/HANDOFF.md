# LibraryPlus 插件接手开发指南（HANDOFF）

> 本文档面向**新接手的 AI**：读这一篇就能一条龙走完「理解项目 → 开发 → 构建调试 → 提交 → 发布」。
> 当前版本：v1.4.5。仓库：https://github.com/Hansnmor/PotatoVN.Plugins.LibraryPlus （分支 main）

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
   - `doc/PLAN-kungal-data-source.md` — kungal 数据源/评分融合的原始设计
   - `doc/search-issue.md` — 搜索框完整排查史（**只在要动搜索框时读**，§8.5 是最终形态定稿）
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
│   │   ├── SearchHelper.cs        # 搜索谓词（反射宿主 ApplySearchKey + 本地降级）
│   │   ├── GalgameClassifier.cs   # 内容分类（萌作/剧情作/拔作/其他）与形态分类
│   │   └── ExpectedPlayTimeHelper.cs # 预计时长估算
│   └── Models/PluginData.cs       # 插件持久化数据（RatingCache 等，经 LiteDB）
├── doc/                           # 文档（见 §1）
├── tools/                         # 开发辅助脚本（Python 探测/分析脚本）
└── .github/                       # 仅 copilot 指令，无 CI
```

---

## 3. 已开发功能总结（v1.4.0）

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
5. **版本号**：改 `PotatoVN.App.PluginBase.csproj` 的 `<Version>`（当前 1.4.4），与 GitHub tag 对齐。
6. 搜刮器/评分 API 细节（VNDB 匿名 POST、Bangumi v1 匿名可拿 R18、host token 经 `HostServices.GetBgmTokenAsync()`）见对应 Helper 文件头注释。
7. **kungal API v2 破坏性变更（已适配，勿回退）**：搜索/详情接口的 `name` 现在都是普通字符串（不再是 `{en-us,ja-jp,zh-cn,zh-tw}` 多语言对象），新增 `name_original`（原名/日文名）；详情 `introduction` 是 `[{lang,intro,machine}]` 数组（不再是多语言对象）。`KungalModels.cs` 里 `KungalCard.Name`、`KungalDetail.Name/NameOriginal/Introduction` 已按新结构建模（`KungalLang` 已移除）。若再遇「所有游戏批量搜刮都未匹配」，优先怀疑 kungal 接口又改了结构（用 curl 打 `/api/search`、`/api/galgame/{gid}` 实测比对）。
8. **右键菜单触发的导航必须等 `MenuFlyout.Closed` 再执行（v1.4.4，性能红线）**：在 Flyout 的点击处理里（或仅排队一拍）同步执行 `NavigateTo`，页面构建会被卷进菜单关闭动画的病态布局——实测构建耗时从正常的 50-100ms 膨胀到 ~3000ms，且 AppBarButton 本地化文字延迟渲染（宿主 `HomeViewModel.GalFlyOutEdit` 有同款"延迟导航"处理与注释）。正确写法见 `SortPage.EditGame_Click`：记录待办 → 挂一次性 `Closed` 事件 → `Closed` 里再 `InvokeOnMainThread` 导航。左键 `ItemClick` 无此问题，可直接同步导航。
9. **宿主游玩记录数据流事实（排障必读，v1.4.2 起）**：启动游戏时宿主无条件 `LastPlayTime = DateTime.Now`；`TotalPlayTime/LastPlayTime` 由 `PlayedTime` 字典派生，`MergeTime` 是 max 合并**只增不减**；「删除的记录重启后复活」渠道 = PVN 云同步 pull（游玩时长编辑页的保存**无条件**触发同步任务，不检查同步开关）/ Steam 刷新覆盖 / `.PotatoVN\meta.json` 备份重新入库合并。
10. **Core Audio 应用会话音量（v1.4.5，音量规范化用，写错必踩坑）**：
    - 宿主 `GalgameManager/Models/BgTasks/GameMuteTask.cs` 的 `AudioHelper` 是**权威参照**（后台静音功能，已验证可用），接口定义与 IID 照抄它。
    - **`IAudioSessionEnumerator` 的 IID 必须是 `E2F5BB11-0570-40CA-ACDD-3AA01277DEE8`**——写错（如抄成别的）会因列集器按错误 IID 做 QI 而 `GetSessionEnumerator` 出参恒 null、静默失效。
    - COM 接口的 vtable 槽位要占齐：`IMMDeviceEnumerator.GetDefaultAudioEndpoint` 是第 2 槽（前面留 1 个占位）、`IAudioSessionManager2.GetSessionEnumerator` 是第 3 槽（继承自 IAudioSessionManager 的 2 个方法占前 2 槽）；`out` 参数直接声明为目标接口类型可避免 object→cast 产生第二 RCW。
    - **RCW 释放纪律**：所有 `Marshal.ReleaseComObject` 必须 try-catch + 置空（`ReleaseSafe(ref x)`），且在 finally 里抛 `InvalidComObjectException` 会逃过方法内 catch 冒泡到调用方——曾因此「压一次成功却报处理异常」。
    - 触发判定**不要用 `now <= known` 防回退**：消息路+属性路同秒并发会自相矛盾地误拦；改为「LastPlayTime 在最近 2 分钟」+ `InFlight` 去重即可。
    - 匹配进程取 `Process.MainModule?.FileName`（bat 场景记录真实游戏 exe 路径，用于移动检测）；`PlayCount` 只在单次游玩 ≥5 分钟才 +1，故「首次」= `PlayCount==0 && TotalPlayTime<5`。
11. **kungal 角色中文字段语义与 bgm 抓取定位（角色中文名功能，踩过坑）**：
    - kungal 角色详情 `/api/galgame-character/{id}` 的 `name` 是**简体中文名**，`name_original` 是日文原名，
      `name_ja` 恒为 null。实测 10/10 与 bgm 角色页「简体中文名」完全一致、`name_original` 与 bgm 日文主名
      逐字一致——**kungal 已镜像 bangumi 角色数据**。但 kungal 并非全本地化：**部分角色（尤其冷门作）只有
      vndb 链接、无 bgm 链接，kungal 给的中文译名可能与 bgm 不一致**（实例：轮舞曲Duo「神埼 イツキ」，
      kungal 给「神埼树」，bgm 收录的是另一译名，两者不同）。
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
    - 环境：**`bgm.tv` / `api.bgm.tv` 在本机直连必然超时**（DNS 被污染，解析到 `2a03:2880::/32`、
      `31.13.64.0/18` 等非真实地址），必须走 Clash 系统代理（`127.0.0.1:7889`）。
      规律：kungal 直连可达，bgm 必须走代理。
    - **排障坑**：本机 Bash 环境自带 `http_proxy/https_proxy=127.0.0.1:3327`（沙箱代理），
      Python urllib / curl 默认会走它 → 用它测 bgm.tv 会得到**假阴性**（全超时，极易误判为"bgm 挂了"、
      "正则失效"）。测外网必须显式 `curl -x http://127.0.0.1:7889`，或用 `--noproxy '*'` 做对照。
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
7. 收尾：清理临时文件，更新本文档（功能清单、版本号、注意事项如有新增）。
