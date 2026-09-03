using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Collections;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.NavigationApi;
using GalgameManager.WinApp.Base.Contracts.NavigationApi.NavigateParameters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PotatoVN.App.PluginBase.Helper;
using Windows.Foundation;
using PotatoVN.App.PluginBase.Helper.Bangumi;
using PotatoVN.App.PluginBase.Helper.Kungal;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Controls;

/// <summary>
/// 独立的游戏排序页面：与游戏库原生页面功能一致（点击进详情、右键菜单、卡片布局），
/// 排序上额外提供原生没有的条件：预计时长 / 游玩时间 / 游玩次数 / 我的评分，支持主键+次键多级排序。
/// 本页排序只作用于本页，不影响原生页面。
/// </summary>
public sealed partial class SortPage : Page
{
    /// <summary>排序键的默认值（无排序 = 保持游戏库原顺序）</summary>
    private const string KeyDefault = "Default";

    private readonly AdvancedCollectionView _source;
    private Galgame? _currentGame;

    /// <summary>多选勾选集（独立于 GridView.SelectedItems 维护——视图重建/筛选刷新会清空 SelectedItems，
    /// 勾选意图存这里跨筛选保留，刷新后从它恢复可见项的选中）</summary>
    private readonly HashSet<Guid> _batchSelection = new();

    /// <summary>恢复勾选进行中：屏蔽 SelectionChanged 同步（SelectedItems.Clear/Add 会触发事件，
    /// 若不同步屏蔽会把 _batchSelection 自己清掉——勾选丢失的根源）</summary>
    private bool _restoringSelection;

    public SortPage()
    {
        XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);

