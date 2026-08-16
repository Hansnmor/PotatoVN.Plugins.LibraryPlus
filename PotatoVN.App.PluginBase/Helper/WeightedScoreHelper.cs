using System;
using GalgameManager.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 综合加权评分（定稿规则 2026-08-16）：
/// 双源：final = (bangumi × √n_b + vndb × √n_v) / (√n_b + √n_v)（不校准、不收缩，恒落两站原始分之间）
/// 单源：直接返回该站原始分；无评分返回 null。
/// 数据源：<see cref="Plugin.Data.KungalData"/>（批量搜刮/详情页首次打开时从 kungal external_ratings 采集）。
/// </summary>
internal static class WeightedScoreHelper
{
    /// <summary>综合分（2 位小数）。两站都无有效评分返回 null。</summary>
    public static double? Compute(double? bangumi, int bangumiCount, double? vndb, int vndbCount)
    {
        bool hasBgm = bangumi is > 0;
        bool hasVndb = vndb is > 0;
        if (!hasBgm && !hasVndb) return null;
        if (!hasBgm) return Math.Round(vndb!.Value, 2);
        if (!hasVndb) return Math.Round(bangumi!.Value, 2);

        double wb = Math.Sqrt(bangumiCount > 0 ? bangumiCount : 1);
        double wv = Math.Sqrt(vndbCount > 0 ? vndbCount : 1);
        double score = (bangumi!.Value * wb + vndb!.Value * wv) / (wb + wv);
        return Math.Round(score, 2);
    }

    /// <summary>从统一评分缓存取某游戏综合分（无数据返回 null）。</summary>
    public static double? GetScore(Galgame game)
    {
        if (!Plugin.Data.RatingCache.TryGetValue(game.Uuid.ToString(), out var rating)) return null;
        return Compute(rating.BgmScore > 0 ? rating.BgmScore : null, rating.BgmCount,
            rating.VndbScore > 0 ? rating.VndbScore : null, rating.VndbCount);
    }
}
