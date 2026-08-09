# PotatoVN.Plugins.MoreSortOptions

为 [PotatoVN](https://potatovn.net) 提供**独立游戏排序页面**的插件，支持更多排序条件。

## 功能

侧边栏新增「更多排序」入口，进入一个**与原生游戏库页功能一致**的独立页面：

- **布局一致**：使用与原生页面相同的游戏卡片（封面尺寸、名称显示、游玩状态角标等），网格随窗口宽度自适应排列
- **交互一致**：
  - 单击游戏卡片 → 进入游戏详情页（带与原生一致的封面过渡动画）
  - 右键卡片 → 游玩状态修改 / 编辑游戏信息 / 下载游戏信息 / 在文件管理器中打开 / 从游戏库删除
- **排序更多**（本页唯一差异）：
  - 按预计时长排序：升序（默认）/ 降序
  - 未填写预计时长的游戏（显示为 `——`）无论升序降序都排在最后
  - 默认顺序：恢复游戏库原有顺序
- **排除已玩过**：工具栏开关可过滤掉游玩状态为「已玩过」的游戏（默认关闭），状态持久化，返回页面/重启应用后保持
- **数量统计**：左上角显示「共 xx 款游戏」，启用排除后跟随过滤扣减已玩过的数量

**完全独立**：本页的排序与操作只作用于插件页面，不影响 PotatoVN 自带的【游戏】页面及其排序。

## 使用方法

1. 在 PotatoVN 的插件管理页安装本插件并启用。
2. 侧边栏点击「更多排序」进入排序页面。
3. 点击工具栏「排序」选择排序条件与方向，游戏列表即时更新。

## 实现说明

- 排序页面是插件自己的 WinUI `Page`（`Controls/SortPage`），通过侧边栏按钮进入（`IPotatoVnApi.NavigateTo` + `RegisterSidebarButton`）。
- 列表使用 `AdvancedCollectionView` + 自定义 `IComparer` 实现排序，不触碰宿主 `HomeViewModel` 的任何状态。
- 点击进详情 / 打开设置页通过 `IPotatoVnApi.NavigateTo(PageEnum.GalgamePage / GalgameSettingPage)` 完成；
  保存、删除、搜刮信息等操作通过 `HostServices` 反射调用宿主 `GalgameCollectionService` 的公开方法。
- 侧边栏选中指示器（蓝色小条）跟随：宿主只自动匹配内置导航项，插件按钮需通过 `SidebarSelectionHelper`
  反射设置 `NavigationView.SelectedItem`——进详情时移到「游戏」项，返回插件页时移回「更多排序」。
- 「排除已玩过」状态持久化在插件数据（`PluginData`）中；插件页面由宿主 `PluginHostPage` 每次重新创建，因此页面状态均通过插件数据保存/恢复。
- 预计时长支持 VNDB 搜刮的格式（如 `1h30m`、`45m`），以及 `very short` / `short` / `medium` / `long` / `very long` 类别（映射为估算时长）。

## 开发

- `PotatoVN.App.PluginBase`：插件本体
- `PotatoVN`：PotatoVN 主项目（git submodule，含 WinApp.Base 应用公开库）
- 开发文档见 `doc/` 目录

构建插件包（Release）：`dotnet build PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj -c Release`，产物位于 `artifacts/plugin.pvnplugin.zip`。
