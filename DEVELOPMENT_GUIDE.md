# PotatoVN 插件开发实战参考（LibraryPlus）

> 本文档是 PotatoVN.Plugins.LibraryPlus（原名 MoreSortOptions）从零到 v1.1.0 的完整开发复盘，
> 供**接手本插件**或**开发其他 PotatoVN 插件**的 AI 助手/开发者参考。
> 包含：架构设计、宿主机制、全部踩坑记录、分类规则演进、开发与发布工作流。

---

## 1. 项目概览

| 项 | 值 |
|---|---|
| 仓库 | `Hansnmor/PotatoVN.Plugins.LibraryPlus`（原名 MoreSortOptions，2026-08-10 更名） |
| 插件名 | 游戏库增强 / 侧边栏按钮「扩展库」 |
| 插件 Guid | `70ee3f8a-361a-450a-acff-5371e85808b4`（**保持不变的插件身份**，改名时未动） |
| 程序集名 | `A70ee3f8a-361a-450a-acff-5371e85808b4` |
| 版本 | v1.0.0（2026-08-10）→ v1.1.0（2026-08-12） |
| 宿主 | PotatoVN（GalgameManager）1.10.2.0，MSIX 安装 |

**功能定位**：独立页面提供原生没有的能力——多级排序（预计时长/游玩时间/游玩次数/我的评分）、
状态筛选（全部/待玩/自定义）、时长区间（<10h/10-20h/20-40h/>40h/未知）、内容分类
（萌作/剧情作/拔作/同人作/其他）、统计条。**不修改原生页面任何行为**。

**核心哲学**：插件是"外挂"——最稳定的插件是**完全不碰宿主内部**的插件。本插件只通过
官方 API（IPotatoVnApi）和反射调用宿主公开成员，从不 Harmony patch、不操作宿主 ViewModel。

---

## 2. 架构总览

```
PotatoVN.App.PluginBase/          ← 插件本体（唯一需要改的工程）
├── Plugin.cs                     ← IPlugin 入口：初始化、数据持久化、ClearLastError
├── Plugin_Ui.cs                  ← 侧边栏按钮注册（Id=libraryPlus，进 SortPage）
├── Controls/SortPage.xaml(.cs)   ← 独立页面（排序/筛选/统计/交互）
├── Controls/Prefabs/             ← 模板自带控件（GalgamePrefab/Panel/Setting/Std*）
├── Controls/Converters/          ← 模板自带转换器（GalgamePrefab 用）
├── HostServices.cs               ← 反射宿主服务：保存/删除/搜刮/事件订阅/清 lastError
├── SidebarSelectionHelper.cs     ← 反射控制宿主侧边栏选中项（蓝条）
├── Helper/ExpectedPlayTimeHelper.cs  ← 预计时长解析（排序/区间/统计三处共用）
├── Helper/GalgameClassifier.cs   ← 内容分类器（萌/剧情/拔/同人/其他）
├── Models/PluginData.cs          ← 插件持久化数据（排序/筛选/区间/分类设置）
└── XamlResourceLocatorFactory.cs ← 模板自带 XAML 加载器（含踩坑点，见 §4.5）
```

**数据流**：页面打开 `HostApi.GetAllGames()` 拿快照 → `AdvancedCollectionView` 排序/筛选 →
操作通过 `HostServices` 反射调宿主方法 → 宿主 `PhrasedEvent` 触发时自动刷新列表。

---

## 3. 宿主关键机制（必须理解）

### 3.1 插件加载隔离（PluginLoadContext）
插件在独立 `AssemblyLoadContext`（`PluginService.PluginLoadContext`）加载。**编译期只能引用
`GalgameManager.WinApp.Base`**（应用公开库），宿主程序集 `GalgameManager` 只能运行时反射。
Base 是 git submodule 引入，**严禁修改**（改版本地即可，别提交回上游）。

