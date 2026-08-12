using System;
using System.Collections.Generic;
using System.Linq;
using GalgameManager.Enums;
using GalgameManager.Models;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>内容轴分类：萌作 / 剧情作 / 拔作 / 其他（v2 双轴体系，2026-08-12）</summary>
public enum GalgameCategory
{
    Moe,        // 萌作
    Story,      // 剧情作
    Nukige,     // 拔作
    Other,      // 其他
}

/// <summary>形态轴分类：传统ADV / 非传统ADV（SLG/RPG 等玩法形态，正面证据判定）</summary>
public enum GalgameForm
{
    TraditionalAdv,     // 传统ADV
    NonTraditionalAdv,  // 非传统ADV（SLG/RPG/模拟…）
}

/// <summary>
/// 游戏分类器（双轴）：
/// 内容轴（萌/剧情/拔/其他）三层信号：① kungal 用户类型投票（ratings.galgame_type 聚合，真投票）
/// ② kungal content-tag 热度加权（galgame_count 为标签可信度先验）③ 旧关键词规则 v4.7 fallback。
/// 形态轴（传统ADV/非传统ADV）：kungal 类型词 tag + 非 ADV 引擎正面证据。
/// 数据源：Plugin.Data.KungalData（批量搜刮时采集）；无 kungal 数据的游戏自动走 fallback。
/// </summary>
public static class GalgameClassifier
{
    // ==================== 内容轴 ====================

    /// <summary>投票独占阈值：最高拆票 ≥10 时投票独占（社区共识足够强）；少票时与热度②融合</summary>
    private const double VoteExclusiveThreshold = 10;

    /// <summary>投票分换算热度单位（1 拆票 ≈ 500 热度），少票融合时使用</summary>
    private const double VoteToHeatFactor = 3000;

