using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Helper.Bangumi;
using PotatoVN.App.PluginBase.Helper.Vndb;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase;

/// <summary>
/// 详情页右侧面板：综合加权评分卡片（IGalgamePageRightPanel 官方注入位）。
/// 宿主每次导航到详情页自动调用 <see cref="CreateRightPanelUiAsync"/>；页面重建自动重新注入，无状态残留。
///
/// 数据流（独立于 kungal，两站官方 API 直连 + 双重缓存）：
/// 1. 缓存命中（Plugin.Data.RatingCache，内存 + 落盘）→ 立即显示，零网络
/// 2. 未命中 → 占位 + 后台拉取：
///    · VNDB：官方 API 按 vndb id 直查（匿名可用，宿主 token 可增强）
///    · bangumi：宿主 token v0 byId 直查（R18 全量）→ 无 token/失败降级 v1 搜索（多关键词 + id 锚定）
///    · 写入缓存 → 填充卡片
/// 每个游戏只会真实拉取一次，之后任何场景零延迟。
/// </summary>
public partial class Plugin
{
    public Task<FrameworkElement> CreateRightPanelUiAsync(Galgame game)
    {
        var scoreText = new TextBlock
        {
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var detailText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(new TextBlock
        {
            Text = "综合评分",
            FontSize = 12,
            Opacity = 0.6,
        });
        panel.Children.Add(scoreText);
        panel.Children.Add(detailText);

        // 卡片化（背景 + 圆角边框）
        var card = new Border
        {
            Background = GetThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = GetThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = panel,
        };
        // 宿主注入位固定在最下方（游玩状态面板之后）——挂载后把整个注入区移到右列顶部，第一眼可见；
        // 宿主页面重建会重新注入并再次调整；结构找不到时静默保持原样
        card.Loaded += (_, _) => MoveInjectionAreaToTop(card);

        // 缓存命中（含重启后的落盘缓存）→ 立即显示，零网络
        if (TryFillFromCache(game, scoreText, detailText))
        {
            // 缓存命中但 bangumi 缺失（kungal 侧缺/旧缓存）→ 后台补拉增强，成功后刷新卡片为双源
            if (CacheMissingBgm(game))
                _ = FetchAndFillAsync(game, scoreText, detailText);
            return Task.FromResult<FrameworkElement>(card);
        }

        // 未命中 → 占位 + 后台拉取（不阻塞宿主页面加载）
        scoreText.Text = "…";
        detailText.Text = "正在获取评分数据…";
        _ = FetchAndFillAsync(game, scoreText, detailText);
        return Task.FromResult<FrameworkElement>(card);
    }

    /// <summary>缓存中该游戏的 bangumi 评分缺失且游戏有 bgm id（可补拉增强）。</summary>
    private static bool CacheMissingBgm(Galgame game)
    {
        if (!Plugin.Data.RatingCache.TryGetValue(game.Uuid.ToString(), out var rating)) return false;
        if (rating.BgmScore > 0) return false;
        return GetBgmId(game) > 0;
    }

    /// <summary>取主题画刷（缺失返回 null，Border 透明显示）。</summary>
    private static Brush? GetThemeBrush(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out object? value) ? value as Brush : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把右侧注入区（ItemsRepeater）移到右列 StackPanel 顶部（游玩状态面板上方）。
    /// 仅调整宿主公开布局容器内两个元素的顺序，不碰私有结构；失败静默（保持宿主默认位置）。
    /// </summary>
    private static void MoveInjectionAreaToTop(DependencyObject child)
    {
        try
        {
            DependencyObject? node = child;
            ItemsRepeater? repeater = null;
            while (node is not null)
            {
                node = VisualTreeHelper.GetParent(node);
                if (node is ItemsRepeater ir)
                {
                    repeater = ir;
                    break;
                }
            }
            if (repeater is null) return;
            if (VisualTreeHelper.GetParent(repeater) is not StackPanel stack) return;
            int index = stack.Children.IndexOf(repeater);
            if (index > 0)
                stack.Children.Move((uint)index, 0);
        }
        catch
        {
            // 结构异常保持宿主默认位置
        }
    }

    /// <summary>从统一评分缓存填充卡片；无数据返回 false。</summary>
    private static bool TryFillFromCache(Galgame game, TextBlock scoreText, TextBlock detailText)
    {
        if (!Plugin.Data.RatingCache.TryGetValue(game.Uuid.ToString(), out var rating)) return false;
        if (rating.BgmScore <= 0 && rating.VndbScore <= 0)
        {
            // 已缓存「无评分」（批量算过/拉取失败过）→ 直接显示暂无，不再重拉
            scoreText.Text = "—";
            detailText.Text = "暂无评分数据";
            return true;
        }
        FillText(rating, scoreText, detailText);
        return true;
    }

    /// <summary>
    /// 拉取并缓存某游戏评分（VNDB 官方 API + bangumi token→搜索），详情页与批量计算共用。
    /// 默认已有缓存直接返回（增量，不重拉）；<paramref name="force"/> 为 true 时强制重新拉取
    /// （用于用户补了 id 后重算）。无任何评分也写入缓存（防反复拉取）。
    /// </summary>
    public static async Task<RatingData?> FetchRatingAsync(Galgame game, bool force = false)
    {
        if (!force && Plugin.Data.RatingCache.TryGetValue(game.Uuid.ToString(), out var cached))
            return cached;

        var rating = new RatingData { FetchedAt = DateTime.Now };

        // VNDB：官方 API 按 id 直查（匿名可用）
        string? vndbId = GetVndbId(game);
        if (vndbId is not null)
        {
            var vndb = new VndbClient();
            if (await vndb.GetScoreAsync(vndbId) is { } v)
            {
                rating.VndbScore = v.Rating;
                rating.VndbCount = v.VoteCount;
            }
        }

        // bangumi：token v0 byId 直查 → 无 token/失败降级 v1 搜索
        int bgmId = GetBgmId(game);
        if (bgmId > 0)
            await FetchBgmScoreAsync(game, bgmId, rating);

        // 整体替换赋值触发 PluginData.PropertyChanged → 自动持久化（内存 + 落盘缓存）
        var cache = new Dictionary<string, RatingData>(Plugin.Data.RatingCache)
        {
            [game.Uuid.ToString()] = rating,
        };
        Plugin.Data.RatingCache = cache;
        return rating;
    }

    /// <summary>后台拉取两站评分并填充卡片（每个游戏首次进详情页时执行一次）。</summary>
    private static async Task FetchAndFillAsync(Galgame game, TextBlock scoreText, TextBlock detailText)
    {
        try
        {
            RatingData? rating = await FetchRatingAsync(game);
            Plugin.HostApi.InvokeOnMainThread(() =>
            {
                if (rating is null || (rating.BgmScore <= 0 && rating.VndbScore <= 0))
                {
                    scoreText.Text = "—";
                    detailText.Text = "暂无评分数据";
                    return;
                }
                FillText(rating, scoreText, detailText);
            });
        }
        catch
        {
            // 拉取失败保持占位，不影响详情页
        }
    }

    /// <summary>bangumi 评分：① 宿主 token → v0 byId 直查（按 id 最准，R18 全量）；② 降级 v1 搜索（多关键词 + id 锚定）。</summary>
    private static async Task FetchBgmScoreAsync(Galgame game, int bgmId, RatingData rating)
    {
        var bgmClient = new BgmClient { RequestDelay = TimeSpan.FromMilliseconds(200) };
        (double Score, int Count)? hit = null;

        string? token = await HostServices.GetBgmTokenAsync();
        if (!string.IsNullOrEmpty(token))
            hit = await bgmClient.GetScoreByTokenAsync(bgmId, token);
        if (hit is null)
        {
            foreach (string keyword in GetSearchKeywords(game))
            {
                hit = await bgmClient.SearchScoreAsync(bgmId, keyword);
                if (hit is not null) break;
            }
        }

        if (hit is { } score)
        {
            rating.BgmScore = score.Score;
            rating.BgmCount = score.Count;
        }
    }

    private static void FillText(RatingData rating, TextBlock scoreText, TextBlock detailText)
    {
        double? score = WeightedScoreHelper.Compute(
            rating.BgmScore > 0 ? rating.BgmScore : null, rating.BgmCount,
            rating.VndbScore > 0 ? rating.VndbScore : null, rating.VndbCount);
        scoreText.Text = score?.ToString("F2") ?? "—";

        var parts = new List<string>();
        if (rating.BgmScore > 0) parts.Add($"bangumi {rating.BgmScore:F1} ({rating.BgmCount}人)");
        if (rating.VndbScore > 0) parts.Add($"vndb {rating.VndbScore:F1} ({rating.VndbCount}人)");
        string detail = string.Join(" · ", parts);

        bool hasBgm = rating.BgmScore > 0, hasVndb = rating.VndbScore > 0;
        detailText.Text = hasBgm && hasVndb
            ? detail
            : $"{detail}（仅{(hasBgm ? "bangumi" : "vndb")}）";
    }

    /// <summary>VNDB id：优先 kungal 数据（"v5940" 格式），其次 Galgame.Ids[0]（纯数字，归一化补 v）。</summary>
    private static string? GetVndbId(Galgame game)
    {
        if (Plugin.Data.KungalData.TryGetValue(game.Uuid.ToString(), out var kungal) &&
            !string.IsNullOrWhiteSpace(kungal.VndbId))
            return kungal.VndbId;
        string?[]? ids = game.Ids;
        if (ids is not null && ids.Length > 0 && !string.IsNullOrWhiteSpace(ids[0]))
            return ids[0];
        return null;
    }

    /// <summary>bangumi subject id（Galgame.Ids[1]）。</summary>
    private static int GetBgmId(Galgame game)
    {
        string?[]? ids = game.Ids;
        if (ids is null || ids.Length < 2 || string.IsNullOrWhiteSpace(ids[1])) return 0;
        return int.TryParse(ids[1], out int bgmId) ? bgmId : 0;
    }

    /// <summary>
    /// 搜索关键词列表：完整中文名 → 完整日文原名 → 两者的字母数字片段（英文/罗马字名，bangumi 常可命中），
    /// 去重保序。bangumi 条目名多为日文/英文，中文名常搜不到；带符号的完整名也可能分词失败，故拆片段兜底。
    /// </summary>
    private static List<string> GetSearchKeywords(Galgame game)
    {
        var keywords = new List<string>();
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!keywords.Contains(text)) keywords.Add(text);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, "[A-Za-z0-9]{3,}"))
            {
                if (!keywords.Contains(m.Value)) keywords.Add(m.Value);
            }
        }
        Add(game.Name?.Value);
        Add(game.OriginalName?.Value);
        return keywords;
    }
}