        // 搜索框收起兜底：WinUI 中点击不可聚焦区域（工具栏空白/页面空白等）不会移动焦点，
        // 单独靠 SearchBox.LostFocus 收不到；PointerPressed 是冒泡路由事件，
        // AddHandler(handledEventsToo:true) 能收到所有按下（含按钮等已处理事件的元素），
        // 在 OnRootPointerPressed 里统一判断"按下的目标是否在搜索框内"。
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);

        // 搜索按钮/搜索框高度对齐：宿主主题下 AppBarButton 实际高度不一定是 40，
        // 布局完成后以「功能说明」按钮为准，让搜索相关元素与其他按钮完全等高
        Loaded += OnPageLoaded;

        // 页面重建（如从详情页返回）后，把侧边栏选中指示器移回「更多排序」
        SidebarSelectionHelper.SelectPluginButton("libraryPlus");

        List<Galgame> games = Plugin.HostApi.GetAllGames();
        _source = new AdvancedCollectionView(games, true);

        // 恢复持久化的页面状态（跨页面重建 / 应用重启保持）。
        // 顺序关键：先恢复全部状态（含搜索词），再挂上过滤器并一次性刷新，
        // 最后才把 _source 绑给 GridView——保证首次渲染就是"已过滤"的列表。
        // 否则切回页面会先显示全部、再重算过滤，画面闪一下（原生页因页面常驻缓存无此问题）。
        RestoreRangeState();
        RestoreCategoryState();
        RestoreFormState();
        RestoreSortMenuState();
        RestoreSearchState();
        ApplySort();
        _source.Filter = FilterGame;
        _source.Refresh();
        GameGridView.ItemsSource = _source;
        UpdateCountText();
        UpdateStatsText();

        // 批量搜刮进行中（切走再切回）：恢复锁定 UI + 进度显示，并订阅实时更新
        if (Plugin.IsBatchScraping)
        {
            GameGridView.IsEnabled = false;
            GameGridView.Opacity = 0.5; // 变灰特效（GalgamePrefab 非 Control，IsEnabled 无视觉降级）
            BatchProgressText.Text = Plugin.BatchStatus;
            Plugin.BatchStatusChanged += OnBatchStatusChanged;
            Unloaded += (_, _) => Plugin.BatchStatusChanged -= OnBatchStatusChanged;
        }

        // 搜刮信息完成后自动刷新列表（与原生页行为对齐，保留当前排序/筛选状态）
        HostServices.SubscribePhrased(OnHostPhrased);
        Unloaded += (_, _) => HostServices.UnsubscribePhrased(); // 页面销毁退订，防事件泄漏
    }

    /// <summary>宿主搜刮完成事件：重新拉数据并刷新，保留排序/筛选/区间/分类状态</summary>
    private void OnHostPhrased()
    {
        Plugin.HostApi.InvokeOnMainThread(() =>
        {
            try
            {
                _source.Source = Plugin.HostApi.GetAllGames();
                ApplySort();
                RefreshFilter();
            }
            catch
            {
                // 静默：刷新失败不影响页面
            }
        });
    }

    #region 排序

    private void ApplySort()
    {
        _source.SortDescriptions.Clear();
        AddSortDescription(Plugin.Data.PrimarySortKey, Plugin.Data.PrimaryDescending);
        AddSortDescription(Plugin.Data.SecondarySortKey, Plugin.Data.SecondaryDescending);
        _source.RefreshSorting();
    }

    /// <summary>
    /// 添加一条排序描述。SortDescription 不带属性名 + 自定义比较器（直接接收 Galgame 对象），
    /// 方向固定 Ascending，升降序在比较器内部处理（保证"未知/0 值恒排最后"在两种方向下一致）。
    /// 多个 SortDescription 由 AdvancedCollectionView 依次比较，天然形成主键→次键的多级排序。
    /// </summary>
    private void AddSortDescription(string key, bool descending)
    {
        if (key is null or "" or KeyDefault) return;
        _source.SortDescriptions.Add(new SortDescription(SortDirection.Ascending, CreateComparer(key, descending)));
    }

    private static IComparer CreateComparer(string key, bool descending) => key switch
    {
        "ExpectedPlayTime" => new ExpectedPlayTimeComparer(descending),
        "PlayTime" => new NumericValueComparer(g => g.TotalPlayTime, descending),
        "PlayCount" => new NumericValueComparer(g => g.PlayCount, descending),
        "MyRate" => new NumericValueComparer(g => g.MyRate, descending),
        "WeightedScore" => new WeightedScoreComparer(descending),
        // 兜底：未知键不改变顺序
        _ => new NumericValueComparer(_ => 0, false),
    };

    /// <summary>从持久化数据恢复主/次排序键菜单选中状态</summary>
    private void RestoreSortMenuState()
    {
        string primary = Plugin.Data.PrimarySortKey;
        PrimaryDefault.IsChecked = primary == KeyDefault;
        PrimaryExpected.IsChecked = primary == "ExpectedPlayTime";
        PrimaryPlayTime.IsChecked = primary == "PlayTime";
        PrimaryPlayCount.IsChecked = primary == "PlayCount";
        PrimaryMyRate.IsChecked = primary == "MyRate";
        PrimaryWeightedScore.IsChecked = primary == "WeightedScore";
        PrimaryDescendingItem.IsChecked = Plugin.Data.PrimaryDescending;

        string secondary = Plugin.Data.SecondarySortKey;
        SecondaryDefault.IsChecked = secondary == KeyDefault;
        SecondaryExpected.IsChecked = secondary == "ExpectedPlayTime";
        SecondaryPlayTime.IsChecked = secondary == "PlayTime";
        SecondaryPlayCount.IsChecked = secondary == "PlayCount";
        SecondaryMyRate.IsChecked = secondary == "MyRate";
        SecondaryWeightedScore.IsChecked = secondary == "WeightedScore";
        SecondaryDescendingItem.IsChecked = Plugin.Data.SecondaryDescending;
    }

    private void PrimaryKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.CommandParameter is not string key) return;
        Plugin.Data.PrimarySortKey = key;
        ApplySort();
    }

    private void PrimaryDescending_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item) return;
        Plugin.Data.PrimaryDescending = item.IsChecked;
        ApplySort();
    }

    private void SecondaryKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.CommandParameter is not string key) return;
        Plugin.Data.SecondarySortKey = key;
        ApplySort();
    }

    private void SecondaryDescending_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item) return;
        Plugin.Data.SecondaryDescending = item.IsChecked;
        ApplySort();
    }

    /// <summary>
    /// 按预计时长比较游戏：未知时长（—— / 空 / 无法解析）始终排在最后，无论升序降序。
    /// </summary>
    private sealed class ExpectedPlayTimeComparer : IComparer
    {
        private readonly bool _descending;

        public ExpectedPlayTimeComparer(bool descending) => _descending = descending;

        public int Compare(object? x, object? y)
        {
            long? px = ExpectedPlayTimeHelper.ParseMinutes((x as Galgame)?.ExpectedPlayTime?.Value);
            long? py = ExpectedPlayTimeHelper.ParseMinutes((y as Galgame)?.ExpectedPlayTime?.Value);

            if (px is null && py is null) return 0;
            if (px is null) return 1; // 未知时长排在后面
            if (py is null) return -1;

            int cmp = px.Value.CompareTo(py.Value);
            return _descending ? -cmp : cmp;
        }
    }

    /// <summary>
    /// 按加权综合评分比较游戏（bangumi/vndb 双源 √n 加权，见 <see cref="WeightedScoreHelper"/>）。
    /// 无评分数据（未搜刮 kungal / 未采集评分）恒排最后，无论升序降序。
    /// </summary>
    private sealed class WeightedScoreComparer : IComparer
    {
        private readonly bool _descending;

        public WeightedScoreComparer(bool descending) => _descending = descending;

        public int Compare(object? x, object? y)
        {
            double? px = x is Galgame gx ? WeightedScoreHelper.GetScore(gx) : null;
            double? py = y is Galgame gy ? WeightedScoreHelper.GetScore(gy) : null;

            if (px is null && py is null) return 0;
            if (px is null) return 1; // 无评分排在后面
            if (py is null) return -1;

            int cmp = px.Value.CompareTo(py.Value);
            return _descending ? -cmp : cmp;
        }
    }

    /// <summary>
    /// 按数值属性比较游戏（游玩时间/游玩次数/我的评分）：0（未游玩/未评分）恒排最后，无论升序降序。
    /// </summary>
    private sealed class NumericValueComparer : IComparer
    {
        private readonly Func<Galgame, int> _selector;
        private readonly bool _descending;

        public NumericValueComparer(Func<Galgame, int> selector, bool descending)
        {
            _selector = selector;
            _descending = descending;
        }

        public int Compare(object? x, object? y)
        {
            int px = x is Galgame gx ? _selector(gx) : 0;
            int py = y is Galgame gy ? _selector(gy) : 0;

            bool mx = px <= 0, my = py <= 0;
            if (mx && my) return 0;
            if (mx) return 1; // 0 值排在后面
            if (my) return -1;

            int cmp = px.CompareTo(py);
            return _descending ? -cmp : cmp;
        }
    }

    #endregion

    #region 过滤与统计

    /// <summary>时长区间筛选键：All=全部（默认），其余见 <see cref="MatchesRange"/></summary>
    private const string RangeKeyAll = "All";

    /// <summary>状态筛选模式常量：All=全部 / ToPlay=待玩预设 / Custom=自定义</summary>
    private const string FilterModeAll = "All";
    private const string FilterModeToPlay = "ToPlay";
    private const string FilterModeCustom = "Custom";

    /// <summary>内容分类筛选键：All=全部</summary>
    private const string CategoryKeyAll = "All";

    /// <summary>「筛选」弹出层打开时，同步预设单选与状态勾选态（仅在自定义模式下显示勾选）</summary>
    private void FilterMenu_Opening(object sender, object e)
    {
        string mode = Plugin.Data.FilterMode;
        FilterAll.IsChecked = mode == FilterModeAll;
        FilterToPlay.IsChecked = mode == FilterModeToPlay;
        FilterCustom.IsChecked = mode == FilterModeCustom;
        SyncFilterCheckBoxes();
    }

    /// <summary>预设点击：全部 / 待玩 / 自定义。切到「全部/待玩」时清空自定义选择（切回自定义重新选）</summary>
    private void FilterPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string mode) return;
        if (mode != FilterModeCustom)
        {
            Plugin.Data.IncludedPlayTypes = new(); // 清空自定义选择（不再保留，切回自定义重新选）
            SyncFilterCheckBoxes();
        }
        Plugin.Data.FilterMode = mode; // 持久化
        RefreshFilter();
    }

    /// <summary>自定义模式：勾选=只显示该状态；实时生效（列表即时刷新）。
    /// CheckBox 点击不关闭菜单，可继续勾选其他状态再即时更新。</summary>
    private void FilterState_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string playTypeName) return;

        List<string> included = Plugin.Data.IncludedPlayTypes;
        if (cb.IsChecked == true)
        {
            if (!included.Contains(playTypeName)) included.Add(playTypeName);
        }
        else
        {
            included.Remove(playTypeName);
        }
        Plugin.Data.IncludedPlayTypes = included; // 触发持久化
        Plugin.Data.FilterMode = FilterModeCustom; // 手动勾选即进入自定义
        FilterAll.IsChecked = false;
        FilterToPlay.IsChecked = false;
        FilterCustom.IsChecked = true;
        RefreshFilter(); // 实时生效
    }

    /// <summary>把 6 个状态 CheckBox 的勾选态同步为当前自定义选择（仅自定义模式显示勾选）</summary>
    private void SyncFilterCheckBoxes()
    {
        bool showTicks = Plugin.Data.FilterMode == FilterModeCustom;
        List<string> included = Plugin.Data.IncludedPlayTypes;
        IncludeNone.IsChecked = showTicks && included.Contains("None");
        IncludePlaying.IsChecked = showTicks && included.Contains("Playing");
        IncludePlayed.IsChecked = showTicks && included.Contains("Played");
        IncludeShelved.IsChecked = showTicks && included.Contains("Shelved");
        IncludeAbandoned.IsChecked = showTicks && included.Contains("Abandoned");
        IncludeWantToPlay.IsChecked = showTicks && included.Contains("WantToPlay");
    }

    /// <summary>统一刷新过滤：状态筛选（包含式）+ 时长区间，两个条件为 AND 关系。
    /// 视图重建会清空 GridView.SelectedItems——勾选意图在 _batchSelection（SelectionChanged 同步），
    /// 刷新后从它恢复可见项的选中（被筛选掉的切回后自然恢复）。</summary>
    private void RefreshFilter()
    {
        // 关键：屏蔽标志必须在 _source.Refresh() 之前设置——视图重建会清空 SelectedItems 并触发
        // SelectionChanged，若屏蔽未生效，_batchSelection 会被自己的事件清掉（勾选丢失的根源）
        bool restore = GameGridView.SelectionMode == ListViewSelectionMode.Multiple &&
                       _batchSelection.Count > 0;
        if (restore) _restoringSelection = true;

        _source.Filter = FilterGame;
        _source.Refresh();
        UpdateCountText();
        UpdateStatsText();

        if (restore)
        {
            try
            {
                GameGridView.SelectedItems.Clear();
                foreach (Galgame item in _source.OfType<Galgame>().Where(g => _batchSelection.Contains(g.Uuid)))
                    GameGridView.SelectedItems.Add(item);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // 框架 bug：本次恢复失败，下次刷新再从 _batchSelection 重试（意图不丢）
            }
            finally
            {
                _restoringSelection = false;
            }
        }
    }

    /// <summary>
    /// 非本地游戏（虚拟游戏）是否可见：「显示非本地游戏」开关统一口径。
    /// FilterGame 与完成度统计共用——两处口径不一致会出现"总时长跟着开关变、完成度不变"。
    /// </summary>
    private bool VirtualGameVisible(Galgame game) => Plugin.Data.DisplayVirtualGame || game.IsLocalGame;

    private bool FilterGame(object? obj)
    {
        if (obj is not Galgame game) return false;
        // 非本地游戏（虚拟游戏）默认隐藏：与原生游戏页 VirtualGameFilter 默认行为对齐，
        // 挡住云同步换机后只恢复元数据的「幽灵条目」（开启「显示非本地游戏」后放行）
        if (!VirtualGameVisible(game)) return false;
        if (!SearchHelper.ApplySearchKey(game, _searchKeyword)) return false;
        if (!MatchesPlayTypeFilter(game)) return false;
        if (!MatchesCategory(game)) return false;
        if (!MatchesForm(game)) return false;
        return MatchesRange(game);
    }

    /// <summary>工具栏搜索词（与筛选 AND 联动；持久化到 PluginData——切走再切回仍在，直到手动删除/清空）</summary>
    private string _searchKeyword = "";

    /// <summary>搜索框是否展开</summary>
    private bool _searchExpanded;

    /// <summary>
    /// 恢复搜索词进行中：抑制"恢复那一次 TextChanged"触发的 RefreshFilter。
    /// 构造器已把过滤器在首次渲染前挂好并预刷一次，若再让恢复的 TextChanged 调
    /// RefreshFilter()（内部 _source.Refresh() → GridView Reset 重绘），切回页面
    /// 就会画面闪一下（用户 2026-08-20 实测反馈）。下一次真实输入正常刷新。
    /// </summary>
    private bool _restoringSearch;

    /// <summary>
    /// 搜索框（SearchOverlay）高度：紧凑输入框标准高度 32，居中于按钮高度区域。
    /// 不能对齐到按钮控件高度（实测 48）——按钮可见内容只有图标+文字约 20 高，
    /// 48 高实心输入框视觉凸出近两倍（用户截图证实）；32 与宿主主页原生搜索框观感一致。
    /// </summary>
    private const double SearchBoxHeight = 32;

    /// <summary>
    /// 页面首次布局完成后，搜索按钮高度对齐到「功能说明」按钮（标准 AppBarButton，
    /// 高度由宿主主题决定，实测 48）；搜索框用紧凑高度 32 居中，两者视觉协调。
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded; // 一次性；CommandBar 高度不随窗口变化，无需重复对齐
        if (HelpButton is null || HelpButton.ActualHeight <= 0) return;
        double h = HelpButton.ActualHeight;
        SearchButton.Height = h;
        SearchOverlay.Height = SearchBoxHeight;
        Debug.WriteLine($"[LibraryPlus] 工具栏高度对齐：HelpButton={h:F1} → SearchButton={h:F1}，SearchOverlay={SearchBoxHeight:F1}（紧凑输入框，居中）");
    }

    /// <summary>搜索按钮：未展开则展开（聚焦输入框）；已展开则仅聚焦（点空白/失焦由 PointerPressed/LostFocus 收起）</summary>
    private void SearchToggle_Click(object sender, RoutedEventArgs e) => ExpandSearch();

    private void ExpandSearch()
    {
        if (_searchExpanded)
        {
            FocusSearchBox();
            return;
        }
        _searchExpanded = true;
        ToggleSearchState(true);
        // 强制搜索框高度=紧凑输入框高度：防 OnPageLoaded 未执行/时机偏差时高度退回内容值
        SearchOverlay.Height = SearchBoxHeight;
        AnimateSearchWidth(220);
        FocusSearchBox();
        Debug.WriteLine($"[LibraryPlus] 搜索框展开：Button={SearchButton.ActualHeight:F1} Overlay={SearchOverlay.ActualHeight:F1}");
    }

    /// <summary>
    /// 让焦点进入搜索框。容器刚由 Collapsed→Visible 时内容可能尚未完成加载/模板实例化，
    /// 立即 Focus() 会静默失败——焦点没进搜索框，之后点击别处就不会触发 LostFocus，
    /// 自动收起整条事件链断裂（尝试 6 失败的直接原因）。直接聚焦失败则挂 Loaded 等模板
    /// 就绪后再聚焦（一次性，聚焦后即摘除）。
    /// </summary>
    private void FocusSearchBox()
    {
        if (SearchBox.Focus(FocusState.Programmatic)) return;
        SearchBox.Loaded -= SearchBox_Loaded_Focus;
        SearchBox.Loaded += SearchBox_Loaded_Focus;
    }

    private void SearchBox_Loaded_Focus(object sender, RoutedEventArgs e)
    {
        SearchBox.Loaded -= SearchBox_Loaded_Focus;
        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>失焦收起（原生同款）：搜索词为空时点击页面空白/其他位置即收起</summary>
    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_searchExpanded && string.IsNullOrEmpty(SearchBox.Text))
            CollapseSearch();
    }

    /// <summary>
    /// 页面级指针按下兜底：点击不可聚焦区域（工具栏空白、页面空白 Grid 等）在 WinUI 中
    /// 不移动焦点，LostFocus 收不到；这里是收起的主要触发（对"点击页面空白处搜索框消失"
    /// 的语义全覆盖，不依赖焦点机制）。按下的目标不在搜索框内且输入为空 → 收起。
    /// </summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_searchExpanded) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsWithin(source, SearchOverlay)) return; // 点击搜索框自身（含清除按钮）不收起
        if (string.IsNullOrEmpty(SearchBox.Text))
            CollapseSearch();
    }

    /// <summary>判断 target 是否在 ancestor 的视觉子树内（含自身）</summary>
    private static bool IsWithin(DependencyObject target, DependencyObject ancestor)
    {
        DependencyObject? node = target;
        while (node != null)
        {
            if (node == ancestor) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void CollapseSearch()
    {
        if (!_searchExpanded) return;
        _searchExpanded = false;
        // 空输入折叠不触发刷新（避免整页刷新闪烁）；有搜索词时折叠清空并恢复列表
        bool hadKeyword = !string.IsNullOrEmpty(_searchKeyword);
        SearchBox.Text = "";
        _searchKeyword = "";
        Plugin.Data.SearchKeyword = ""; // 清除持久化：下次重建不再恢复
        if (hadKeyword) RefreshFilter();
        ToggleSearchState(false);
        AnimateSearchWidth(0);
    }

    /// <summary>按钮/搜索框显隐切换（与原生 ToggleState 一致：不展开时按钮可点，展开时搜索框可点）</summary>
    private void ToggleSearchState(bool isExpanded)
    {
        SearchButton.IsHitTestVisible = !isExpanded;
        SearchOverlay.IsHitTestVisible = isExpanded;
        SearchButton.Opacity = isExpanded ? 0 : 1;
        SearchOverlay.Opacity = isExpanded ? 1 : 0;
    }

    /// <summary>展开/收起宽度动画（搜索框右对齐，宽度增长即向左扩展，覆盖按钮与左侧功能说明）</summary>
    private void AnimateSearchWidth(double targetWidth)
    {
        try
        {
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                From = SearchOverlay.Width,
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(180),
                EnableDependentAnimation = true, // Width 是布局依赖属性，必须启用
            };
            Storyboard.SetTarget(animation, SearchOverlay);
            Storyboard.SetTargetProperty(animation, "Width");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            // 动画失败直接设置宽度
            SearchOverlay.Width = targetWidth;
        }
    }

    /// <summary>搜索框输入：实时过滤（复用宿主原生搜索语义）</summary>
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput &&
            args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            return;
        if (_restoringSearch)
        {
            // 恢复场景：过滤器已由构造器在首次渲染前应用，跳过这次刷新（避免 GridView Reset 重绘闪烁）；
            // 同时消费标记，保证之后的真实输入正常实时过滤
            _restoringSearch = false;
            _searchKeyword = sender.Text ?? "";
            Plugin.Data.SearchKeyword = _searchKeyword;
            return;
        }
        _searchKeyword = sender.Text ?? "";
        Plugin.Data.SearchKeyword = _searchKeyword; // 持久化：页面重建/切回后恢复（清空时置 "" = 手动删除）
        RefreshFilter();
    }

    /// <summary>内容轴分类匹配（萌作/剧情作/拔作/其他），All=全部；与形态轴、时长区间、状态筛选 AND 联动</summary>
    private bool MatchesCategory(Galgame game)
    {
        string key = Plugin.Data.CategoryKey;
        if (key == CategoryKeyAll) return true;
        // silent：筛选遍历全库，不打分类命中日志（防每次刷新刷屏）
        return GalgameClassifier.ClassifyContent(game, silent: true).ToString() == key;
    }

    /// <summary>形态轴分类匹配（传统ADV/非传统ADV），All=全部</summary>
    private bool MatchesForm(Galgame game)
    {
        string key = Plugin.Data.FormKey;
        if (key == CategoryKeyAll) return true;
        return GalgameClassifier.ClassifyForm(game).ToString() == key;
    }

    /// <summary>内容分类按钮点击：持久化并刷新列表与统计</summary>
    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string key) return;
        Plugin.Data.CategoryKey = key; // 持久化
        RefreshFilter();
    }

    /// <summary>形态分类按钮点击：持久化并刷新列表与统计</summary>
    private void Form_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string key) return;
        Plugin.Data.FormKey = key; // 持久化
        RefreshFilter();
    }

    /// <summary>从持久化数据恢复内容分类按钮选中状态（旧版「Doujin」键已退役，兜底置为全部）</summary>
    private void RestoreCategoryState()
    {
        string key = Plugin.Data.CategoryKey;
        if (key is not ("All" or "Moe" or "Story" or "Nukige" or "Other")) key = "All";
        CategoryAll.IsChecked = key == CategoryKeyAll;
        CategoryMoe.IsChecked = key == "Moe";
        CategoryStory.IsChecked = key == "Story";
        CategoryNukige.IsChecked = key == "Nukige";
        CategoryOther.IsChecked = key == "Other";
    }

    /// <summary>从持久化数据恢复形态分类按钮选中状态</summary>
    private void RestoreFormState()
    {
        string key = Plugin.Data.FormKey;
        if (key is not ("All" or "TraditionalAdv" or "NonTraditionalAdv")) key = "All";
        FormAll.IsChecked = key == CategoryKeyAll;
        FormTraditional.IsChecked = key == "TraditionalAdv";
        FormNonTraditional.IsChecked = key == "NonTraditionalAdv";
    }

    /// <summary>
    /// 状态筛选（包含式）：All=全部；ToPlay=待玩预设（未标记+游玩中+想玩）；
    /// Custom=只显示 IncludedPlayTypes 中的状态（空列表视为全部）。
    /// </summary>
    private bool MatchesPlayTypeFilter(Galgame game)
    {
        string mode = Plugin.Data.FilterMode;
        if (mode == FilterModeAll) return true;
        if (mode == FilterModeToPlay)
            return game.PlayType is PlayType.None or PlayType.Playing or PlayType.WantToPlay;

        // Custom
        List<string> included = Plugin.Data.IncludedPlayTypes;
        return included.Count == 0 || included.Contains(game.PlayType.ToString());
    }

    /// <summary>时长区间按钮点击：持久化并刷新列表与统计</summary>
    private void Range_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string key) return;
        Plugin.Data.RangeKey = key; // 持久化
        _source.Filter = FilterGame;
        _source.Refresh();
        UpdateCountText();
        UpdateStatsText();
    }

    /// <summary>
    /// 时长区间匹配：<10h（短篇小品）/ 10-20h（单线）/ 20-40h（中等）/ &gt;40h（长篇）/ 时长未知。
    /// 时长未知的游戏不算入四个时长区间，仅归入「时长未知」档（或在"全部"中可见）。
    /// </summary>
    private bool MatchesRange(Galgame game)
    {
        string key = Plugin.Data.RangeKey;
        if (key == RangeKeyAll) return true;

        long? minutes = ExpectedPlayTimeHelper.ParseMinutes(game.ExpectedPlayTime?.Value);
        if (key == "Unknown") return minutes is null;
        if (minutes is null) return false; // 未知时长不算入任何时长区间
        double hours = minutes.Value / 60.0;

        return key switch
        {
            "Under10" => hours < 10,
            "10to20" => hours >= 10 && hours < 20,
            "20to40" => hours >= 20 && hours < 40,
            "Over40" => hours >= 40,
            _ => true,
        };
    }

    /// <summary>从持久化数据恢复时长区间按钮选中状态</summary>
    private void RestoreRangeState()
    {
        string key = Plugin.Data.RangeKey;
        RangeAll.IsChecked = key == RangeKeyAll;
        RangeUnder10.IsChecked = key == "Under10";
        Range10To20.IsChecked = key == "10to20";
        Range20To40.IsChecked = key == "20to40";
        RangeOver40.IsChecked = key == "Over40";
        RangeUnknown.IsChecked = key == "Unknown";
    }

    /// <summary>
    /// 恢复持久化的搜索词（页面切走再切回/应用重启后搜索内容依然显示，直到用户手动删除）：
    /// 有关键词 → 展开搜索框并显示文本；无则保持收起。
    /// 只恢复显隐/文本，不主动抢焦点；<b>不在此处触发刷新</b>——过滤器由构造器在
    /// 首次渲染前统一挂载（见构造器注释），避免切回时重复搜索导致画面闪烁。
    /// </summary>
    private void RestoreSearchState()
    {
        _searchKeyword = Plugin.Data.SearchKeyword ?? "";
        if (string.IsNullOrEmpty(_searchKeyword)) return;
        // 吸收恢复那一次 TextChanged：只记状态，不触发 RefreshFilter（构造器已预挂过滤器）
        _restoringSearch = true;
        SearchBox.Text = _searchKeyword;
        _searchExpanded = true;
        SearchOverlay.Height = SearchBoxHeight;
        ToggleSearchState(true);
        // 恢复场景直接铺开宽度（不播展开动画），避免切回时闪动
        SearchOverlay.Width = 220;
        Debug.WriteLine($"[LibraryPlus] 搜索词恢复: \"{_searchKeyword}\"");
    }

    /// <summary>更新左上角统计：显示当前可见（排除+区间生效后）数量</summary>
    private void UpdateCountText()
    {
        CountTextBlock.Text = $"共 {_source.Count} 款游戏";
    }

    /// <summary>
    /// 更新统计条（跟随当前可见列表）：待玩总时长 / 完成度 / 时长未知数。
    /// 完成度动态化：左侧三个筛选（时长/内容分类/形态）全部为「全部」→ 全局完成度（已玩/全库）；
    /// 任一筛选非「全部」→ 该筛选条件下的完成度（已玩/当前分类子集，与状态筛选无关）。
    /// 双轴分类统计：内容轴（萌/剧情/拔/其他）+ 形态轴（传统ADV/非传统ADV）。
    /// </summary>
    private void UpdateStatsText()
    {
        long totalMinutes = 0;
        int unknown = 0;
        var categoryCounts = new Dictionary<GalgameCategory, int>();
        var formCounts = new Dictionary<GalgameForm, int>();
        foreach (Galgame g in _source.OfType<Galgame>())
        {
            long? minutes = ExpectedPlayTimeHelper.ParseMinutes(g.ExpectedPlayTime?.Value);
            if (minutes is null) unknown++;
            else totalMinutes += minutes.Value;

            GalgameCategory cat = GalgameClassifier.ClassifyContent(g, silent: true);
            categoryCounts[cat] = categoryCounts.GetValueOrDefault(cat) + 1;
            GalgameForm form = GalgameClassifier.ClassifyForm(g);
            formCounts[form] = formCounts.GetValueOrDefault(form) + 1;
        }
        TotalTimeText.Text = $"待玩总时长：{ExpectedPlayTimeHelper.FormatHours(totalMinutes)}";

        // 完成度：左侧三个筛选全「全部」→ 全局；否则按三筛选条件（时长/内容/形态，不含状态筛选）的子集统计。
        // 从未过滤源出发是原设计（刻意绕开状态筛选与搜索词），但「显示非本地游戏」开关必须同样生效，
        // 与待玩总时长/时长未知（走过滤后视图）口径一致
        bool isGlobal = Plugin.Data.RangeKey == RangeKeyAll &&
                        Plugin.Data.CategoryKey == CategoryKeyAll &&
                        Plugin.Data.FormKey == CategoryKeyAll;
        IEnumerable<Galgame> progressSet = _source.Source.OfType<Galgame>().Where(VirtualGameVisible);
        if (!isGlobal)
            progressSet = progressSet.Where(g => MatchesRange(g) && MatchesCategory(g) && MatchesForm(g));
        int played = progressSet.Count(g => g.PlayType == PlayType.Played);
        int total = progressSet.Count();
        ProgressText.Text = isGlobal
            ? $"完成度：{played}/{total}"
            : $"完成度：{played}/{total}（当前分类）";

        UnknownTimeText.Text = $"{unknown} 款时长未知";

        // 内容轴：萌作/剧情作/拔作/其他（括号跟在"共 X 款游戏"后，表示对当前列表的总结）
        string[] order = { nameof(GalgameCategory.Moe), nameof(GalgameCategory.Story), nameof(GalgameCategory.Nukige),
            nameof(GalgameCategory.Other) };
        // 形态轴：传统ADV / 非传统ADV
        string[] formOrder = { nameof(GalgameForm.TraditionalAdv), nameof(GalgameForm.NonTraditionalAdv) };
        CategoryStatsText.Text = $"（{string.Join(" · ",
            order.Select(key => $"{GalgameClassifier.GetDisplayName(Enum.Parse<GalgameCategory>(key))} {categoryCounts.GetValueOrDefault(Enum.Parse<GalgameCategory>(key))}"))}" +
            $"｜{string.Join(" · ",
            formOrder.Select(key => $"{GalgameClassifier.GetFormDisplayName(Enum.Parse<GalgameForm>(key))} {formCounts.GetValueOrDefault(Enum.Parse<GalgameForm>(key))}"))}）";
    }

    #endregion

    /// <summary>功能简介对话框</summary>
    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        const string help = @"【kungal 数据源】
