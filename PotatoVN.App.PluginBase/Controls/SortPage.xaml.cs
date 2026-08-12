using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Collections;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.NavigationApi;
using GalgameManager.WinApp.Base.Contracts.NavigationApi.NavigateParameters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Helper;
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

    public SortPage()
    {
        XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);

        // 页面重建（如从详情页返回）后，把侧边栏选中指示器移回「更多排序」
        SidebarSelectionHelper.SelectPluginButton("libraryPlus");

        List<Galgame> games = Plugin.HostApi.GetAllGames();
        _source = new AdvancedCollectionView(games, true);
        GameGridView.ItemsSource = _source;

        // 恢复持久化的页面状态（跨页面重建 / 应用重启保持）
        RestoreRangeState();
        RestoreCategoryState();
        RestoreFormState();
        RestoreSortMenuState();
        ApplySort();
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
        PrimaryDescendingItem.IsChecked = Plugin.Data.PrimaryDescending;

        string secondary = Plugin.Data.SecondarySortKey;
        SecondaryDefault.IsChecked = secondary == KeyDefault;
        SecondaryExpected.IsChecked = secondary == "ExpectedPlayTime";
        SecondaryPlayTime.IsChecked = secondary == "PlayTime";
        SecondaryPlayCount.IsChecked = secondary == "PlayCount";
        SecondaryMyRate.IsChecked = secondary == "MyRate";
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

    /// <summary>统一刷新过滤：状态筛选（包含式）+ 时长区间，两个条件为 AND 关系</summary>
    private void RefreshFilter()
    {
        _source.Filter = FilterGame;
        _source.Refresh();
        UpdateCountText();
        UpdateStatsText();
    }

    private bool FilterGame(object? obj)
    {
        if (obj is not Galgame game) return false;
        if (!MatchesPlayTypeFilter(game)) return false;
        if (!MatchesCategory(game)) return false;
        if (!MatchesForm(game)) return false;
        return MatchesRange(game);
    }

    /// <summary>内容轴分类匹配（萌作/剧情作/拔作/其他），All=全部；与形态轴、时长区间、状态筛选 AND 联动</summary>
    private bool MatchesCategory(Galgame game)
    {
        string key = Plugin.Data.CategoryKey;
        if (key == CategoryKeyAll) return true;
        return GalgameClassifier.ClassifyContent(game).ToString() == key;
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

    /// <summary>更新左上角统计：显示当前可见（排除+区间生效后）数量</summary>
    private void UpdateCountText()
    {
        CountTextBlock.Text = $"共 {_source.Count} 款游戏";
    }

    /// <summary>
    /// 更新统计条（跟随当前可见列表）：待玩总时长 / 完成度（基于全库） / 时长未知数。
    /// 完成度基于全库——排除已玩过后可见列表已玩数为 0，用全库才有意义。
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

            GalgameCategory cat = GalgameClassifier.ClassifyContent(g);
            categoryCounts[cat] = categoryCounts.GetValueOrDefault(cat) + 1;
            GalgameForm form = GalgameClassifier.ClassifyForm(g);
            formCounts[form] = formCounts.GetValueOrDefault(form) + 1;
        }
        TotalTimeText.Text = $"待玩总时长：{ExpectedPlayTimeHelper.FormatHours(totalMinutes)}";

        int played = _source.Source.OfType<Galgame>().Count(g => g.PlayType == PlayType.Played);
        ProgressText.Text = $"完成度：{played}/{_source.Source.Count}";

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
            // 必须先 Clear 再切 None：None 模式下 SelectedItems 是无效集合，Clear 会抛 E_UNEXPECTED（框架 bug）
            try
            {
                GameGridView.SelectedItems.Clear();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // 框架 bug：忽略
            }
            GameGridView.SelectionMode = ListViewSelectionMode.None;
        }
        GameGridView.IsMultiSelectCheckBoxEnabled = multi;
    }

    /// <summary>当前批量搜刮的目标游戏集：多选模式有选中 → 用选中集；否则退回当前筛选可见集</summary>
    private List<Galgame> GetBatchTargets()
    {
        if (GameGridView.SelectionMode == ListViewSelectionMode.Multiple &&
            GameGridView.SelectedItems.Count > 0)
            return GameGridView.SelectedItems.OfType<Galgame>().ToList();
        return _source.OfType<Galgame>().ToList();
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
        if (games.Count == 0)
        {
            Plugin.HostApi.Info(InfoBarSeverity.Informational, msg: "当前筛选下没有游戏");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Plugin.HostApi.GetMainWindow()?.Content.XamlRoot,
            Title = "批量搜刮（kungal）",
            Content = $"将对{(isSelected ? "选中的" : "当前筛选的")} {games.Count} 款游戏用 kungal 搜刮：\n" +
                      "· 简介：仅填充为空或非中文的（已有中文简介不覆盖）\n" +
                      "· 标签：与现有标签合并（不删除原有标签）\n" +
                      $"预计耗时约 {games.Count * 3 / 10 + 1} 秒（含网络请求节流）",
            PrimaryButtonText = "开始搜刮",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // 防重入：批量进行中禁用按钮，finally 恢复
        if (sender is AppBarButton scrapeButton) scrapeButton.IsEnabled = false;
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

        int ok = 0, noMatch = 0, fail = 0, locked = 0;
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

                    // 采集完整 kungal 数据到 PluginData（新建实例赋值，保证触发持久化）
                    var dict = new Dictionary<string, KungalGameData>(Plugin.Data.KungalData)
                    {
                        [game.Uuid.ToString()] = KungalPhraser.BuildKungalData(fetched.Value.Detail)
                    };
                    Plugin.Data.KungalData = dict;

                    await HostServices.SaveGameAsync(game);
                    ok++;
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
            try { GameGridView.IsEnabled = true; GameGridView.Opacity = 1.0; } catch { }
            try { OnHostPhrased(); } catch { } // 当前页面刷新（页面若已销毁则静默）
            HostServices.TriggerPhrased(); // 触发宿主事件：主页/详情/重建后的扩展库页统一刷新
        }

        Plugin.HostApi.Info(InfoBarSeverity.Informational,
            title: "批量搜刮完成",
            msg: $"成功 {ok} / 简介锁定未动 {locked} / 未匹配 {noMatch} / 失败 {fail}" +
                 (bgmClient.Token != null ? $" / Bangumi 标签 {bgmOk} 款" : ""),
            displayTimeMs: 10000); // 10 秒，可读完整汇总
        Plugin.HostApi.Log(InfoBarSeverity.Informational,
            $"批量搜刮完成: ok={ok} locked={locked} noMatch={noMatch} fail={fail} " +
            $"bgmToken={bgmClient.Token != null} bgmOk={bgmOk}");
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
        }
    }

    /// <summary>手动分类：设置/清除该游戏的分类覆盖（Auto=清除），持久化并即时刷新统计与筛选</summary>
    private void SetCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game || sender is not RadioMenuFlyoutItem item ||
            item.CommandParameter is not string key) return;

        var dict = Plugin.Data.UserCategory;
        if (key == "Auto") dict.Remove(game.Uuid.ToString());
        else dict[game.Uuid.ToString()] = key;
        Plugin.Data.UserCategory = dict; // 整体替换触发持久化
        RefreshFilter(); // 内部会刷新统计与列表
    }

    private void ChangePlayStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is not { } game || sender is not MenuFlyoutItem item) return;
        if (!Enum.TryParse((string)item.CommandParameter, out PlayType playType)) return;

        game.PlayType = playType;
        _ = HostServices.SaveGameAsync(game);
    }

    private void EditGame_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGame is null) return;
        Plugin.HostApi.NavigateTo(PageEnum.GalgameSettingPage, _currentGame);
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
