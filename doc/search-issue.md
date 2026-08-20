# 扩展库页搜索框：实现问题排查文档

> 状态：**已解决（2026-08-16 新会话接手）**。根因与修复见 §8。
> 本文档保留历次尝试与失败记录，接手者请从 §8 的修复结论出发，勿重复已失败方案。

---

## 1. 需求

插件扩展库页（`PotatoVN.App.PluginBase/Controls/SortPage.xaml`）工具栏需要搜索功能：

- 默认收起：只显示一个「搜索」按钮（🔍 + 文字），**不占宽度**，与整排按钮样式一致
- 点击后展开搜索框，交互对齐宿主原生主页搜索（`InlineSearchAutoSuggestBox`）
- 输入实时过滤（复用宿主原生搜索语义 `GalgameExtension.ApplySearchKey`，已实现且可用）
- 收起条件：**失焦且输入为空时自动收起**（点击页面空白处搜索框消失）
- 搜索词与现有筛选（状态/分类/形态/时长）AND 联动

已实现且**确认可用**的部分：
- `Helper/SearchHelper.cs`：反射调用宿主 `GalgameManager.Models.GalgameExtension.ApplySearchKey`（匹配 Name/ChineseName/OriginalName/Developer/Tags），失败降级本地复刻——**无需改动**
- 搜索过滤与 `FilterGame` 组合——**无需改动**

**唯一未解决**：搜索框的展开/收起交互（位置、自动折叠）。

---

## 2. 原生实现分析（反编译结论，1.10.2.0）

宿主主页搜索框是自定义控件 **`GalgameManager.Views.Control.InlineSearchAutoSuggestBox`**（另有 `SearchAutoSuggestBox`，非折叠式，勿混淆）。

反编译位置：`_workspace\_decompile\searchbox2\`（ilspycmd 输出），关键逻辑：

### 2.1 结构
```
InlineSearchAutoSuggestBox (UserControl)
└── Grid
    ├── SearchButton（类型 AppBarButton，字段 `private AppBarButton SearchButton`）
    └── SearchBox（AutoSuggestBox，重叠在按钮上）