### 3.2 插件页面生命周期（最重要的机制）
宿主 `PluginHostPage` 承载插件页面：
- 每次导航都 `Activator.CreateInstance` **重新创建插件 Page**
- `OnNavigatedFrom` 时**清空插件内容**
- → **插件页面的 `NavigationCacheMode` 完全无效**
- → **所有跨导航/跨重启的状态必须存 PluginData**（经 `HostApi.GetDataAsync/SaveDataAsync` 持久化），页面重建时恢复

### 3.3 侧边栏按钮与蓝条
- 插件按钮通过 `RegisterSidebarButton` 注册，宿主生成的 `UniqueId = plugin:{guid:N}:{buttonId}`
  （Tag 就是这个，不是原始 buttonId！匹配时要用 `CreatePluginButtonId` 反射构造或后缀兜底）
- 宿主 `NavigationViewService.GetSelectedItem` 只匹配设置了 `NavigateToProperty` 的内置项，
  插件按钮没有该属性 → 导航后宿主不会自动移动蓝条 → 需 `SidebarSelectionHelper` 反射设
  `NavigationView.SelectedItem`
- 原生行为：**详情页不显示蓝条** → 进详情后要 `ClearSelection`（SelectedItem=null）

### 3.4 点×进托盘 = 重启（宿主多进程竞态）
`SetWindowMode(SystemTray)` 走 `AppInstance.Restart("/r")` **实际重启进程**。重启时新进程
删除插件热重载目录（`_PluginXamlHotReload`），**任务栏旧实例**激活时插件 XAML 资源 URI
指向已删目录 → `XamlParseException`。托盘图标打开的是新进程 → 正常。**dev 模式专属，正式安装无此问题。**

### 3.5 热重载缺陷
dev 模式热重载后首次进页面崩：`GalgamePrefab cannot be cast to GalgamePrefab`（新旧
PluginLoadContext 各加载一份同名程序集，XAML 类型缓存冲突）。**改代码后必须完全重启 PotatoVN（含托盘），不要依赖热重载。**

---

## 4. 踩坑记录（按坑的"级别"排列）

### 4.1 【致命】宿主共享程序集的 API 版本红线
**插件不能用宿主共享 WinUI 程序集中较新版本的 API。**
`DependencyObject.DispatcherQueue`（DispatcherQueue 属性）是较新 WinUI 才有，宿主 1.10.2.0
较旧 → `MissingMethodException` 闪退。凡是宿主共享程序集（WinUI、CommunityToolkit 等，
见 PluginLoadContext 黑名单）的 API，只能用宿主版本已有的。**线程调度统一用
`IPotatoVnApi.InvokeOnMainThread`，禁用 DispatcherQueue/Dispatcher。**

### 4.2 【致命】插件 XAML 不能写 IsChecked 字面量
插件 XAML 经 `Application.LoadComponent`（`XamlResourceLocatorFactory.PluginControlInit`）
加载时，`ToggleButton`/`RadioButton` 写 `IsChecked="True"` 会
`XamlParseException: Failed to assign to property 'ToggleButton.IsChecked'` → 页面创建即崩。
**XAML 里一律不写 IsChecked，选中状态全部由代码设置。**

### 4.3 【致命】App.GetService<T>() 必须传 DI 注册类型
宿主 DI 注册的是**接口** `IGalgameCollectionService`，不是具体类。传具体类抛
`"needs to be registered in ConfigureServices"`（被反射包装成 TargetInvocationException）。
**实例用接口解析，方法反射仍从具体类拿**（运行时实例类型匹配可正常 Invoke）。

### 4.4 【严重】反射 Invoke 必须传全部参数
泛型方法（如 `SaveSettingAsync<T>(key, value, isLarge=false, ...)`）有可选参数，但反射
`Invoke` **默认值不自动应用**——必须传全参，否则 `TargetParameterCountException`。
（曾导致 ClearLastError 静默失败很久。）

