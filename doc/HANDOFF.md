# LibraryPlus 插件接手开发指南（HANDOFF）

> 本文档面向**新接手的 AI**：读这一篇就能一条龙走完「理解项目 → 开发 → 构建调试 → 提交 → 发布」。
> 当前版本：v1.4.2。仓库：https://github.com/Hansnmor/PotatoVN.Plugins.LibraryPlus （分支 main）

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
- **清除游玩记录**（SortPage「记录」菜单）：清空勾选/筛选集的 PlayedTime 明细 + 累计时长 + 上次游玩时间，可选连 PlayCount；确认框警示不可逆与 Steam 覆盖/云同步合并风险；完成自动退出多选。
- 宿主关键事实（排障必读）：启动游戏时宿主无条件 `LastPlayTime = DateTime.Now`；`TotalPlayTime/LastPlayTime` 由 `PlayedTime` 字典派生（`MergeTime` 为 max 合并**只增不减**）；「重启后删除的记录复活」渠道 = PVN 云同步 pull（编辑游玩时长页的保存**无条件**触发同步任务，不检查同步开关）/ Steam 刷新覆盖 / `.PotatoVN\meta.json` 备份重新入库合并。

### 3.6 插件数据导出/导入（v1.4.2）
- `Helper/PluginDataIoHelper.cs`：PluginData 整体备份为带标识头的 JSON（app 标记 + schema 版本 + 时间戳），含页面设置、手动分类/形态覆盖、搜刮与评分缓存、守卫配置。导入校验标识与版本（不符明确拒绝），确认后 `Plugin.ReplaceData` 热替换并持久化，页面状态即时重放刷新。
- 动机：卸载勾选「删除数据」= 直接删 `plugin_data` 集合中本插件 Guid 那条记录，不可恢复。

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
5. **版本号**：改 `PotatoVN.App.PluginBase.csproj` 的 `<Version>`（当前 1.4.2），与 GitHub tag 对齐。
6. 搜刮器/评分 API 细节（VNDB 匿名 POST、Bangumi v1 匿名可拿 R18、host token 经 `HostServices.GetBgmTokenAsync()`）见对应 Helper 文件头注释。
7. **kungal API v2 破坏性变更（已适配，勿回退）**：搜索/详情接口的 `name` 现在都是普通字符串（不再是 `{en-us,ja-jp,zh-cn,zh-tw}` 多语言对象），新增 `name_original`（原名/日文名）；详情 `introduction` 是 `[{lang,intro,machine}]` 数组（不再是多语言对象）。`KungalModels.cs` 里 `KungalCard.Name`、`KungalDetail.Name/NameOriginal/Introduction` 已按新结构建模（`KungalLang` 已移除）。若再遇「所有游戏批量搜刮都未匹配」，优先怀疑 kungal 接口又改了结构（用 curl 打 `/api/search`、`/api/galgame/{gid}` 实测比对）。

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
