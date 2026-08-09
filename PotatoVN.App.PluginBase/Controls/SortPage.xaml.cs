using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Collections;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.NavigationApi;
using GalgameManager.WinApp.Base.Contracts.NavigationApi.NavigateParameters;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Controls;

/// <summary>
/// 独立的游戏排序页面：与游戏库原生页面功能一致（点击进详情、右键菜单、卡片布局），
/// 仅在排序方式上额外提供「按预计时长」。本页排序只作用于本页，不影响原生页面。
/// </summary>
public sealed partial class SortPage : Page
{
    private const string SortConditionDefault = "Default";
    private const string SortConditionExpected = "Expected";

    private readonly AdvancedCollectionView _source;
    private Galgame? _currentGame;
    private int _totalCount;

    public SortPage()
    {
        XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);

        // 页面重建（如从详情页返回）后，把侧边栏选中指示器移回「更多排序」
        SidebarSelectionHelper.SelectPluginButton("moreSortOptions");

        List<Galgame> games = Plugin.HostApi.GetAllGames();
        _totalCount = games.Count;
        _source = new AdvancedCollectionView(games, true);
        GameGridView.ItemsSource = _source;
        UpdateCountText();

        // 恢复「排除已玩过」状态（页面重建后保持，除非手动更改）
        HidePlayedToggle.IsChecked = Plugin.Data.HidePlayed;

        // 默认按预计时长升序
        ConditionExpected.IsChecked = true;
        DescendingItem.IsChecked = false;
        ApplySort();
    }

    #region 排序

    private void ApplySort()
    {
        _source.SortDescriptions.Clear();
        if (ConditionExpected.IsChecked == true)
        {
            // 不带属性名 + 自定义比较器，方向固定 Ascending，升/降序在比较器内部处理
            _source.SortDescriptions.Add(new SortDescription(SortDirection.Ascending,
                new ExpectedPlayTimeComparer(DescendingItem.IsChecked == true)));
        }
        _source.RefreshSorting();
    }

    private void SetCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item) return;
        ConditionDefault.IsChecked = item.Name == "ConditionDefault";
        ConditionExpected.IsChecked = item.Name == "ConditionExpected";
        ApplySort();
    }

    private void ToggleDescending_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item) DescendingItem.IsChecked = item.IsChecked;
        ApplySort();
    }

    /// <summary>「排除已玩过」切换：启用时从列表过滤掉游玩状态为已玩过的游戏（默认不启用）</summary>
    private void HidePlayedToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        bool hidePlayed = HidePlayedToggle.IsChecked == true;
        Plugin.Data.HidePlayed = hidePlayed; // 持久化，页面重建/应用重启后保持
        _source.Filter = hidePlayed ? FilterGame : null;
        _source.Refresh();
        UpdateCountText();
    }

    private bool FilterGame(object? obj) =>
        HidePlayedToggle.IsChecked != true || obj is not Galgame game || game.PlayType != PlayType.Played;

    /// <summary>更新左上角统计：显示库中全部数量；启用「排除已玩过」时扣减已玩过的数量</summary>
    private void UpdateCountText()
    {
        if (HidePlayedToggle.IsChecked == true)
        {
            int playedCount = _source.Source.OfType<Galgame>().Count(g => g.PlayType == PlayType.Played);
            CountTextBlock.Text = $"共 {_totalCount - playedCount} 款游戏";
        }
        else
        {
            CountTextBlock.Text = $"共 {_totalCount} 款游戏";
        }
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
            long? px = ParseMinutes((x as Galgame)?.ExpectedPlayTime?.Value);
            long? py = ParseMinutes((y as Galgame)?.ExpectedPlayTime?.Value);

            if (px is null && py is null) return 0;
            if (px is null) return 1; // 未知时长排在后面
            if (py is null) return -1;

            int cmp = px.Value.CompareTo(py.Value);
            return _descending ? -cmp : cmp;
        }
    }

    /// <summary>
    /// 解析预计时长字符串为分钟数，无法解析返回 null。
    /// 支持的格式：VNDB 搜刮的 "20h" / "45m" / "1h30m"，以及 "very short" 等类别（映射为估算分钟数）。
    /// </summary>
    private static long? ParseMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Galgame.DefaultString) return null;

        switch (value.Trim().ToLowerInvariant())
        {
            case "very short": return 60;
            case "short": return 5 * 60;
            case "medium": return 15 * 60;
            case "long": return 30 * 60;
            case "very long": return 50 * 60;
        }

        bool any = false;
        long minutes = 0;
        Match m = Regex.Match(value, @"(\d+(?:\.\d+)?)\s*h", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            minutes += (long)(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60);
            any = true;
        }
        m = Regex.Match(value, @"(\d+(?:\.\d+)?)\s*m", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            minutes += (long)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            any = true;
        }
        return any ? minutes : null;
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
        _totalCount--;
        _source.Refresh();
        UpdateCountText();
    }

    #endregion
}