### 4.5 【严重】namespace stamping 构建怪癖
模板构建时把源码复制到 `obj/Stamped/` 替换 namespace（随机 hash）。**增量构建偶发
XAML WMC0001 "Unknown type" 报错**（GalgamePrefab 的转换器），`rm -rf bin obj` 全量重建即恢复。
调试时注意：运行时是 `obj/Stamped/` 副本，断点可能绑不上。

### 4.6 【中等】反射调用要区分"实例解析"与"方法反射"
`HostServices` 里 `Service`（实例，按接口解析）+ MethodInfo（按具体类取）分开缓存，
`GetService` 失败不能静默吞（会掩盖所有后续调用失败）——早期 CreateService 失败被
静默 catch，导致下载游戏信息一直坏到 v1.1.0 才被发现。

### 4.7 【中等】宿主 lastError 只累加不清除
宿主 `App_UnhandledException` 把异常累加存 `KeyValues.LastError` 且**从不清理** → 任何
历史崩溃后每次恢复窗口都弹"上次运行崩溃了"。插件初始化时反射清（**条件清除**：仅当
lastError 含已知噪音特征才清，避免掩盖真实崩溃）。lastError 在 MSIX 的 settings.dat 里
**非明文，勿手工改文件**。

### 4.8 【小】MenuFlyout 只能放 MenuFlyoutItemBase
需要 CheckBox/RadioButton 多选的弹出层，用 **Flyout + StackPanel**（可放任意 UIElement），
MenuFlyout.Items 只接受 MenuFlyoutItemBase（且 ToggleMenuFlyoutItem 点击会关菜单）。

### 4.9 【小】emoji/图标渲染用 Python Pillow，别用 PowerShell System.Drawing
PowerShell 的 `Pen`/GraphicsPath 在脚本里有诡异作用域/类型问题（StartCap 报错、函数内
变量 null），改用 Python Pillow（本机有 12.3.0）生成图标等图形最稳。

---

## 5. 内容分类规则（GalgameClassifier，v4.7 冻结）

**数据源**：`Galgame.Tags.Value`（VNDB 中文翻译标签 + Bangumi 中文用户标签混合，可能含英文原样）
+ `Galgame.Ids` 的 VndbId。**纯本地零网络**（用户确认不做网络补标签——添加游戏时 tag 已下到本地）。

**判定顺序**（用户多轮确认，最终冻结）：
```
① 无 VNDB 条目 → 同人作（RPG Maker 同人/小黄油普遍只有 Bangumi 页面）
② 强拔作 → 拔作
    - 硬核词：nukige/porn with plot/凌辱/触手/调教/轮奸/双飞/mind break/humiliation/撸出血/抜きゲー
    - 萝莉/幼女 + 显式拔作标签（loli-nukige，夜羊社）
    - 人妻/母系/熟女/母 + 显式拔作标签（Mama×Holic）
③ 剧情作（剧情/剧情作/催泪/悬疑/泣系…，含 R18 神作如勇战魔物娘——拔作标签只是 R18 属性描述）
④ 显式拔作标签计数 ≥2（变身！5 个拔作标签）
⑤ 萌作（纯爱/治愈/废萌/甜作…）
⑥ 弱拔作（拔作/实用/萌拔/vanilla/ahegao/后宫/无修正，兜底）
⑦ 其他（诚实兜底，与"时长未知"哲学一致）
```

**演进史（重要，避免重走弯路）**：
- v1 内容优先（拔>剧>萌>同人）→ v2 引擎优先（RPG Maker 归同人）→ v3 VNDB-ID 硬判定
  （引擎优先误伤夜羊社）→ v4 拔作分强弱 → v4.2 萝莉/幼女改条件强信号（误伤 FAVORITE
  纯爱剧情作）→ v4.3 成熟题材中信号 → v4.4 拔作标签计数≥2 → v4.5 剧情作提前于拔作计数
  （勇战魔物娘）→ v4.7 撸出血/抜きゲー 硬核词（万华镜系列）→ **冻结**
