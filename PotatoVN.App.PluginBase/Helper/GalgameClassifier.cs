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

        // 有 VNDB 条目 → 按内容标签分类
        List<string> tags = (game.Tags?.Value ?? []).Select(t => t.Trim().ToLowerInvariant()).ToList();
        if (tags.Any(IsNukigeTag)) return GalgameCategory.Nukige;
        if (tags.Any(IsStoryTag)) return GalgameCategory.Story;
        if (tags.Any(IsMoeTag)) return GalgameCategory.Moe;
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

    /// <summary>拔作信号：nukige 及其派生标签（含中文）</summary>
    private static bool IsNukigeTag(string tag)
    {
        if (tag is "nukige" or "porn with plot" or "sex with plot" or "pornographic" or "explicit sex"
            or "拔作" or "拔作向" or "实用" or "实用作" or "同人拔" or "eroge") return true;
        return ContainsAny(tag, NukigeKeywords);
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
            or "萌" or "治愈" or "日常" or "纯爱" or "废萌" or "温馨" or "轻松") return true;
        return ContainsAny(tag, MoeKeywords);
    }

    private static bool ContainsAny(string text, string[] keywords)
        => keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));

    private static readonly string[] NukigeKeywords =
    {
        "ntr", "凌辱", "调教", "触手", "轮奸", "强x", "肉便器", "孕ませ", "榨精", "援交",
        "h-scene", "vanilla", "ahegao", "impregnation", "mind break", "humiliation",
        "萌拔", "实用向",
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