    /// <summary>内容轴分类（不触发网络，仅遍历本地数据）</summary>
    public static GalgameCategory ClassifyContent(Galgame game)
    {
        // ① 手动覆盖（用户显式设定，最高优先级——自动分类有边界，个人认知靠手动兜底）
        if (Plugin.Data.UserCategory.GetValueOrDefault(game.Uuid.ToString()) is { } manual &&
            manual != "" && Enum.TryParse<GalgameCategory>(manual, out GalgameCategory manualCat))
            return manualCat;

        KungalGameData? kungal = Plugin.Data.KungalData.GetValueOrDefault(game.Uuid.ToString());
        List<BgmTagData>? bgm = Plugin.Data.BgmData.GetValueOrDefault(game.Uuid.ToString());

        // ② 「拔作」tag 否决：kungal 社区明确标注的拔作定性（content/sexual 类 tag 名含「拔作」）。
        //    判别力实测：真拔作 4/5 有该 tag，剧情作/废萌 7/7 无（含 R18 剧情作）。
        //    R18 行为词（体位/手交/口交/乳交…）不参与拔作判定——R18 游戏普遍有 H 场景 tag，
        //    实测废萌/剧情作（永不枯萎/野良与皇女/兰斯）sexual tag 同样密集，行为词判别力差。
        if (kungal is { Tags.Count: > 0 } &&
            kungal.Tags.Any(t => t.Category is "content" or "sexual" && IsNukigeTag(t.Name)))
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                $"分类[拔作]: {game.Name.Value} 路径=kungal拔作tag");
            return GalgameCategory.Nukige;
        }

        // ③ kungal 用户类型投票（拆票值；galgame_type 是多选属性标签，勾 plot 表示"有剧情属性"≠"剧情作"）
        if (kungal is { TypeVotes.Count: > 0 })
        {
            var votes = kungal.TypeVotes
                .Select(kv => (Category: MapTypeVote(kv.Key), Votes: kv.Value))
                .Where(x => x.Category != null)
                .GroupBy(x => x.Category!.Value)
                .Select(g => (Category: g.Key, Votes: g.Sum(x => x.Votes)))
                .ToList();
            if (votes.Count > 0)
            {
                var top = votes.OrderByDescending(x => x.Votes).First();
                // 投票独占：最高拆票 ≥10（社区共识足够强，如素晴日 18 / 永不枯萎 14.7）
                if (top.Votes >= VoteExclusiveThreshold) return top.Category;
                // 少票：投票与热度②/Bangumi 信号融合（投票是人的直接表态，权重须高于 tag 热度）
                var combined = MergeVotesWithHeat(votes, CalcHeatScores(kungal), CalcBgmScores(bgm));
                if (combined.Count > 0)
                {
                    var winner = combined.OrderByDescending(kv => kv.Value).First();
                    if (winner.Key == GalgameCategory.Nukige)
                        Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                            $"分类[拔作]: {game.Name.Value} 路径=投票融合 {string.Join(",", combined.Select(kv => $"{kv.Key}={kv.Value:F0}"))}");
                    return winner.Key;
                }
            }
        }

        // ④ kungal tag 热度加权（content+sexual 类，只算萌/剧词——拔作判定已由②承担）+ Bangumi 词表分
        if (kungal is { Tags.Count: > 0 })
        {
            var scores = CalcHeatScores(kungal);
            var final = scores.ToDictionary(kv => kv.Key, kv => (double)kv.Value);
            AddBgmScores(final, bgm);
            if (final.Count > 0)
                return final.OrderByDescending(kv => kv.Value).First().Key;
        }

        // ⑤ 旧关键词规则 fallback（同人作归「其他」——同人 ADV 无内容证据，形态轴另行判定）
        var legacy = ClassifyLegacy(game);
        if (legacy == GalgameCategoryLegacy.Nukige)
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                $"分类[拔作]: {game.Name.Value} 路径=fallback旧规则 tags={string.Join(",", (game.Tags?.Value ?? []).Take(15))}");
        return legacy switch
        {
            GalgameCategoryLegacy.Doujin => GalgameCategory.Other,
            GalgameCategoryLegacy.Nukige => GalgameCategory.Nukige,
            GalgameCategoryLegacy.Story => GalgameCategory.Story,
            GalgameCategoryLegacy.Moe => GalgameCategory.Moe,
            _ => GalgameCategory.Other,
        };
    }

    /// <summary>热度②计分：content+sexual 类 tag 按萌/剧词表归属（拔作由「拔作」tag 否决承担，行为词退出判定）</summary>
    private static Dictionary<GalgameCategory, int> CalcHeatScores(KungalGameData kungal)
    {
        var scores = new Dictionary<GalgameCategory, int>();
        foreach (KungalTagData tag in kungal.Tags.Where(t => t.Category is "content" or "sexual"))
        {
            GalgameCategory? cat = MatchContentTag(tag.Name);
            if (cat != null)
                scores[cat.Value] = scores.GetValueOrDefault(cat.Value) + Math.Max(tag.GalgameCount, 1);
        }
        return scores;
    }

    /// <summary>Bangumi tag 词表分：tag 名按萌/剧词表归属，count（投票人数）加权</summary>
    private static Dictionary<GalgameCategory, double> CalcBgmScores(List<BgmTagData>? bgm)
    {
        var scores = new Dictionary<GalgameCategory, double>();
        if (bgm is null) return scores;
        foreach (BgmTagData tag in bgm)
        {
            GalgameCategory? cat = MatchContentTag(tag.Name);
            if (cat != null)
                scores[cat.Value] = scores.GetValueOrDefault(cat.Value) + tag.Count;
        }
        return scores;
    }

    /// <summary>把 Bangumi 词表分并入总分（×BgmToHeatFactor，把投票人数对齐到 kungal 热度量级）</summary>
    private static void AddBgmScores(Dictionary<GalgameCategory, double> target, List<BgmTagData>? bgm)
    {
        foreach (var kv in CalcBgmScores(bgm))
            target[kv.Key] = target.GetValueOrDefault(kv.Key) + kv.Value * BgmToHeatFactor;
    }

    /// <summary>少票投票与热度②融合：总分 = 拆票分×500 + 热度分（类型取并集）</summary>
    /// <summary>Bangumi tag 投票换算热度单位（1 投票 ≈ 50 热度，Bangumi count 量级远小于 kungal galgame_count）</summary>
    private const double BgmToHeatFactor = 50;

    private static Dictionary<GalgameCategory, double> MergeVotesWithHeat(
        List<(GalgameCategory Category, double Votes)> votes, Dictionary<GalgameCategory, int> heat,
        Dictionary<GalgameCategory, double> bgmScores)
    {
        var combined = new Dictionary<GalgameCategory, double>();
        foreach (var v in votes)
            combined[v.Category] = v.Votes * VoteToHeatFactor;
        foreach (var h in heat)
            combined[h.Key] = combined.GetValueOrDefault(h.Key) + h.Value;
        foreach (var b in bgmScores)
            combined[b.Key] = combined.GetValueOrDefault(b.Key) + b.Value * BgmToHeatFactor;
        return combined;
    }

    /// <summary>kungal 用户类型投票 → 内容轴（moe=萌作/plot=剧情作/ba_saku=拔作/daily=日常系归萌作）</summary>
    private static GalgameCategory? MapTypeVote(string type) => type switch
    {
        "moe" => GalgameCategory.Moe,
        "plot" => GalgameCategory.Story,
        "ba_saku" => GalgameCategory.Nukige,
        "daily" => GalgameCategory.Moe,
        _ => null,
    };

    /// <summary>
    /// 「拔作」tag 精确匹配：只认 tag 名为「拔作」（社区定性标注）。
    /// 排除「拔作(笑)」等梗/衍生标签（NUKITASHI 的 tag 只是名字梗，不是拔作定性）。
    /// </summary>
    private static bool IsNukigeTag(string name)
    {
        string t = name.Trim();
        return t == "拔作" || t == "拔作向";
    }

    /// <summary>content/sexual tag 名 → 萌/剧分类（拔作由「拔作」tag 否决承担，行为词不参与热度判定）</summary>
    private static GalgameCategory? MatchContentTag(string tag)
    {
        string t = tag.Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        if (StoryKeywords.Any(kw => t.Contains(kw, StringComparison.Ordinal))) return GalgameCategory.Story;
        if (MoeKeywords.Any(kw => t.Contains(kw, StringComparison.Ordinal))) return GalgameCategory.Moe;
        return null;
    }

    // ==================== 形态轴 ====================

    /// <summary>形态轴分类：类型词 tag / 非 ADV 引擎命中 → 非传统ADV；否则传统ADV</summary>
    public static GalgameForm ClassifyForm(Galgame game)
    {
        KungalGameData? kungal = Plugin.Data.KungalData.GetValueOrDefault(game.Uuid.ToString());
        if (kungal is { Tags.Count: > 0 })
        {
            foreach (KungalTagData tag in kungal.Tags.Where(t => t.Category == "content"))
            {
                string t = tag.Name.Trim().ToLowerInvariant();
                if (FormTypeKeywords.Any(kw => t.Contains(kw, StringComparison.Ordinal)))
                    return GalgameForm.NonTraditionalAdv;
            }
        }
        if (kungal is { Engine.Count: > 0 })
        {
            foreach (string engine in kungal.Engine)
            {
                string e = engine.Trim().ToLowerInvariant();
                if (FormEngineKeywords.Any(kw => e.Contains(kw, StringComparison.Ordinal)))
                    return GalgameForm.NonTraditionalAdv;
            }
        }
        return GalgameForm.TraditionalAdv;
    }

    // ==================== 显示名 ====================

    public static string GetDisplayName(GalgameCategory category) => category switch
    {
        GalgameCategory.Moe => "萌作",
        GalgameCategory.Story => "剧情作",
        GalgameCategory.Nukige => "拔作",
        _ => "其他",
    };

    public static string GetFormDisplayName(GalgameForm form) => form switch
    {
        GalgameForm.NonTraditionalAdv => "非传统ADV",
        _ => "传统ADV",
    };

    // ==================== 词表（kungal content-tag 中文词） ====================

    /// <summary>
    /// 萌作词（包含匹配）。注意：
    /// - 不含单字「萌」（会命中「萌拔」等拔作词）；用「废萌/萌系/萌作」等复合词
    /// - 不含「少女」（包含命中「幼女」，幼女题材偏拔作）；不含「妹」（妹妹系萌拔两可）
    /// - **不含题材词**（幼驯染/学校/学园/校园/傲娇/青梅竹马）——这些在剧情作同样常见
    ///   （交响乐之雨/白色相簿都是幼驯染+催泪剧情），题材词会误判萌作（2026-08-12 实测）
    /// </summary>
    private static readonly string[] MoeKeywords =
    {
        "废萌", "萌系", "萌作", "纯爱", "治愈", "甜", "日常", "恋爱", "温馨", "轻松",
        "喜剧", "甜蜜", "ほのぼの", "纯情",
    };

    /// <summary>剧情作词（包含匹配）</summary>
    private static readonly string[] StoryKeywords =
    {
        "剧情", "悬疑", "推理", "催泪", "泣", "电波", "哲学", "狂气", "心理", "惊悚",
        "恐怖", "生死", "虐", "致郁", "悲剧", "loop", "metafiction", "世界观", "宏大",
        "反转", "伏笔", "史诗", "文学", "轮回", "解密", "智斗", "民俗", "克苏鲁", "意识流",
    };

    /// <summary>形态类型词（content tag，包含匹配）：命中 → 非传统ADV</summary>
    private static readonly string[] FormTypeKeywords =
    {
        "slg", "rpg", "模拟", "策略", "act", "动作", "射击", "弹幕", "stg", "音游",
        "益智", "解谜", "拼图", "tcg", "卡牌", "经营", "养成", "塔防", "战棋", "srpg",
        "arpg", "竞速", "体育", "格斗", "沙盒", "开放世界", "roguelike", "rouge", "地牢", "迷宫",
    };

    /// <summary>非 ADV 引擎词（包含匹配）：只放明确非视觉小说形态的引擎（Ren'Py 是 ADV 引擎不放）</summary>
    private static readonly string[] FormEngineKeywords =
    {
        "rpg maker", "rpgツクール", "srpg studio", "wolf rpg", "gamemaker",
    };

    // ==================== 旧规则 fallback（v4.7 关键词规则原样保留） ====================

    private enum GalgameCategoryLegacy
    {
        Moe, Story, Nukige, Doujin, Other,
    }

    /// <summary>旧关键词规则（v4.7 冻结）：无 kungal 数据时的 fallback，Doujin 由调用方归「其他」</summary>
    private static GalgameCategoryLegacy ClassifyLegacy(Galgame game)
    {
        if (!HasVndbEntry(game)) return GalgameCategoryLegacy.Doujin;
        List<string> tags = (game.Tags?.Value ?? []).Select(t => t.Trim().ToLowerInvariant()).ToList();
        if (HasStrongNukigeSignal(tags)) return GalgameCategoryLegacy.Nukige;
        if (tags.Any(IsStoryTag)) return GalgameCategoryLegacy.Story;
        if (CountExplicitNukigeTags(tags) >= 2) return GalgameCategoryLegacy.Nukige;
        if (tags.Any(IsMoeTag)) return GalgameCategoryLegacy.Moe;
        if (HasWeakNukigeSignal(tags)) return GalgameCategoryLegacy.Nukige;
        return GalgameCategoryLegacy.Other;
    }

    private static int CountExplicitNukigeTags(List<string> tags)
        => tags.Count(t => ExplicitNukigeTagKeywords.Contains(t));

    private static bool HasVndbEntry(Galgame game)
    {
        string?[]? ids = game.Ids;
        int vndbIndex = (int)RssType.Vndb;
        return ids is not null && ids.Length > vndbIndex && !string.IsNullOrWhiteSpace(ids[vndbIndex]);
    }

    private static bool HasStrongNukigeSignal(List<string> tags)
    {
        if (tags.Any(t => t is "nukige" or "porn with plot" or "sex with plot" or "pornographic" or "explicit sex"))
            return true;
        if (ContainsAnyAny(tags, HardNukigeKeywords)) return true;
        if (ContainsAnyAny(tags, LoliAttributeKeywords) && ContainsAnyAny(tags, ExplicitNukigeTagKeywords))
            return true;
        if (ContainsAnyAny(tags, MatureThemeKeywords) && ContainsAnyAny(tags, ExplicitNukigeTagKeywords))
            return true;
        return false;
    }

    private static bool HasWeakNukigeSignal(List<string> tags)
    {
        if (tags.Any(t => t is "拔作" or "拔作向" or "实用" or "实用作" or "实用向" or "同人拔" or "萌拔" or "eroge"))
            return true;
        return ContainsAnyAny(tags, WeakNukigeKeywords);
    }

    private static bool IsStoryTag(string tag)
    {
        if (tag is "high degree of plot" or "plot twist" or "nakige" or "tearjerker" or "mind screw"
            or "剧情" or "剧情作" or "泣系" or "催泪" or "悬疑" or "推理" or "郁系" or "虐心") return true;
        return ContainsAny(tag, StoryKeywordsLegacy);
    }

    private static bool IsMoeTag(string tag)
    {
        if (tag is "cute story" or "daily life" or "slice of life" or "school life" or "moe"
            or "萌" or "治愈" or "日常" or "纯爱" or "废萌" or "温馨" or "轻松" or "甜作") return true;
        return ContainsAny(tag, MoeKeywordsLegacy);
    }

    private static bool ContainsAny(string text, string[] keywords)
        => keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));

    private static bool ContainsAnyAny(List<string> tags, string[] keywords)
        => tags.Any(tag => ContainsAny(tag, keywords));

    private static readonly string[] HardNukigeKeywords =
    {
        "ntr", "凌辱", "调教", "触手", "轮奸", "强x", "肉便器", "孕ませ", "榨精", "援交",
        "mind break", "humiliation", "双飞", "撸出血", "抜きゲー", "拔きゲー",
    };

    private static readonly string[] LoliAttributeKeywords =
    {
        "萝莉", "幼女", "loli",
    };

    private static readonly string[] MatureThemeKeywords =
    {
        "人妻", "母系", "熟女", "母",
    };

    private static readonly string[] ExplicitNukigeTagKeywords =
    {
        "拔作", "拔作向", "实用", "实用作", "实用向", "同人拔", "萌拔", "eroge",
    };

    private static readonly string[] WeakNukigeKeywords =
    {
        "vanilla", "ahegao", "impregnation", "无修正", "后宫",
    };

    private static readonly string[] StoryKeywordsLegacy =
    {
        "mystery", "psychological", "thriller", "tragedy", "drama", "dark", "philosophical",
        "侦探", "反转", "剧本", "世界观", "loop", "metafiction",
    };

    private static readonly string[] MoeKeywordsLegacy =
    {
        "comedy", "romance", "cute", "healing", "fluff", "school", "charming",
        "搞笑", "恋爱", "甜", "萌系", "少女",
    };
}
