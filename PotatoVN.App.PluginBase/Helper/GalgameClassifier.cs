using System;
using System.Collections.Generic;
using System.Linq;
using GalgameManager.Enums;
using GalgameManager.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 游戏内容分类：萌作 / 剧情作 / 拔作 / 同人作 / 其他。
///
/// 判定规则（用户确认 2026-08-10）：
/// 1. 有 VNDB 条目的游戏（正式/商业发行，普遍 VNDB+Bangumi 双页面都有）→ 按内容标签分类：
///    拔作 &gt; 剧情作 &gt; 萌作 &gt; 其他。夜羊社（同人社团但作品有 VNDB ID）等按内容归拔作/萌作/剧情作。
/// 2. 没有 VNDB 条目的游戏（RPG Maker 同人/小黄油等，普遍只有 Bangumi 页面）→ 直接归「同人作」。
///
/// 数据源全部来自本地（搜刮时已下载）：Galgame.Tags（VNDB 中文翻译 + Bangumi 中文用户标签混合）、
/// Galgame.Ids 里的 VndbId。词表在下方静态数组中，可随时增删。
/// </summary>
public enum GalgameCategory
{
    Moe,        // 萌作
    Story,      // 剧情作
    Nukige,     // 拔作
    Doujin,     // 同人作
    Other,      // 其他
}

public static class GalgameClassifier
{
    /// <summary>分类一个游戏（不触发网络，仅遍历本地数据）</summary>
    public static GalgameCategory Classify(Galgame game)
    {
        // 无 VNDB 条目 = 同人/黄油（RPG Maker 同人普遍只有 Bangumi 页面）→ 同人作
        if (!HasVndbEntry(game)) return GalgameCategory.Doujin;

        // 有 VNDB 条目 → 按内容标签分类。
        // 拔作信号分强弱两级（用户确认 2026-08-10）：
        //   强信号（nukige/凌辱/触手/萝莉/幼女等硬核内容词）→ 直接归拔作，即使同时有萌/剧情标签
        //   弱信号（拔作/实用/萌拔等萌拔常见标签）→ 只有无强拔、无萌/剧情信号时才归拔作
        // 判定顺序：强拔作 > 剧情作 > 萌作 > 弱拔作 > 其他
        List<string> tags = (game.Tags?.Value ?? []).Select(t => t.Trim().ToLowerInvariant()).ToList();
        if (HasStrongNukigeSignal(tags)) return GalgameCategory.Nukige;
        if (tags.Any(IsStoryTag)) return GalgameCategory.Story;
        if (tags.Any(IsMoeTag)) return GalgameCategory.Moe;
        if (HasWeakNukigeSignal(tags)) return GalgameCategory.Nukige;
        return GalgameCategory.Other;
    }

    public static string GetDisplayName(GalgameCategory category) => category switch
    {
        GalgameCategory.Moe => "萌作",
        GalgameCategory.Story => "剧情作",
        GalgameCategory.Nukige => "拔作",
        GalgameCategory.Doujin => "同人作",
        _ => "其他",
    };

    /// <summary>是否有 VNDB 条目（Ids 数组 Vndb 槽位非空）</summary>
    private static bool HasVndbEntry(Galgame game)
    {
        string?[]? ids = game.Ids;
        int vndbIndex = (int)RssType.Vndb;
        return ids is not null && ids.Length > vndbIndex && !string.IsNullOrWhiteSpace(ids[vndbIndex]);
    }

    /// <summary>拔作强信号（彻底拔作）：VNDB 官方拔作标签或硬核 R18 内容词，即使同时有萌/剧情标签也归拔作</summary>
    private static bool HasStrongNukigeSignal(List<string> tags)
    {
        if (tags.Any(t => t is "nukige" or "porn with plot" or "sex with plot" or "pornographic" or "explicit sex"))
            return true;
        return ContainsAnyAny(tags, StrongNukigeKeywords);
    }

    /// <summary>拔作弱信号（萌拔常见标签）：仅当无强拔、无萌/剧情信号时才归拔作</summary>
    private static bool HasWeakNukigeSignal(List<string> tags)
    {
        if (tags.Any(t => t is "拔作" or "拔作向" or "实用" or "实用作" or "实用向" or "同人拔" or "萌拔" or "eroge"))
            return true;
        return ContainsAnyAny(tags, WeakNukigeKeywords);
    }

    /// <summary>剧情作信号：plot / 悬疑 / 泣系等</summary>
    private static bool IsStoryTag(string tag)
    {
        if (tag is "high degree of plot" or "plot twist" or "nakige" or "tearjerker" or "mind screw"
            or "剧情" or "剧情作" or "泣系" or "催泪" or "悬疑" or "推理" or "郁系" or "虐心") return true;
        return ContainsAny(tag, StoryKeywords);
    }

    /// <summary>萌作信号：moe / 日常 / 治愈等</summary>
    private static bool IsMoeTag(string tag)
    {
        if (tag is "cute story" or "daily life" or "slice of life" or "school life" or "moe"
            or "萌" or "治愈" or "日常" or "纯爱" or "废萌" or "温馨" or "轻松" or "甜作") return true;
        return ContainsAny(tag, MoeKeywords);
    }

    private static bool ContainsAny(string text, string[] keywords)
        => keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));

    private static bool ContainsAnyAny(List<string> tags, string[] keywords)
        => tags.Any(tag => ContainsAny(tag, keywords));

    /// <summary>拔作强信号词（硬核 R18 内容词 / 重口系）</summary>
    private static readonly string[] StrongNukigeKeywords =
    {
        "ntr", "凌辱", "调教", "触手", "轮奸", "强x", "肉便器", "孕ませ", "榨精", "援交",
        "mind break", "humiliation", "萝莉", "幼女", "双飞",
    };

    /// <summary>拔作弱信号词（萌拔/甜拔常见标签，非决定性）</summary>
    private static readonly string[] WeakNukigeKeywords =
    {
        "vanilla", "ahegao", "impregnation", "无修正", "后宫",
    };

    private static readonly string[] StoryKeywords =
    {
        "mystery", "psychological", "thriller", "tragedy", "drama", "dark", "philosophical",
        "侦探", "反转", "剧本", "世界观", "loop", "metafiction",
    };

    private static readonly string[] MoeKeywords =
    {
        "comedy", "romance", "cute", "healing", "fluff", "school", "charming",
        "搞笑", "恋爱", "甜", "萌系", "少女",
    };
}
