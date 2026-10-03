using System;
using System.Collections.Generic;
using GalgameManager.Models;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 评分缓存时效策略（2026-10-03 新增）。
/// 动机：<see cref="RatingData.FetchedAt"/> 此前只写不读，于是「当时没评分」的条目会被永久缓存，
/// 界面也没有清理入口——上游后来补了评分，用户依然只看到「暂无评分数据」。
/// 现在的语义：过期的条目**展示时照旧先用旧值**（不闪、不阻塞页面），同时后台重拉一次自愈。
/// </summary>
internal static class RatingCachePolicy
{
    /// <summary>有评分的缓存有效期：上游评分变化缓慢，30 天足够，也不会频繁打扰上游。</summary>
    public static readonly TimeSpan FreshForRated = TimeSpan.FromDays(30);

    /// <summary>「无评分」缓存有效期：短得多——上游随时可能补上评分（新条目尤其常见）。</summary>
    public static readonly TimeSpan FreshForEmpty = TimeSpan.FromDays(7);

    /// <summary>缓存是否仍新鲜。没有时间戳（<see cref="DateTime"/> 默认值）一律视为过期，让它重拉一次。</summary>
    public static bool IsFresh(RatingData rating, DateTime now)
    {
        if (rating.FetchedAt == default) return false;
        TimeSpan ttl = rating.BgmScore > 0 || rating.VndbScore > 0 ? FreshForRated : FreshForEmpty;
        return now - rating.FetchedAt < ttl;
    }

    public static bool IsFresh(RatingData rating) => IsFresh(rating, DateTime.Now);

    /// <summary>
    /// 该游戏是否已有**仍然新鲜**的评分缓存。批量增量的判定以此为准（而不是「有没有缓存过」），
    /// 否则「无评分」的负缓存永远拦着重新拉取。
    /// </summary>
    public static bool HasFresh(Galgame game) =>
        Plugin.Data.RatingCache.TryGetValue(game.Uuid.ToString(), out RatingData? rating) &&
        rating is not null &&
        IsFresh(rating);

    /// <summary>清空评分缓存；返回被清掉的条目数。读缓存的地方会在下次进入/计算时重新拉取。</summary>
    public static int Clear()
    {
        int count = Plugin.Data.RatingCache.Count;
        Plugin.Data.RatingCache = new Dictionary<string, RatingData>(); // 整体替换赋值才会触发持久化
        return count;
    }
}