- **v4.6 强萌拦截（废萌/萌作 提前）被还原**：影响面太大误伤带"废萌"标签的真拔作。
  教训：**强拔作词表只能放硬核 R18 行为词；角色属性/题材词（萝莉/幼女/人妻/后宫/姐/妹）
  必须与显式拔作标签组合才构成强信号，单独出现太常见于正常剧情作**。
- 误判案例库（经验）：秽翼（Artemis 商业引擎误判同人）、夜羊社（有 VNDB ID 该走内容）、
  甜蜜女友2（萌拔归萌）、Mama×Holic（母系拔归拔）、变身！（拔作标签密集归拔）、
  勇战魔物娘（R18 神作归剧情）、万华镜（撸出血归拔）、五彩斑斓/红瞳世界（萝莉角色剧情作）、
  Ambitious Mission FD2（社区拔作标签，接受瑕疵）、BALDR SKY Dive1（标签缺剧情掉其他，接受）

---

## 6. 开发工作流

```bash
# 构建
dotnet build PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj -c Debug
# 增量构建偶发 XAML 报错 → 全量重建
rm -rf PotatoVN.App.PluginBase/bin PotatoVN.App.PluginBase/obj
# Release 打包
dotnet build PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj -c Release
# 产物：artifacts/plugin.pvnplugin.zip
```

**测试**：改代码后**完全退出 PotatoVN（含托盘）再重启**。dev 插件路径 =
`...\PotatoVN.App.PluginBase\bin\Debug\net8.0-windows10.0.22621.0`（目录改名后需在插件管理重新指向）。
日常从**托盘图标**打开（任务栏打开有宿主竞态，见 §3.4）。

**调试**：宿主日志在 `LocalState\Logs\log{yyyyMMdd}.txt`；`IPotatoVnApi.Log()` 写日志、
`Info()` 弹 InfoBar。排查崩溃要**按时间戳取最新**（"Oops 上次运行崩溃了"可能是旧残留）。

---

## 7. 发布流程

1. 构建 Release（产 zip）
2. `git tag -a vX.Y.Z -m "..." && git push origin vX.Y.Z`
3. 创建 GitHub Release（tag 指向），附功能说明
4. 上传 `plugin.pvnplugin.zip` 到 release assets（用 `uploads.github.com` 域名，不是 api.github.com）

**正式安装版**（非 dev）：不启用热重载目录 → §3.4 竞态、§3.5 热重载闪退都不存在。
上商城/正式分发用打包安装即可。

---

## 8. 已知限制（宿主侧，插件无解）

| 限制 | 说明 |
|---|---|
| 详情页过渡"瞬间空白" | 宿主 PluginHostPage 清空插件内容 + 详情页首次创建，原生无 connected animation |
| 任务栏恢复 XAML 错误 | 宿主多进程竞态（§3.4），dev 专属 |
| 热重载后首进闪退 | 宿主 PluginLoadContext 双加载（§3.5），dev 专属 |
| 原生"更多设置"对话框 | 宿主私有类型无插件 API 出口，无法复用 |
| 原生排序菜单加项 | SortKeys 枚举 + XAML + switch 三层硬编码，无插件接口（曾用 harmony 方案被否决） |

---

## 9. 给未来开发者的建议清单

1. **先读 §3、§4 再动手**——这 9 个坑每一个都真实崩过/坏过
2. 插件页面状态一律走 PluginData 持久化，别依赖页面缓存
3. 反射调宿主：实例按接口解析、方法按具体类反射、Invoke 传全参、别静默吞错
4. 分类词表在 `GalgameClassifier` 静态数组，改词比改规则安全；规则改前先对照 §5 误判案例库
5. 改代码后完全重启 PotatoVN 验证，别用热重载
6. 加新功能优先走官方 API + 页面内数据，宿主内部能力没接口就放弃或诚实标注（如"更多设置"）