· 原生源选择器可选 Kungal；设置页选 Kungal 后可用「游戏ID」按 gid 更新
· 搜刮内容：中文简介、标签、角色中文简介、日文角色名换简体中文名、角色图片

【在Kungal中打开】
· 游戏经单次/批量 kungal 搜刮后（本地已保存 gid），详情页右上角「···」菜单会出现「在Kungal中打开」
· 未搜刮过的游戏不显示该入口

【批量搜刮】
· 「更多搜刮」：对筛选或勾选的游戏批量拉取 kungal + Bangumi 数据
· 简介规则：空或非中文才填充（已有中文简介不覆盖）
· 标签与现有合并（不删除原有标签）
· 角色：简介空/非中文才填；勾选「补充角色」可补齐缺失角色（含图片）
· 剧透角色自动跳过；完成后报告重复角色（需手动清理）

【双轴分类】
· 内容轴：萌作 / 剧情作 / 拔作 / 其他（社区投票 + 标签热度 + 基础规则）
· 形态轴：传统ADV / 非传统ADV（玩法形态判定）
· 统计条与筛选均为双轴联动，可交叉筛选
· 完成度跟随分类筛选动态统计（时长/内容/形态全「全部」时显示全局）

【手动覆盖】
· 右键游戏 → 分类 / 形态，可手动指定（多选勾选时批量应用）
· 手动设定优先于自动分类，持久化生效