```

### 2.2 展开逻辑（SearchButton_Click → Expand）
```csharp
private void Expand()
{
    if (_isExpanded) { SearchBox.Focus(FocusState.Programmatic); return; }
    _isExpanded = true;
    ToggleState(isExpanded: true);
    AnimateWidth(ExpandedWidth);   // 宽度动画（Storyboard + DoubleAnimation）
    SearchBox.Focus(FocusState.Programmatic);
}
```

### 2.3 收起逻辑（关键）
```csharp
private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
{
    if (string.IsNullOrEmpty(SearchKey))   // 只有搜索词为空才收起
        Collapse();
}
// ClearButton 在 SearchKey 为空时点击也 Collapse
```

### 2.4 ToggleState（按钮/搜索框切换）
```csharp
private void ToggleState(bool isExpanded)
{
    SearchButton.IsHitTestVisible = !isExpanded;
    SearchBox.IsHitTestVisible = isExpanded;
    SearchButton.Opacity = (!isExpanded) ? 1 : 0;
    SearchBox.Opacity = (isExpanded ? 1 : 0);
}
```

### 2.5 宽度动画（AnimateWidth）
`Storyboard` + `DoubleAnimation`（字段 `_widthStoryboard`/`_widthAnimation`），目标宽度来自 `ExpandedWidth` 依赖属性。

### 2.6 其他
- `OnCtrlF_Invoked`：Ctrl+F 展开
- 附带清除按钮逻辑（AttachClearButton，找 AutoSuggestBox 模板内 DeleteButton）

### 2.7 原生按钮为何"一行"？
原生控件内 AppBarButton 在 UserControl 内（无 CommandBar 上下文），其 LabelPosition 未显式设置。
**注意**：运行时 WinUI（Microsoft.WindowsAppSDK 2.1.0）的 `CommandBarLabelPosition` 枚举只有
`Default` / `Collapsed` 两个成员（**没有 `Right`**，ilspycmd 反编译确认）——因此原生按钮
在 UserControl 内的实际显示形态（图标+文字纵向？仅图标？）需在原生主页肉眼确认（用户对照图见 §4.1）。

---

## 3. 历次实现尝试与失败记录（勿重复）

### 尝试 1：AppBarElementContainer + 重叠（最接近原生）
结构：`<AppBarElementContainer><Grid><AppBarButton .../><Border Width=0><AutoSuggestBox/></Border></Grid></AppBarElementContainer>`
- 失败：AppBarElementContainer 内的 AppBarButton **不继承 CommandBar 的 DefaultLabelPosition=Right**，
  显示为图标上文字下**两行**（用户反馈：图标和文字分两行）

### 尝试 2：自绘 Button（图标+文字横排 StackPanel）
`<Button><StackPanel Orientation="Horizontal">...` 
- 失败：① 高度比整排按钮高（默认 Button 尺寸 ≠ AppBarButton）；② hover 背景灰度与 AppBarButton 不同
- 尝试设 `Height=40` 对齐，仍"高了一点"，样式终究不一致

### 尝试 3：AppBarButton.Content 自绘横排（保持 AppBarButton 模板）
`<AppBarButton><AppBarButton.Content><StackPanel Orientation="Horizontal">...`
- 失败：**按钮上下明显更宽**（Content 在模板中占额外高度，视觉高度 > 整排按钮）

### 尝试 4：标准 AppBarButton 直接放 CommandBar + 独立覆盖层（Margin.Right 定位）
- 结构：CommandBar 主命令区 `<AppBarButton Icon="Find" Label="搜索"/>`（样式完全一致 ✓）；
  根 Grid Row0 叠一个 `<Border x:Name="SearchOverlay" HorizontalAlignment="Right" Width=0>`
- 展开：`TransformToVisual(Page).TransformPoint` 取按钮坐标 → `Margin.Right = 根宽 - 按钮X` → Width 动画 0→220（原地向左扩展）
- 失败现象：**搜索框渲染在页面左 1/4 处，与按钮组分离**（用户截图 2026-08-16 180726.png 实证）
- 诊断日志（用户实测提供）：
  ```
  搜索定位: btn=(1249,0) 根宽=1735 right=486 top=8 按钮高=48 overlay高=32 overflow=False
  ```
  **坐标计算完全正确**（right=1735-1249=486），但渲染位置与实际不符 → 问题在渲染层而非计算层

### 尝试 5：定位与动画分离（Margin.Left + TranslateTransform）
- 覆盖层 `Width=220` 固定，`Margin.Left = 按钮左X`（布局定位），展开动画 `RenderTransform.TranslateX 0→-220`（滑动，不影响布局）
- 失败现象：用户反馈**搜索框从右边跑到左边**（整个框滑动，非原地展开；动画方向/起点感知错误）

### 尝试 6：CommandBar 内联元素（Visibility 切换，零定位代码）
- 结构：`<AppBarButton SearchButton/>` + `<AppBarElementContainer x:Name="SearchBoxContainer" Visibility="Collapsed"><Border><AutoSuggestBox Width=200/></Border></AppBarElementContainer>` 紧随其后
- 展开：按钮 `Visibility=Collapsed`，容器 `Visibility=Visible`（CommandBar 自动布局，按钮原位变搜索框）
- 失败现象：**展开后失焦不自动折叠**（SearchBox_LostFocus → CollapseSearch 未生效或时序问题）

---

## 4. 已知事实与矛盾

### 4.1 用户截图（_OCR 目录）
- `屏幕截图 2026-08-16 180659.png`：收起态，按钮整排正常（功能说明/搜索/排序/筛选/多选/更多搜刮/计算评分）
- `屏幕截图 2026-08-16 180726.png`：**展开态**——搜索框渲染在页面左 1/4 处，与右侧按钮组分离，中间有明显空白；功能说明按钮被覆盖消失
- `屏幕截图 2026-08-16 180740.png`：**原生主页搜索对照**（原生的 InlineSearchAutoSuggestBox 形态）

### 4.2 关键矛盾
| 事实 | 矛盾 |
|---|---|
| 日志：按钮 X=1249，right=486（计算正确） | 实际渲染在页面左 1/4（≈X 400-620） |
| `TransformToVisual(Page)` 与 `TransformToVisual(Content/根Grid)` 两种基准都试过 | 均未解决 |
| 展开时 `IsInOverflow=False`（未折叠） | 排除 CommandBar 溢出折叠因素 |
| `IsDynamicOverflowEnabled=False` 已设 | 仍偏移 |

### 4.3 环境事实
- 宿主 1.10.2.0（`C:\Program Files\WindowsApps\37126GoldenPotato137.PotatoVN_1.10.2.0_x64__8vtbc0gbd4jey`）
- 插件页面在宿主 `PluginHostPage` 内承载（**可能存在宿主容器/嵌套布局因素**，如 PluginHostPage 对插件页面的包装、XamlRoot 偏移、页面缩放等）
- 反编译输出：`_workspace\_decompile\`（searchbox / searchbox2 / vm / base3 / gmodel / base / base2 等目录）

---

## 5. 待验证假设（建议新会话从这里开始）

1. **宿主 PluginHostPage 嵌套布局**：插件 Page 可能被宿主包在带 Margin/Padding/ScrollViewer 的容器里，
   `TransformToVisual(Page.Content)` 的坐标系与**渲染命中区域**不一致。验证：展开时输出
   `SearchOverlay.TransformToVisual(null).TransformPoint(0,0)`（相对窗口根）与
   `SearchButton.TransformToVisual(null)` 对比，看差值是否 = Margin 设置值。
2. **覆盖层渲染顺序/Z 序**：Border 虽声明在 CommandBar 之后，但 PluginHostPage 或 CommandBar 的
   背景/裁剪可能影响。验证：展开时给 SearchOverlay 加高亮 BorderBrush 观察实际可见区域。
3. **失焦收起失效（尝试 6）**：AutoSuggestBox.LostFocus 在 Visibility 切换/CommandBar 重排时的触发
   时序；验证：CollapseSearch 前打日志确认 LostFocus 是否触发、_searchExpanded 状态。
4. **原生形态确认**：肉眼对照原生主页搜索按钮（截图 180740）——确认原生按钮是"一行图标+文字"
   还是"两行"或"仅图标"，据此决定按钮形态（避免再为"一行"绕路）。
5. **WinUI 版本特性**：Microsoft.WindowsAppSDK 2.1.0（插件 bin 内 Microsoft.WinUI.dll）——
   `CommandBarLabelPosition` 无 Right（已确认），`AppBarElementContainer` 内按钮不继承
   `DefaultLabelPosition`（实测确认）——不要再尝试这两条路径。

---

## 6. 建议排查路径（按优先级）

1. **先解决"位置偏移"（尝试 4/5 现象）**：按假设 1/2 做渲染级诊断
   （TransformToVisual(null) 双端对比 + 高亮边框可视化），定位是坐标基准还是渲染层问题。
2. **再解决"自动折叠"（尝试 6 现象）**：确认 LostFocus 触发链（加日志），必要时
   改用 `PointerPressed`（页面级）或 `IsSuggestionListOpen` 管理等替代收起触发。
3. **形态定稿**：对照原生截图确认按钮形态后，选择：
   - 若接受"按钮→原位替换为搜索框"（尝试 6 结构，CommandBar 布局最稳）：修自动折叠即可
   - 若必须"覆盖式原地向左扩展"：需先搞清尝试 4 的渲染偏移根因（假设 1/2）
4. 全程注意：**不要使用 DispatcherQueue**（宿主共享 WinUI 版本过旧，MissingMethodException 闪退，
   见 DEVELOPMENT_GUIDE §4.1），线程调度用 `Plugin.HostApi.InvokeOnMainThread`。

---

## 7. 相关文件索引

| 内容 | 位置 |
|---|---|
| 插件页 XAML（工具栏/搜索相关） | `PotatoVN.App.PluginBase/Controls/SortPage.xaml` |
| 插件页代码（SearchToggle/Expand/Collapse/LostFocus/过滤） | `PotatoVN.App.PluginBase/Controls/SortPage.xaml.cs` |
| 搜索语义 helper（已可用，勿动） | `PotatoVN.App.PluginBase/Helper/SearchHelper.cs` |
| 原生控件反编译（InlineSearchAutoSuggestBox） | `_workspace\_decompile\searchbox2\GalgameManager.Views.Control\InlineSearchAutoSuggestBox.cs` |
| 原生控件反编译（SearchAutoSuggestBox） | `_workspace\_decompile\searchbox\` |
| 宿主 HomeViewModel 搜索谓词 | `_workspace\_decompile\vm\`（第 1271 行 `Source.Filter`） |
| 宿主 GalgameExtension.ApplySearchKey | `_workspace\_decompile\GalgameManager.Models\GalgameExtension.cs`（第 52 行） |
| 用户截图（对照/实证） | `_workspace\_OCR\屏幕截图 2026-08-16 180659/180726/180740.png` |
| 宿主日志（搜索定位诊断行） | `%LOCALAPPDATA%\Packages\37126GoldenPotato137.PotatoVN_8vtbc0gbd4jey\LocalState\Logs\`（搜「搜索定位」） |

---

## 8. 解决方案（2026-08-16 新会话接手，已实现并构建通过）

**形态定稿**：采用尝试 6 结构（按钮→CommandBar 内原位替换为搜索框），放弃覆盖层定位（尝试 4/5 的
渲染偏移与 PluginHostPage 无关——反编译确认宿主只是把插件 Page 直接放进 ContentPresenter，
`Stretch` 对齐，无嵌套布局因素；偏移根因未再深究，因为原位替换结构已无定位代码）。

### 8.1 根因（「失焦不自动折叠」的两条独立原因）

1. **焦点从未进入搜索框**：`ExpandSearch` 里容器刚由 `Collapsed→Visible` 就立即调用
   `SearchBox.Focus(Programmatic)`——内容尚未完成加载/模板实例化，`Focus()` 静默失败。
   焦点没进搜索框 → 之后点击页面任何位置都不会触发 `SearchBox.LostFocus` → 自动收起整条事件链断裂。
2. **点击不可聚焦区域本就不移动焦点**：即使焦点进了搜索框，WinUI 中点击工具栏空白、页面空白 Grid
   等不可聚焦区域不会移动焦点，`LostFocus` 不触发（宿主原生页能收起，是因为它的"页面空白"基本
   都是 ListView 等可聚焦控件）。

### 8.2 修复（改动文件：`SortPage.xaml` + `SortPage.xaml.cs`）

1. **页面级 PointerPressed 兜底（收起的主要触发，不依赖焦点机制）**：
   构造器里 `RootGrid.AddHandler(UIElement.PointerPressedEvent, ..., handledEventsToo: true)`
   （宿主 `HomePage.cs:3927` 同款模式，WinUI 冒泡路由事件，能收到按钮等已处理事件的按下）。
   `OnRootPointerPressed`：按下目标不在 `SearchBoxContainer` 子树内且输入为空 → `CollapseSearch`。
   对"点击页面空白处搜索框消失"的语义全覆盖（含工具栏空白、统计文本、GridView、右键按下）。
2. **聚焦修复**：`FocusSearchBox()` 直接 `Focus()`，失败则挂 `SearchBox.Loaded` 一次性聚焦
   （模板就绪后再聚焦，聚焦后摘除）——展开后立即可打字。
3. **保留 `SearchBox_LostFocus`** 作为键盘 Tab 等焦点移动场景的补充触发（防御性，双保险）。
4. **补齐 Ctrl+F 加速器**（`Page.KeyboardAccelerators`，`args.Handled = true`），对齐原生
   `InlineSearchAutoSuggestBox.OnCtrlF_Invoked`（按钮 Tooltip 早已宣传 Ctrl+F）。

### 8.3 行为核对表

| 操作 | 结果 |
|---|---|
| 点「搜索」按钮 / Ctrl+F | 展开，按钮原位变搜索框，焦点进框（可立即输入） |
| 输入文字 | 实时过滤（原有逻辑，未动） |
| 点页面空白 / 工具栏空白 / 其他按钮 / 游戏卡片（输入为空） | 收起并还原按钮 |
| 点搜索框自身 / 清除按钮 / 建议列表弹出层 | 不收起 |
| 输入非空时点别处 | 不收起（符合"失焦且输入为空才收起"语义） |
| Tab 移出搜索框（输入为空） | 收起（LostFocus 兜底） |

构建：`dotnet build PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj -c Release` ✓（0 错误），
产物 `PotatoVN.App.PluginBase/artifacts/plugin.pvnplugin.zip`。待用户实测确认后可将 §8.1/8.2 结论
精简回 §1 需求下，删除本节的临时性描述。

### 8.4 尝试 7（当前代码状态）：按用户要求恢复「按钮+重叠搜索框」结构 + Height 钳制

用户反馈尝试 6 的原位替换形态不合意，要求改回尝试 3 的「AppBarButton + 重叠 SearchOverlay」结构，
并加 `Height=40` 钳制按钮高度。**当前代码即此形态**（§8.2 的尝试 6 描述已不适用，保留作历史）：

- XAML：`SearchButton`（`Height=40 VerticalAlignment=Center`，Content 自绘横排「🔍 搜索」，
  `Click=SearchToggle_Click`）+ 同容器内右对齐 `SearchOverlay` Border（`Width=0`、`Opacity=0`、
  `IsHitTestVisible=False`，内含 `SearchBox` AutoSuggestBox）。
- 显隐切换：`ToggleSearchState`（Opacity + IsHitTestVisible 互斥，不用 Visibility/Collapsed，
  避免模板实例化时序问题）；宽度动画 `AnimateSearchWidth(0↔220)`（DoubleAnimation +
  EnableDependentAnimation，异常兜底直设 Width）。
- 收起链不变（§8.2 的 1/2/3/4 全部保留）：页面级 PointerPressed 兜底（`IsWithin(source, SearchOverlay)`）、
  `FocusSearchBox` Loaded 重试、`SearchBox_LostFocus`、Ctrl+F。
- 已清掉全部 `SearchBoxContainer` 残留引用；`dotnet build -c Debug` ✓ 0 错误
  （产物 `bin\Debug\net8.0-windows10.0.22621.0\A70ee3f8a-...dll`，2026-08-16 18:35）。
- **用户全量重启 PotatoVN（含托盘）实测通过（2026-08-16 晚）**。后续按用户反馈又修了 4 个视觉细节，
  均为本轮最终形态，接手者直接照抄即可：

### 8.5 最终形态定稿（用户实测通过，2026-08-16）

1. **按钮高度**：不写死，`OnPageLoaded` 读「功能说明」按钮 `ActualHeight` 赋给 `SearchButton.Height`（实测 48）。
2. **搜索框高度**：紧凑输入框 `32`（`SearchBoxHeight` 常量），居中于按钮区域——**不能对齐到按钮控件高度 48**：
   按钮可见内容只有图标+文字约 20 高，48 高实心输入框视觉凸出近两倍（用户截图确认）。
   设置位置：`OnPageLoaded` + `ExpandSearch` 双重强制。
3. **无 `QueryIcon`**：原生 AutoSuggestBox 默认右侧是 × 清除按钮（输入后出现），不是放大镜。
4. **`SearchOverlay` Border 只做宽度动画容器**：不加 `Background`/`Padding`/`CornerRadius`——
   加了会在 AutoSuggestBox 周围露边，形成左右两条半透明圆角浅色块（用户截图确认）。
5. **坑位记录**：
   - 插件 XAML 经 `XamlResourceLocatorFactory`/XamlReader 加载，**`ElementName` 绑定不生效**（Height 绑定曾静默失败），一律代码直接赋值。
   - `VerticalAlignment="Stretch"` 会被外层 Grid（CommandBar 容器拉伸）撑高，不可用于等高。
   - `Debug.WriteLine` 不进宿主日志文件（只进调试器），排查诊断要显示在 UI 上（`BatchProgressText`）或让用户截图。
   - AutoSuggestBox 在容器宽度≈0 时测量高度会异常（曾达 64），显式 Height 钳制即可。
6. **搜索词持久化（2026-08-20 新增，用户需求：切走再切回搜索内容仍在，直到手动删除）**：
   - `PluginData.SearchKeyword` 持久化搜索词（随排序/筛选等页面状态一并自动保存）；
   - `SortPage.RestoreSearchState()`：构造器恢复关键词 → 非空则展开搜索框、填入文本（不抢焦点、不播展开动画、直接铺开宽度）；
   - **抗闪烁关键（两步，缺一不可）**：① 构造器先恢复全部页面状态（含搜索词）→ 挂 `_source.Filter = FilterGame` 并 `Refresh()` → **最后才** `GameGridView.ItemsSource = _source`，首次渲染即"已过滤"；② **抑制恢复那一次 TextChanged**：`RestoreSearchState` 设 `_restoringSearch=true` 再赋 `SearchBox.Text`，`SearchBox_TextChanged` 见标记只记状态、跳过 `RefreshFilter()`（`_source.Refresh()` 会触发 GridView Reset 重绘）。此前的写法（先绑 ItemsSource 再恢复并 RefreshFilter）会先显示全部、再重算过滤，切回时画面闪一下（用户 2026-08-20 两次实测反馈，已修）；
   - `SearchBox_TextChanged` 每次输入即持久化；`CollapseSearch` 置空持久化（手动删除/清空后不再恢复）；
   - 效果：详情页返回/应用重启后搜索内容仍在；点 × 或清空文本后消失。行为与原生主页搜索一致且更持久（原生仅会话内存、页面常驻不重搜；本实现连重启都保留——若需对齐原生"重启即清"，把持久化源从 PluginData 换回静态字段即可）。
