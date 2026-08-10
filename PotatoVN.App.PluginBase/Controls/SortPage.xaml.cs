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
        RestoreSortMenuState();
        ApplySort();
        UpdateCountText();
        UpdateStatsText();
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

    /// <summary>「筛选」菜单打开时，同步预设单选与状态勾选态（仅在自定义模式下显示勾选）</summary>
    private void FilterMenu_Opening(object sender, object e)
    {
        string mode = Plugin.Data.FilterMode;
        FilterAll.IsChecked = mode == FilterModeAll;
        FilterToPlay.IsChecked = mode == FilterModeToPlay;
        FilterCustom.IsChecked = mode == FilterModeCustom;

        // 只有自定义模式才显示状态勾选；全部/待玩模式下勾选标记保持清除
        // （自定义选择数据保留，切回自定义可恢复）
        bool showTicks = mode == FilterModeCustom;
        List<string> included = Plugin.Data.IncludedPlayTypes;
        IncludeNone.IsChecked = showTicks && included.Contains("None");
        IncludePlaying.IsChecked = showTicks && included.Contains("Playing");
        IncludePlayed.IsChecked = showTicks && included.Contains("Played");
        IncludeShelved.IsChecked = showTicks && included.Contains("Shelved");
        IncludeAbandoned.IsChecked = showTicks && included.Contains("Abandoned");
        IncludeWantToPlay.IsChecked = showTicks && included.Contains("WantToPlay");
    }

    /// <summary>「筛选」菜单关闭时统一应用筛选（自定义模式允许一次勾选多个状态后再生效）</summary>
    private void FilterMenu_Closing(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase sender,
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBaseClosingEventArgs args) => RefreshFilter();

    /// <summary>预设点击：全部 / 待玩 / 自定义</summary>
    private void FilterPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.CommandParameter is not string mode) return;
        Plugin.Data.FilterMode = mode; // 持久化
        // 切到「全部/待玩」时立即清除下方勾选标记（数据保留，切回自定义可恢复）
        if (mode != FilterModeCustom)
        {
            IncludeNone.IsChecked = false;
            IncludePlaying.IsChecked = false;
            IncludePlayed.IsChecked = false;
            IncludeShelved.IsChecked = false;
            IncludeAbandoned.IsChecked = false;
            IncludeWantToPlay.IsChecked = false;
        }
        RefreshFilter();
    }

    /// <summary>自定义模式：勾选=只显示该状态；勾选时自动切到「自定义」模式。
    /// 不立即刷新——等菜单关闭时统一应用，支持一次勾选多个状态。</summary>
    private void FilterState_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item || item.CommandParameter is not string playTypeName) return;

        List<string> included = Plugin.Data.IncludedPlayTypes;
        if (item.IsChecked)
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
        // 不在此刷新：等菜单关闭时统一应用（见 FilterMenu_Closing）
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
        return MatchesRange(game);
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
    /// </summary>
    private void UpdateStatsText()
    {
        long totalMinutes = 0;
        int unknown = 0;
        foreach (Galgame g in _source.OfType<Galgame>())
        {
            long? minutes = ExpectedPlayTimeHelper.ParseMinutes(g.ExpectedPlayTime?.Value);
            if (minutes is null) unknown++;
            else totalMinutes += minutes.Value;
        }
        TotalTimeText.Text = $"待玩总时长：{ExpectedPlayTimeHelper.FormatHours(totalMinutes)}";

        int played = _source.Source.OfType<Galgame>().Count(g => g.PlayType == PlayType.Played);
        ProgressText.Text = $"完成度：{played}/{_source.Source.Count}";

        UnknownTimeText.Text = $"{unknown} 款时长未知";
    }

    #endregion

    #region 游戏交互（与原生游戏库页一致）

    private void GameGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
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
        }
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
            Plugin.HostApi.Info(InfoBarSeverity.Error, msg: $"下载游戏信息失败：{ex.Message}");
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
