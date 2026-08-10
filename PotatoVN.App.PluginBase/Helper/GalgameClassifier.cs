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

        // 有 VNDB 条目 → 按内容标签分类（用户确认 2026-08-10）：
        // 判定顺序：强拔作 > 显式拔作标签计数≥2 > 剧情作 > 萌作 > 弱拔作 > 其他。
        // 显式拔作标签（拔作/实用/萌拔/eroge…）命中 ≥2 个 = 社区一致认定拔作，压过单个萌/剧情标签
        //（如 变身！5 个拔作标签归拔；甜蜜女友2 仅 1 个「拔作」仍按萌信号归萌）。
        List<string> tags = (game.Tags?.Value ?? []).Select(t => t.Trim().ToLowerInvariant()).ToList();
        if (HasStrongNukigeSignal(tags)) return GalgameCategory.Nukige;
        if (CountExplicitNukigeTags(tags) >= 2) return GalgameCategory.Nukige;
        if (tags.Any(IsStoryTag)) return GalgameCategory.Story;
        if (tags.Any(IsMoeTag)) return GalgameCategory.Moe;
        if (HasWeakNukigeSignal(tags)) return GalgameCategory.Nukige;
        return GalgameCategory.Other;
    }

    /// <summary>统计命中的显式拔作标签个数（精确词：拔作/实用/萌拔/eroge 等）</summary>
    private static int CountExplicitNukigeTags(List<string> tags)
        => tags.Count(t => ExplicitNukigeTagKeywords.Contains(t));

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

    /// <summary>
    /// 拔作强信号（命中即归拔作，即使同时有萌/剧情标签）：
    /// 1) 硬核 R18 行为词（nukige/凌辱/触手/调教/轮奸/双飞 等）直接命中；
    /// 2) 萝莉/幼女属性词 + 显式拔作标签（loli-nukige，如夜羊社）；
    /// 3) 成熟题材词（人妻/母系/熟女/母）+ 显式拔作标签（中信号，用户确认 2026-08-10）——
    ///    Mama×Holic 等母系拔作归拔作，而甜蜜女友2（校园/妹，无成熟题材词）不受影响。
    /// </summary>
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

    /// <summary>拔作强信号词（硬核 R18 行为词 / 重口系）。角色属性词（萝莉/幼女）不在此列，
    /// 见 <see cref="LoliAttributeKeywords"/> 与强信号第二条的组合判定。</summary>
    private static readonly string[] HardNukigeKeywords =
    {
        "ntr", "凌辱", "调教", "触手", "轮奸", "强x", "肉便器", "孕ませ", "榨精", "援交",
        "mind break", "humiliation", "双飞",
    };

    /// <summary>萝莉/幼女等角色属性词：仅当与显式拔作标签同时出现时才构成拔作强信号（loli-nukige）</summary>
    private static readonly string[] LoliAttributeKeywords =
    {
        "萝莉", "幼女", "loli",
    };

    /// <summary>成熟题材词（中信号）：人妻/母系/熟女等题材在 galgame 中压倒性偏拔作向，
    /// 与显式拔作标签组合时归拔作（如 Mama×Holic 母系拔作）；甜蜜女友2 等校园/妹题材不受影响</summary>
    private static readonly string[] MatureThemeKeywords =
    {
        "人妻", "母系", "熟女", "母",
    };

    /// <summary>显式拔作标签：与萝莉/幼女组合可构成强信号；单独出现是弱信号（萌拔常见）</summary>
    private static readonly string[] ExplicitNukigeTagKeywords =
    {
        "拔作", "拔作向", "实用", "实用作", "实用向", "同人拔", "萌拔", "eroge",
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