【游玩记录】
· 启动守卫（默认关闭，在 记录 菜单开启）：只点开测试、本轮未达阈值就退出的新游戏，自动还原「上次游玩时间」，不再顶到原生主页「最后游玩」排序最前
· 仅对累计总时长低于阈值的游戏生效（默认 5 分钟）——已玩进去的老游戏回访完全不受影响
· 判定零等待：退出游戏时立即结算，试玩即归位、真玩不动任何数据；守卫只改时间戳，不删游玩时长
· Steam 库游戏不参与守卫（Steam 时间是官方统计，无污染）
· 清除记录：把勾选（或当前筛选）游戏的逐日游玩明细、累计时长、上次游玩时间清零（可选连次数一起），不可撤销

【数据与提示】
· 搜刮数据本地持久化（随软件数据目录，随插件卸载可清）
· 建议 kungal 搜刮放在混合搜刮之后（混合搜刮会覆盖标签）
· 未搜刮 kungal 的游戏使用基础分类（VNDB/Bangumi 标签规则）";
        var dialog = new ContentDialog
        {
            XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
            Title = "游戏库增强 - 功能简介",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    #region 更多菜单（显示设置 + 游玩记录）

    /// <summary>菜单打开时同步显示开关/守卫开关/阈值的选中态（XAML 不写 IsChecked 字面量的既定约束）</summary>
    private void MoreMenu_Opening(object sender, object e)
    {
        try
        {
            ShowVirtualGameItem.IsChecked = Plugin.Data.DisplayVirtualGame;
            GuardToggleItem.IsChecked = Plugin.Data.LaunchGuardEnabled;
            foreach (RadioMenuFlyoutItem item in new[]
                     { GuardThreshold5, GuardThreshold10, GuardThreshold15, GuardThreshold20, GuardThreshold30, GuardThreshold60 })
                item.IsChecked = int.TryParse(item.Tag as string, out int v)
                                 && v == Plugin.Data.LaunchGuardThresholdMinutes;
            VolumeNormalizeToggleItem.IsChecked = Plugin.Data.VolumeNormalizeEnabled;
            foreach (RadioMenuFlyoutItem item in new[]
                     { VolumeLevel10, VolumeLevel20, VolumeLevel30, VolumeLevel40, VolumeLevel50,
                       VolumeLevel60, VolumeLevel70, VolumeLevel80, VolumeLevel90, VolumeLevel100 })
            {
                float? f = TryParseFloatTag(item);
                item.IsChecked = f.HasValue &&
                                 Math.Abs(f.Value - Plugin.Data.VolumeNormalizeLevel) < 0.001f;
            }
        }
        catch
        {
            // 页面销毁中，忽略
        }
    }

    private void ShowVirtualGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem t) return; // Click 在状态翻转后触发，直接读当前值
        Plugin.Data.DisplayVirtualGame = t.IsChecked;     // ObservableProperty 自动持久化
        RefreshFilter();
        int count = Plugin.HostApi.GetAllGames().Count(g => !g.IsLocalGame);
        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            msg: t.IsChecked
                ? $"已显示非本地游戏：本机无游戏文件的条目共 {count} 款，恢复显示（批量操作也会包含它们）"
                : $"已隐藏非本地游戏（共 {count} 款），与原生游戏页默认行为一致");
    }

    private void GuardToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem t) return; // Click 在状态翻转后触发，直接读当前值
        Plugin.Data.LaunchGuardEnabled = t.IsChecked;     // ObservableProperty 自动持久化
        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            msg: t.IsChecked
                ? $"启动守卫已开启：短开未达 {Plugin.Data.LaunchGuardThresholdMinutes} 分钟的游戏不再顶到原生主页「最后游玩」最前"
                : "启动守卫已关闭，恢复原生行为");
    }

    private void GuardThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string s } || !int.TryParse(s, out int minutes)) return;
        Plugin.Data.LaunchGuardThresholdMinutes = Math.Max(1, minutes);
        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            msg: $"守卫阈值已设为 {minutes} 分钟：本轮真实游玩累计达到该时长才认定为「真玩了」");
    }

    /// <summary>音量规范化开关：首次启动把游戏的应用会话音量压到设定档位，压过就不再改动</summary>
    private void VolumeNormalizeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem t) return; // Click 在状态翻转后触发，直接读当前值
        Plugin.Data.VolumeNormalizeEnabled = t.IsChecked; // ObservableProperty 自动持久化
        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            msg: t.IsChecked
                ? $"音量规范化已开启：首次启动游戏时把它压到 {Plugin.Data.VolumeNormalizeLevel * 100:0}%（每款只压一次，之后尊重你的手动调整）"
                : "音量规范化已关闭，恢复原生音量行为");
    }

    /// <summary>音量规范化档位选择</summary>
    private void VolumeLevel_Click(object sender, RoutedEventArgs e)
    {
        float? level = TryParseFloatTag(sender as FrameworkElement);
        if (!level.HasValue) return;
        Plugin.Data.VolumeNormalizeLevel = Math.Clamp(level.Value, 0f, 1f);
        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            msg: $"规范化音量设为 {Plugin.Data.VolumeNormalizeLevel * 100:0}%");
    }

    /// <summary>清空音量规范化「已压过」记录：下次启动游戏时重新压一次（换设备/测试用）</summary>
    private void VolumeResetRecords_Click(object sender, RoutedEventArgs e)
    {
        Helper.VolumeNormalizer.ResetRecords();
        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            msg: $"已清空音量规范化记录：下次启动游戏时会重新压到 {Plugin.Data.VolumeNormalizeLevel * 100:0}%");
    }

    /// <summary>清空单款游戏的音量规范化记录（右键菜单）：该游戏下次启动时重新压一次</summary>
    private void ResetVolumeRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game) return;
        Helper.VolumeNormalizer.ResetRecord(game.Uuid);
        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            msg: $"已清空《{game.Name.Value}》的音量规范化记录：下次启动时会重新把应用音量压到 {Plugin.Data.VolumeNormalizeLevel * 100:0}%");
    }

    /// <summary>把 RadioMenuFlyoutItem.Tag（float 字符串）解析成浮点；解析失败返回 null</summary>
    private static float? TryParseFloatTag(FrameworkElement? item)
    {
        return item is { Tag: string s } && float.TryParse(s,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : null;
    }

    /// <summary>
    /// 清除游玩记录：对勾选集（多选模式）或当前筛选集，清空 PlayedTime / 累计时长 / 上次游玩时间，
    /// 可选连 PlayCount 一起清零。不可撤销，确认框二次把关；清完同步通知守卫丢弃相关观察状态。
    /// </summary>
    private async void ClearPlayRecord_Click(object sender, RoutedEventArgs e)
    {
        List<Galgame> games = GetBatchTargets();
        bool isSelected = games.Count > 0 &&
                          GameGridView.SelectionMode == ListViewSelectionMode.Multiple;
        if (!isSelected) games = _source.OfType<Galgame>().ToList(); // 非多选 = 当前筛选可见集
        if (games.Count == 0)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Informational, msg: "当前筛选下没有游戏");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
            Title = "清除游玩记录",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"将清除{(isSelected ? "选中的" : "当前筛选的")} {games.Count} 款游戏的游玩数据：\n" +
                               "· 游玩时长明细（逐日记录）全部删除\n" +
                               "· 累计游玩时长、上次游玩时间归零\n" +
                               "· 我的评分、游玩状态、分类等其它数据不受影响\n\n" +
                               "此操作不可撤销！注意：Steam 库游戏会被 Steam 数据重新覆盖；" +
                               "若宿主开启了 PVN 云同步，云端旧记录可能在下次同步时合并回来。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new CheckBox
                    {
                        Content = "同时清零游玩次数（PlayCount）",
                        IsChecked = false, // 代码创建的控件可以直接写
                    },
                },
            },
            PrimaryButtonText = "开始清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool clearCount = (dialog.Content as StackPanel)?.Children
            .OfType<CheckBox>().FirstOrDefault()?.IsChecked == true;

        int ok = 0, fail = 0;
        foreach (Galgame game in games)
        {
            try
            {
                LaunchGuardHelper.OnRecordsCleared(new[] { game }); // 先丢守卫状态，避免清零动作被守卫盯上
                game.PlayedTime.Clear();
                game.TotalPlayTime = 0;
                game.LastPlayTime = DateTime.MinValue;
                if (clearCount) game.PlayCount = 0;
                await HostServices.SaveGameAsync(game);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                Plugin.HostApi.Log(InfoBarSeverity.Warning, $"清除记录失败 {game.Name.Value}: {ex.Message}");
            }
        }

        // 与「更多搜刮 / 计算评分 / 批量改分类」行为对齐：批量完成后自动退出多选（清空勾选集）
        try { if (GameGridView.SelectionMode == ListViewSelectionMode.Multiple) ExitMultiSelect(); }
        catch { /* 页面销毁 */ }

        Plugin.HostApi.Info(InfoBarSeverity.Informational, title: "清除完成",
            msg: $"{ok}/{games.Count} 款游戏游玩记录已清零" +
                 (fail > 0 ? $"，失败 {fail} 款（详见插件日志）" : ""));
    }

    #endregion

    #region 插件数据导出/导入

    private async void ExportData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await PluginDataIoHelper.ExportAsync();
            if (path is not null)
                Plugin.HostApi.Info(InfoBarSeverity.Informational, title: "导出完成",
                    msg: $"插件数据已备份到：{path}");
        }
        catch (Exception ex)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Error, title: "导出失败", msg: ex.Message);
        }
    }

    private async void ImportData_Click(object sender, RoutedEventArgs e)
    {
        PluginData? data;
        try
        {
            data = await PluginDataIoHelper.ImportAsync();
        }
        catch (InvalidOperationException ex)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Error, title: "导入失败",
                msg: $"所选文件不是本插件的有效备份：{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Error, title: "导入失败", msg: ex.Message);
            return;
        }
        if (data is null) return; // 用户取消了文件选择，静默

        var dialog = new ContentDialog
        {
            XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
            Title = "导入插件数据",
            Content = new TextBlock
            {
                Text = "将用备份整体覆盖当前全部插件数据：\n" +
                       "· 页面设置（排序/筛选/搜索）\n" +
                       "· 手动分类/形态覆盖\n" +
                       "· kungal / Bangumi 搜刮缓存与加权评分缓存\n" +
                       "· 启动守卫设置\n\n覆盖后立即生效并持久化，当前数据将丢失。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "覆盖",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        Plugin.ReplaceData(data);

        // 重放页面恢复序列，让排序/筛选/搜索状态立即反映新数据（不重启软件）
        ApplySort();
        RestoreSortMenuState();
        RestoreRangeState();
        RestoreCategoryState();
        RestoreFormState();
        SyncFilterCheckBoxes();
        RestoreSearchState();
        RefreshFilter();

        Plugin.HostApi.Info(InfoBarSeverity.Informational, title: "导入完成", msg: "插件数据已替换并持久化");
    }

    #endregion

    #region 批量搜刮（kungal）

    /// <summary>
    /// 批量状态变化订阅（页面重建后恢复进度显示用）。
    /// 批量结束时（IsBatchScraping 清 false）同时恢复锁定与变灰——
    /// 重建页面在批量中订阅，旧实例 finally 的恢复只作用于旧网格，新网格靠这里恢复。
    /// </summary>
    private void OnBatchStatusChanged()
    {
        try
        {
            BatchProgressText.Text = Plugin.BatchStatus;
            if (!Plugin.IsBatchScraping && !GameGridView.IsEnabled)
            {
                GameGridView.IsEnabled = true;
                GameGridView.Opacity = 1.0;
            }
        }
        catch
        {
            // 页面已销毁，忽略
        }
    }

    /// <summary>
    /// 多选勾选变化 → 增量同步到独立勾选集（跨筛选保留）。
    /// 必须增量（不能用 Clear+全量收集）：SelectedItems 只含「当前可见」的选中——
    /// 在剧情分类下勾选新游戏时全量重建会把萌作分类下勾选（当前不可见）的项丢掉。
    /// 视图重建（Refresh）导致的批量清空在 _restoringSelection 屏蔽期内，不会误删。
    /// </summary>
    private void GameGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return; // 恢复勾选过程中不同步（防自清）
        if (GameGridView.SelectionMode != ListViewSelectionMode.Multiple) return;
        foreach (object item in e.AddedItems)
            if (item is Galgame g) _batchSelection.Add(g.Uuid);
        foreach (object item in e.RemovedItems)
            if (item is Galgame g) _batchSelection.Remove(g.Uuid);
    }

    /// <summary>多选开关：开启后网格进入多选模式（勾选），批量搜刮优先作用于选中游戏</summary>
    private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
    {
        bool multi = MultiSelectToggle.IsChecked == true;
        if (multi)
        {
            GameGridView.SelectionMode = ListViewSelectionMode.Multiple;
        }
        else
        {
            ExitMultiSelect();
        }
        GameGridView.IsMultiSelectCheckBoxEnabled = multi;
    }

    /// <summary>退出多选模式（批量操作完成后自动调用，免手动再点一次开关）。</summary>
    private void ExitMultiSelect()
    {
        try { MultiSelectToggle.IsChecked = false; } catch { /* 页面销毁 */ }
        // 必须先 Clear 再切 None：None 模式下 SelectedItems 是无效集合，Clear 会抛 E_UNEXPECTED（框架 bug）
        try
        {
            GameGridView.SelectedItems.Clear();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 框架 bug：忽略
        }
        try { GameGridView.SelectionMode = ListViewSelectionMode.None; } catch { /* 页面销毁 */ }
        _batchSelection.Clear(); // 关闭多选清空勾选集
        try { GameGridView.IsMultiSelectCheckBoxEnabled = false; } catch { /* 页面销毁 */ }
    }

    /// <summary>当前操作目标游戏集：多选模式有勾选 → 用完整勾选集（_batchSelection，跨分类——从完整
    /// 数据源取，不只当前分类可见的）；否则单游戏（右键的/当前游戏）</summary>
    private List<Galgame> GetBatchTargets(Galgame? fallback = null)
    {
        if (GameGridView.SelectionMode == ListViewSelectionMode.Multiple && _batchSelection.Count > 0)
            return _source.Source.OfType<Galgame>().Where(g => _batchSelection.Contains(g.Uuid)).ToList();
        return fallback is { } f ? new List<Galgame> { f } : _source.OfType<Galgame>().ToList();
    }

    /// <summary>
    /// 批量搜刮：对当前筛选可见的游戏全部用 kungal 搜刮。
    /// 简介规则：空或非中文才填，已有中文简介不动；标签：原 tags ∪ kungal tags（去重）。
    /// 同时把 kungal 完整数据（tag 全量 + 类型投票）存入 PluginData，供 M3 投票分类使用。
    /// </summary>
    private async void BatchScrape_Click(object sender, RoutedEventArgs e)
    {
        List<Galgame> games = GetBatchTargets();
        bool isSelected = games.Count > 0 &&
                          GameGridView.SelectionMode == ListViewSelectionMode.Multiple;
        if (!isSelected) games = _source.OfType<Galgame>().ToList(); // 批量按钮非多选时 = 筛选集
        if (games.Count == 0)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Informational, msg: "当前筛选下没有游戏");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
            Title = "批量搜刮（kungal）",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"将对{(isSelected ? "选中的" : "当前筛选的")} {games.Count} 款游戏用 kungal 搜刮：\n" +
                               "· 简介：仅填充为空或非中文的（已有中文简介不覆盖）\n" +
                               "· 角色简介：并发拉取，仅填充为空或非中文的（按 VNDB/Bangumi 角色 id 或名称匹配）\n" +
                               "· 角色名：日文名角色替换为 Bangumi 简体中文名（有则替换）\n" +
                               "· 标签：与现有标签合并（不删除原有标签）\n" +
                               "· 重复检测：完成后报告 bgm/vndb 角色 id 相同的重复角色（不自动删）\n" +
                               $"预计耗时约 {games.Count * 6 / 60 + 1} 分钟（含网络请求节流）",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new CheckBox
                    {
                        Content = "补充 kungal 有而库中没有的角色（含简体中文名与简介）",
                        IsChecked = false, // 代码设置选中态（插件 XAML 红线不适用代码创建）
                    },
                },
            },
            PrimaryButtonText = "开始搜刮",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool addMissing = (dialog.Content as StackPanel)?.Children
            .OfType<CheckBox>().FirstOrDefault()?.IsChecked == true;

        // 防重入：批量进行中禁用按钮，finally 恢复；与「计算评分」互斥
        if (sender is AppBarButton scrapeButton) scrapeButton.IsEnabled = false;
        try { BatchRatingButton.IsEnabled = false; } catch { /* 页面销毁 */ }
        // 批量期间禁用网格点击（防止导航销毁页面——宿主导航不取消异步方法，
        // 页面销毁后控件赋值会抛 COMException，async void 未处理异常会崩宿主）
        GameGridView.IsEnabled = false;
        GameGridView.Opacity = 0.5; // 变灰特效
        // 全局批量状态（页面切走再切回时恢复锁定与进度）
        Plugin.IsBatchScraping = true;
        Plugin.BatchStatus = "批量搜刮准备中…";
        Plugin.BatchStatusChanged?.Invoke();

        // Bangumi tag 采集（需宿主已登录 Bangumi；token 拿不到则跳过，不影响 kungal 功能）
        var bgmClient = new BgmClient { Token = await HostServices.GetBgmTokenAsync() };
        int bgmOk = 0;

        int ok = 0, noMatch = 0, fail = 0, locked = 0, charApplied = 0, charRenamedTotal = 0, charAddedTotal = 0, dupTotal = 0;
        var dupGames = new List<(string GameName, List<(string Name, int Count)> Dups)>();
        try
        {
            for (int i = 0; i < games.Count; i++)
            {
                Galgame game = games[i];
                // 更新全局进度（新页面/旧页面都可见）+ 本地进度文本（页面在才显示）
                Plugin.BatchStatus = $"搜刮中 {i + 1}/{games.Count}：{game.Name.Value}";
                Plugin.BatchStatusChanged?.Invoke();
                try { BatchProgressText.Text = Plugin.BatchStatus; }
                catch { /* 页面已销毁，忽略 */ }
                try
                {
                    // Bangumi tag 投票采集（独立于 kungal——有 Bangumi ID 就拉，无论 kungal 成败）
                    string? bgmId = game.Ids[(int)RssType.Bangumi];
                    if (!string.IsNullOrEmpty(bgmId) && bgmId != "-1" &&
                        int.TryParse(bgmId, out int bgmSubjectId))
                    {
                        List<BgmTag>? bgmTags = await bgmClient.GetTagsAsync(bgmSubjectId);
                        if (bgmTags is { Count: > 0 })
                        {
                            // 必须新建字典实例再赋值：赋回同一引用时 SetProperty 判等不触发
                            // PropertyChanged → SaveData 不执行（数据只在内存、不持久化）
                            var bgmDict = new Dictionary<string, List<BgmTagData>>(Plugin.Data.BgmData)
                            {
                                [game.Uuid.ToString()] = bgmTags
                                    .Select(t => new BgmTagData { Name = t.Name, Count = t.Count }).ToList()
                            };
                            Plugin.Data.BgmData = bgmDict;
                            bgmOk++;
                        }
                    }

                    var fetched = await Plugin.StaticPhraser.FetchDetailAsync(game);
                    if (fetched == null)
                    {
                        noMatch++;
                        continue;
                    }
                    Galgame result = Plugin.StaticPhraser.BuildResult(game, fetched.Value.Detail);
                    if (ApplyBatchResult(game, result)) locked++;

                    // 角色简介 + 简体中文名 + 补齐缺失角色 + 角色图片：
                    // 并发拉取 kungal 角色详情；简介空/非中文才替换，日文名角色从 bgm 页面换简体中文名；
                    // 失败不影响主流程（单角色失败已内部跳过）
                    try
                    {
                        var (charApps, charRenamed, charAddedChars, charNeedsImages) =
                            await Plugin.StaticPhraser.FetchCharacterIntrosAsync(game, fetched.Value.Detail, addMissing);
                        foreach (var (character, intro) in charApps)
                        {
                            character.Summary = intro;
                            charApplied++;
                        }
                        charRenamedTotal += charRenamed;
                        charAddedTotal += charAddedChars.Count;
                        // 缺图角色并发下载图片（反射宿主 DownloadHelper 存宿主 images 目录；失败保持默认图）
                        if (charNeedsImages.Count > 0)
                        {
                            using var imgSem = new SemaphoreSlim(4);
                            var imgTasks = charNeedsImages.Select(async c =>
                            {
                                await imgSem.WaitAsync();
                                try { await HostServices.DownloadCharacterImagesAsync(c); }
                                finally { imgSem.Release(); }
                            });
                            await Task.WhenAll(imgTasks);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.HostApi.Log(InfoBarSeverity.Warning,
                            $"角色简介搜刮失败: {game.Name.Value} ({ex.Message})");
                    }

                    // 采集完整 kungal 数据到 PluginData（新建实例赋值，保证触发持久化）
                    var dict = new Dictionary<string, KungalGameData>(Plugin.Data.KungalData)
                    {
                        [game.Uuid.ToString()] = KungalPhraser.BuildKungalData(fetched.Value.Detail)
                    };
                    Plugin.Data.KungalData = dict;

                    await HostServices.SaveGameAsync(game);
                    ok++;

                    // 重复角色检测（只报告不删除：bgm/vndb 角色 id 相同 = 确定重复，由用户处置）
                    var dups = KungalPhraser.DetectDuplicateCharacters(game);
                    if (dups.Count > 0)
                    {
                        dupTotal += dups.Sum(d => d.Count - 1);
                        dupGames.Add((game.Name.Value, dups));
                        Plugin.HostApi.Log(InfoBarSeverity.Warning,
                            $"检测到重复角色: {game.Name.Value} " +
                            $"{string.Join("、", dups.Select(d => $"{d.Name}（{d.Count} 个）"))}");
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    Plugin.HostApi.Log(InfoBarSeverity.Warning,
                        $"批量搜刮失败: {game.Name.Value} ({ex.Message})");
                }
            }
        }
        finally
        {
            // 页面可能已被导航销毁：所有控件访问必须防御（已销毁控件的属性设置会抛 COMException，
            // 若在 finally 里逃逸会中断 async void 方法——批量"看起来被打断"）
            Plugin.IsBatchScraping = false; // 先清全局状态，再恢复 UI
            Plugin.BatchStatus = "";
            Plugin.BatchStatusChanged?.Invoke();
            try { BatchProgressText.Text = ""; } catch { }
            try { if (sender is AppBarButton restoreButton) restoreButton.IsEnabled = true; } catch { }
            try { BatchRatingButton.IsEnabled = true; } catch { } // 与「计算评分」互斥解除
            try { GameGridView.IsEnabled = true; GameGridView.Opacity = 1.0; } catch { }
            try { if (GameGridView.SelectionMode == ListViewSelectionMode.Multiple) ExitMultiSelect(); } catch { } // 批量完成自动退出多选
            try { OnHostPhrased(); } catch { } // 当前页面刷新（页面若已销毁则静默）
            HostServices.TriggerPhrased(); // 触发宿主事件：主页/详情/重建后的扩展库页统一刷新
        }

        // 重复角色明细对话框（InfoBar 只有 10 秒且只有总数——具体游戏/角色在此列全）
        if (dupGames.Count > 0)
        {
            try
            {
                var lines = dupGames.Select(g =>
                    $"· {g.GameName}：{string.Join("、", g.Dups.Select(d => $"{d.Name}（{d.Count} 个）"))}");
                var dupDialog = new ContentDialog
                {
                    XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
                    Title = $"检测到重复角色（共 {dupTotal} 个）",
                    Content = $"以下游戏存在 bgm/vndb 角色 id 相同的重复角色：\n\n{string.Join("\n", lines)}\n\n" +
                              "插件不会自动删除，请在游戏详情页手动删除多余的角色。",
                    CloseButtonText = "知道了",
                };
                await dupDialog.ShowAsync();
            }
            catch { /* 窗口/页面已销毁则跳过（汇总里仍可见数量） */ }
        }

        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            title: "批量搜刮完成",
            msg: $"成功 {ok} / 简介锁定未动 {locked} / 未匹配 {noMatch} / 失败 {fail}" +
                 (bgmClient.Token != null ? $" / Bangumi 标签 {bgmOk} 款" : "") +
                 $" / 角色简介 {charApplied} 个 / 角色改名 {charRenamedTotal} 个" +
                 (charAddedTotal > 0 ? $" / 新增角色 {charAddedTotal} 个" : "") +
                 (dupTotal > 0 ? $" / 检测到重复角色 {dupTotal} 个" : ""),
            displayTimeMs: 10000); // 10 秒，可读完整汇总
        Plugin.HostApi.Log(InfoBarSeverity.Informational,
            $"批量搜刮完成: ok={ok} locked={locked} noMatch={noMatch} fail={fail} " +
            $"bgmToken={bgmClient.Token != null} bgmOk={bgmOk} charApplied={charApplied} " +
            $"charRenamed={charRenamedTotal} charAdded={charAddedTotal} dupDetected={dupTotal}");
    }

    /// <summary>
    /// 批量计算评分：对当前筛选/勾选的游戏批量拉取 bangumi + vndb 官方评分（复用详情页同款逻辑），
    /// 写入 RatingCache 供「按加权评分」排序与详情页卡片使用。
    /// 确认框可勾选/取消「跳过已有缓存」：默认增量（跳过已缓存的）；取消勾选则全部重算
    /// （如补了 bangumi/vndb id 后需要重新拉取）。
    /// </summary>
    private async void BatchRating_Click(object sender, RoutedEventArgs e)
    {
        List<Galgame> games = GetBatchTargets();
        bool isSelected = games.Count > 0 &&
                          GameGridView.SelectionMode == ListViewSelectionMode.Multiple;
        if (!isSelected) games = _source.OfType<Galgame>().ToList(); // 非多选时 = 筛选集
        if (games.Count == 0)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Informational, msg: "当前筛选下没有游戏");
            return;
        }

        // 默认增量：跳过已有缓存的游戏（含「无评分」标记——避免反复拉取无 id 的游戏）；
        // 用户可取消勾选强制全量重算（如补了 bangumi/vndb id 后需要重新拉取）
        bool skipCached = true;
        int cachedCount = games.Count(g => Plugin.Data.RatingCache.ContainsKey(g.Uuid.ToString()));
        var dialog = new ContentDialog
        {
            XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
            Title = "批量计算评分",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"将对{(isSelected ? "选中的" : "当前筛选的")} {games.Count} 款游戏拉取评分：\n" +
                               "· bangumi：官方 API 按 id 直查（未登录时走搜索兜底）\n" +
                               "· vndb：官方 API 按 id 直查",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new CheckBox
                    {
                        Content = $"跳过已有评分缓存的游戏（本次将跳过 {cachedCount} 款；取消勾选则全部重新计算）",
                        IsChecked = true, // 代码设置选中态
                    },
                },
            },
            PrimaryButtonText = "开始计算",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        skipCached = (dialog.Content as StackPanel)?.Children
            .OfType<CheckBox>().FirstOrDefault()?.IsChecked == true;

        // 目标集：勾选跳过 → 仅无缓存的；取消勾选 → 全部重算
        List<Galgame> pending = skipCached
            ? games.Where(g => !Plugin.Data.RatingCache.ContainsKey(g.Uuid.ToString())).ToList()
            : games;
        int skip = games.Count - pending.Count;
        if (pending.Count == 0)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Informational,
                title: "计算评分",
                msg: $"当前 {games.Count} 款游戏均已计算过评分" + (skip > 0 ? $"（本次跳过 {skip} 款）" : ""));
            return;
        }

        // 防重入 + 锁定（与批量搜刮同一套机制；页面切走再切回时恢复锁定与进度）；与「更多搜刮」互斥
        if (sender is AppBarButton ratingButton) ratingButton.IsEnabled = false;
        try { BatchScrapeButton.IsEnabled = false; } catch { /* 页面销毁 */ }
        GameGridView.IsEnabled = false;
        GameGridView.Opacity = 0.5;
        Plugin.IsBatchScraping = true;
        Plugin.BatchStatus = "评分计算准备中…";
        Plugin.BatchStatusChanged?.Invoke();

        int ok = 0, fail = 0;
        try
        {
            for (int i = 0; i < pending.Count; i++)
            {
                Galgame game = pending[i];
                Plugin.BatchStatus = $"计算评分中 {i + 1}/{pending.Count}：{game.Name.Value}";
                Plugin.BatchStatusChanged?.Invoke();
                try { BatchProgressText.Text = Plugin.BatchStatus; }
                catch { /* 页面已销毁，忽略 */ }
                try
                {
                    RatingData? rating = await Plugin.FetchRatingAsync(game, force: !skipCached);
                    if (rating is not null && (rating.BgmScore > 0 || rating.VndbScore > 0))
                        ok++;
                    else
                        fail++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Plugin.HostApi.Log(InfoBarSeverity.Warning,
                        $"批量评分失败: {game.Name.Value} ({ex.Message})");
                }
            }
        }
        finally
        {
            Plugin.IsBatchScraping = false; // 先清全局状态，再恢复 UI
            Plugin.BatchStatus = "";
            Plugin.BatchStatusChanged?.Invoke();
            try { BatchProgressText.Text = ""; } catch { }
            try { if (sender is AppBarButton restoreButton) restoreButton.IsEnabled = true; } catch { }
            try { BatchScrapeButton.IsEnabled = true; } catch { } // 与「更多搜刮」互斥解除
            try { GameGridView.IsEnabled = true; GameGridView.Opacity = 1.0; } catch { }
            try { if (GameGridView.SelectionMode == ListViewSelectionMode.Multiple) ExitMultiSelect(); } catch { } // 批量完成自动退出多选
            try { OnHostPhrased(); } catch { } // 刷新列表（按加权评分排序立即生效）
        }

        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            title: "批量评分完成",
            msg: $"成功 {ok} / 无评分或失败 {fail}" + (skip > 0 ? $" / 跳过已有缓存 {skip}" : ""),
            displayTimeMs: 8000);
        Plugin.HostApi.Log(InfoBarSeverity.Informational,
            $"批量评分完成: ok={ok} fail={fail} skip={skip} total={games.Count}");
    }

    /// <summary>把搜刮结果应用到游戏对象（批量不走宿主 ParseAsync，自行实现合并）</summary>
    /// <returns>简介是否因锁定跳过（供汇总统计）</returns>
    private static bool ApplyBatchResult(Galgame game, Galgame result)
    {
        bool descriptionLocked = false;
        // 简介：空或非中文才填（已有中文简介不动）；IsLock 由 LockableProperty setter 自行拦截
        if (!KungalPhraser.IsChinese(game.Description.Value) &&
            !string.IsNullOrWhiteSpace(result.Description.Value))
        {
            if (game.Description.IsLock)
                descriptionLocked = true; // 用户锁了简介，搜刮不覆盖（锁的语义）
            else
                game.Description.Value = result.Description.Value;
        }

        // 标签：整体替换为 result.Tags（= 原 ∪ kungal），用增量修改防绑定异常（宿主 SyncCollection 同款语义）；
        // 尊重 Tags.IsLock（与宿主 ParseAsync 行为对齐：锁了就不动）
        if (result.Tags.Value is { } newTags && game.Tags.Value is { } current && !game.Tags.IsLock)
        {
            foreach (string t in current.Where(t => !newTags.Contains(t)).ToList())
                current.Remove(t);
            foreach (string t in newTags.Where(t => !current.Contains(t)).ToList())
                current.Add(t);
        }

        return descriptionLocked;
    }

    #endregion

    #region 游戏交互（与原生游戏库页一致）

    private void GameGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (GameGridView.SelectionMode == ListViewSelectionMode.Multiple) return; // 多选模式只勾选，不导航
        if (e.ClickedItem is not Galgame game) return;
        // 详情页隶属于「游戏」页面：先把侧边栏选中指示器移动到游戏项，
        // 导航完成后清除选中（详情页不显示蓝条，对齐原生行为，保证每次一致）。
        // 注意：不要用 DispatcherQueue（宿主旧版 WinUI 无该 API，会闪退），统一走宿主 InvokeOnMainThread。
        SidebarSelectionHelper.SelectHome();
        Plugin.HostApi.NavigateTo(PageEnum.GalgamePage, new GalgamePageNavParameter { Galgame = game });
        Plugin.HostApi.InvokeOnMainThread(() => SidebarSelectionHelper.ClearSelection());
    }

    private void GameFlyout_Opening(object sender, object e)
    {
        if (sender is MenuFlyout flyout && flyout.Target?.DataContext is Galgame game)
        {
            _currentGame = game;
            StatusPlaying.IsChecked = game.PlayType == PlayType.Playing;
            StatusPlayed.IsChecked = game.PlayType == PlayType.Played;
            StatusShelved.IsChecked = game.PlayType == PlayType.Shelved;
            StatusAbandoned.IsChecked = game.PlayType == PlayType.Abandoned;
            StatusWantToPlay.IsChecked = game.PlayType == PlayType.WantToPlay;
            OpenInExplorerItem.IsEnabled = game.IsLocalGame;

            // 分类子菜单选中态：手动覆盖优先显示，否则"自动分类"
            string? manual = Plugin.Data.UserCategory.GetValueOrDefault(game.Uuid.ToString());
            CatAuto.IsChecked = string.IsNullOrEmpty(manual);
            CatMoe.IsChecked = manual == nameof(GalgameCategory.Moe);
            CatStory.IsChecked = manual == nameof(GalgameCategory.Story);
            CatNukige.IsChecked = manual == nameof(GalgameCategory.Nukige);
            CatOther.IsChecked = manual == nameof(GalgameCategory.Other);

            // 形态子菜单选中态
            string? manualForm = Plugin.Data.UserForm.GetValueOrDefault(game.Uuid.ToString());
            FormAuto.IsChecked = string.IsNullOrEmpty(manualForm);
            FormTrad.IsChecked = manualForm == nameof(GalgameForm.TraditionalAdv);
            FormNonTrad.IsChecked = manualForm == nameof(GalgameForm.NonTraditionalAdv);
        }
    }

    /// <summary>手动形态：设置/清除形态覆盖（Auto=清除）。
    /// 多选模式且有勾选 → 应用到全部选中；否则只改右键的游戏。持久化并即时刷新。</summary>
    private void SetForm_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game || sender is not RadioMenuFlyoutItem item ||
            item.CommandParameter is not string key) return;

        var targets = GetBatchTargets(game); // 多选选中集（含右键项）；非多选 = 当前右键游戏
        Plugin.HostApi.Log(InfoBarSeverity.Informational,
            $"手动形态: 目标 {targets.Count} 个 [{string.Join(",", targets.Select(g => g.Name.Value).Take(6))}] 勾选集={_batchSelection.Count}");
        // 新建实例赋值：赋回同一引用时 SetProperty 判等不触发持久化（数据只在内存）
        var dict = new Dictionary<string, string>(Plugin.Data.UserForm);
        foreach (Galgame g in targets)
        {
            if (key == "Auto") dict.Remove(g.Uuid.ToString());
            else dict[g.Uuid.ToString()] = key;
        }
        Plugin.Data.UserForm = dict; // 新实例 → 触发持久化
        RefreshFilter(); // 内部会刷新统计与列表
    }

    /// <summary>手动分类：设置/清除分类覆盖（Auto=清除）。
    /// 多选模式且有勾选 → 应用到全部选中；否则只改右键的游戏。持久化并即时刷新。</summary>
    private void SetCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game || sender is not RadioMenuFlyoutItem item ||
            item.CommandParameter is not string key) return;

        var targets = GetBatchTargets(game); // 多选选中集（含右键项）；非多选 = 当前右键游戏
        Plugin.HostApi.Log(InfoBarSeverity.Informational,
            $"手动分类: 目标 {targets.Count} 个 [{string.Join(",", targets.Select(g => g.Name.Value).Take(6))}] 多选模式={GameGridView.SelectionMode == ListViewSelectionMode.Multiple} 勾选集={_batchSelection.Count}");
        var dict = new Dictionary<string, string>(Plugin.Data.UserCategory);
        foreach (Galgame g in targets)
        {
            if (key == "Auto") dict.Remove(g.Uuid.ToString());
            else dict[g.Uuid.ToString()] = key;
        }
        Plugin.Data.UserCategory = dict; // 新实例 → 触发持久化
        RefreshFilter(); // 内部会刷新统计与列表
    }

    private void ChangePlayStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game || sender is not MenuFlyoutItem item) return;
        if (!Enum.TryParse((string)item.CommandParameter, out PlayType playType)) return;

        game.PlayType = playType;
        _ = HostServices.SaveGameAsync(game);
    }

    /// <summary>待执行的编辑导航（EditGame_Click 设置，Flyout 彻底关闭后消费）</summary>
    private Galgame? _pendingEditNavGame;

    private void EditGame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game) return;
        // 右键菜单的点击处理期间（乃至菜单关闭动画阶段）同步构建编辑页会病态膨胀到 ~3000ms，
        // 排队下一拍（InvokeOnMainThread/TryEnqueue）也躲不开；而左键 ItemClick 同一页面仅 100-200ms
        // （A/B 实测）。唯一可靠的时机是 MenuFlyout.Closed（菜单彻底关闭）之后再排一拍执行导航。
        // 宿主原生 HomeViewModel.GalFlyOutEdit 也有同款"延迟导航"处理并注明修复文字渲染问题。
        _pendingEditNavGame = game;
        GameFlyout.Closed += EditNav_FlyoutClosed;
    }

    private void EditNav_FlyoutClosed(object sender, object args)
    {
        GameFlyout.Closed -= EditNav_FlyoutClosed;
        if (_pendingEditNavGame is not { } game) return;
        _pendingEditNavGame = null;
        Plugin.HostApi.InvokeOnMainThread(() =>
            Plugin.HostApi.NavigateTo(PageEnum.GalgameSettingPage, game));
    }

    private async void FetchInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game) return;
        try
        {
            await HostServices.ParseGalInfoAsync(game);
        }
        catch (Exception ex)
        {
            // 反射 Invoke 会把宿主方法内部异常包装成 TargetInvocationException，取 InnerException 显示真实原因
            Exception real = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            Plugin.HostApi.Log(InfoBarSeverity.Warning, $"下载游戏信息失败: {ex}\nInner: {real}");
            Plugin.HostApi.Info(InfoBarSeverity.Error, msg: $"下载游戏信息失败：{real.Message}");
        }
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { LocalPath: { } path }) return;
        try
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Error, msg: $"打开文件夹失败：{ex.Message}");
        }
    }

    private void OpenTodayLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? logPath = HostServices.GetHostDailyLogPath();
            if (string.IsNullOrEmpty(logPath))
            {
                Plugin.HostApi.Info(InfoBarSeverity.Warning,
                    msg: "当天日志不存在（宿主尚未写日志，或非 MSIX/便携模式无法定位）");
                return;
            }
            // UseShellExecute：交给系统按 .txt 默认关联打开（记事本/VS Code 等）
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Error, msg: $"打开日志失败：{ex.Message}");
        }
    }

    private async void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
            Title = "从游戏库删除",
            Content = $"确定要将「{game.Name.Value}」从游戏库移除吗？（不会删除磁盘文件）",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await HostServices.RemoveGameAsync(game);
        _source.Source.Remove(game);
        _source.Refresh();
        UpdateCountText();
        UpdateStatsText();
    }

    #endregion
}
