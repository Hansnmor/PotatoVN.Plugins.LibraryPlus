using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 新游戏入库后自动采集评分（2026-10-10）。
///
/// 动机：评分此前只有「打开详情页」和「手动批量计算」两个触发点——用户添加一款新游戏后，
/// 若不打开它的详情页，就永远不会去拉评分（排序键「按加权评分」也会一直显示未知）。
///
/// 为什么挂在「入库」而不是「搜刮完成」：宿主 <c>AddGameAsync</c> 的顺序是
/// 「解析游戏信息（写好 Bangumi/VNDB 外部 id）→ 落盘 → 发 Added 事件」，
/// 所以本回调触发时外部 id 已经可用，**此刻就能拉到分**，不必等到某次搜刮。
///
/// 触发源：<see cref="HostServices.SubscribeGalgameAddedCrossGen"/>（跨宿主代际，
/// 新宿主走 GalgameMutated 的 Added 位、旧宿主走 GalgameAddedEvent）。
/// 全程后台、不阻塞宿主添加流程；失败静默（打开详情页时还会再试一次）。
/// </summary>
internal static class RatingAutoFetcher
{
    /// <summary>整体闸门：一次只跑一个采集，避免批量添加时并发打爆上游。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>挂载（插件 Initialize 时调用一次）。</summary>
    public static void Initialize()
    {
        HostServices.SubscribeGalgameAddedCrossGen(OnGameAdded);
    }

    public static void Uninitialize()
    {
        HostServices.UnsubscribeGalgameAddedCrossGen();
    }

    private static void OnGameAdded(Galgame game)
    {
        _ = Task.Run(() => FetchIfNeededAsync(game));
    }

    /// <summary>
    /// 该游戏没有**仍然新鲜**的评分缓存时拉一次。
    /// 已有新鲜缓存（如从备份恢复、或刚批量算过）则完全不动，避免无谓请求。
    /// </summary>
    private static async Task FetchIfNeededAsync(Galgame game)
    {
        if (game is null) return;
        if (RatingCachePolicy.HasFresh(game)) return; // 已有新鲜缓存 → 什么都不做

        bool acquired = false;
        try
        {
            acquired = Gate.WaitAsync(0).Result;
            if (!acquired) return; // 已有采集在跑，跳过（该游戏下次进详情页仍会拉）

            // force: true —— 能走到这里说明没有新鲜缓存；需要绕过「见缓存即返回」的短路，
            // 否则残留的过期条目会被原样返回，等于什么都没做
            RatingData? rating = await Plugin.FetchRatingAsync(game, force: true);
            if (rating is not null)
                Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                    $"新游戏评分采集: {game.Name.Value} " +
                    $"bgm={(rating.BgmScore > 0 ? rating.BgmScore.ToString("F1") : "无")} " +
                    $"vndb={(rating.VndbScore > 0 ? rating.VndbScore.ToString("F1") : "无")}");
        }
        catch
        {
            // 自动采集是尽力而为：失败静默（详情页打开时会再试）
        }
        finally
        {
            if (acquired) Gate.Release();
        }
    }
}
