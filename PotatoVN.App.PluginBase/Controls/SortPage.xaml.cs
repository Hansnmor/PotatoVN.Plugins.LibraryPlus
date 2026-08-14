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

    /// <summary>多选勾选集（独立于 GridView.SelectedItems 维护——视图重建/筛选刷新会清空 SelectedItems，
    /// 勾选意图存这里跨筛选保留，刷新后从它恢复可见项的选中）</summary>
    private readonly HashSet<Guid> _batchSelection = new();

    /// <summary>恢复勾选进行中：屏蔽 SelectionChanged 同步（SelectedItems.Clear/Add 会触发事件，
    /// 若不同步屏蔽会把 _batchSelection 自己清掉——勾选丢失的根源）</summary>
    private bool _restoringSelection;

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

            GalgameCategory cat = GalgameClassifier.ClassifyContent(g);
            categoryCounts[cat] = categoryCounts.GetValueOrDefault(cat) + 1;
            GalgameForm form = GalgameClassifier.ClassifyForm(g);
            formCounts[form] = formCounts.GetValueOrDefault(form) + 1;
        }
        TotalTimeText.Text = $"待玩总时长：{ExpectedPlayTimeHelper.FormatHours(totalMinutes)}";

        // 完成度：左侧三个筛选全「全部」→ 全局；否则按三筛选条件（时长/内容/形态，不含状态筛选）的子集统计
        bool isGlobal = Plugin.Data.RangeKey == RangeKeyAll &&
                        Plugin.Data.CategoryKey == CategoryKeyAll &&
                        Plugin.Data.FormKey == CategoryKeyAll;
        IEnumerable<Galgame> progressSet = _source.Source.OfType<Galgame>();
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
            _batchSelection.Clear(); // 关闭多选清空勾选集
        }
        GameGridView.IsMultiSelectCheckBoxEnabled = multi;
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
            try { GameGridView.IsEnabled = true; GameGridView.Opacity = 1.0; } catch { }
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
