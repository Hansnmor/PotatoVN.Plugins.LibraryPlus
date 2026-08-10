using System;
using System.Collections.Generic;
using System.Linq;
using GalgameManager.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 游戏内容分类：萌作 / 剧情作 / 拔作 / 同人作 / 其他。
/// 数据源全部来自本地（游戏搜刮时已随 Galgame 下载）：Galgame.Tags（VNDB 中文翻译标签
/// 与 Bangumi 中文用户标签的混合，也可能含英文原样）与 Galgame.Engine。
/// 优先级：同人作（引擎/同人信号） &gt; 拔作 &gt; 剧情作 &gt; 萌作 &gt; 其他——
/// RPG Maker、Wolf RPG、Tyrano 等引擎工具（或同人标签）优先归同人作；无任何信号归「其他」（诚实兜底）。
/// </summary>
public enum GalgameCategory
{
    Moe,        // 萌作
    Story,      // 剧情作
    Nukige,     // 拔作
    Doujin,     // 同人·引擎作
    Other,      // 其他
}

public static class GalgameClassifier
{
    /// <summary>分类一个游戏（不触发网络，仅遍历本地标签与引擎）</summary>
    public static GalgameCategory Classify(Galgame game)
    {
        List<string> tags = (game.Tags?.Value ?? []).Select(t => t.Trim().ToLowerInvariant()).ToList();
        string engine = (game.Engine?.Value ?? string.Empty).Trim().ToLowerInvariant();

        // 引擎/同人信号最优先：RPG Maker、Wolf RPG、Tyrano 等引擎工具（或标签含同人/rpg）
        // 基本就是同人小品/小黄油，优先归同人作（用户确认 2026-08-10）
        if (tags.Any(IsDoujinTag) || IsEngineTag(engine)) return GalgameCategory.Doujin;

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

    /// <summary>同人/引擎作信号：同人标签或常见引擎名</summary>
    private static bool IsDoujinTag(string tag)
    {
        if (tag is "同人" or "同人游戏" or "同人作" or "rpg maker" or "rpg制作大师" or "rpg制作工具") return true;
        return ContainsAny(tag, DoujinKeywords);
    }

    private static bool IsEngineTag(string engine)
    {
        if (engine.Length == 0) return false;
        return engine is "rpg maker" or "rpg maker mv" or "rpg maker vx" or "rpg maker vx ace" or "rpg maker xp"
            or "wolf rpg" or "wolf rpg editor" or "ティラノ" or "tyranoscript" or "tyrano"
            or "rpgツクール" || ContainsAny(engine, EngineKeywords);
    }

    private static bool ContainsAny(string text, string[] keywords)
        => keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));

    private static readonly string[] NukigeKeywords =
    {
        "ntr", "凌辱", "调教", "触手", "轮奸", "强x", "肉便器", "孕ませ", "榨精", "援交",
        "h-scene", "vanilla", "ahegao", "impregnation", "mind break", "humiliation",
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

    private static readonly string[] DoujinKeywords =
    {
        // 裸 "rpg" 太宽泛（RPG 类型标签不代表同人），已移除；"rpgmaker" 等精确词足以覆盖引擎类
        "同人", "小黄油", "独立游戏", "indie", "rpgmaker", "wolf rpg",
    };

    private static readonly string[] EngineKeywords =
    {
        // 明确的同人/独立引擎工具：RPG Maker 家族、Wolf RPG、Tyrano、RPGツクール、Ren'Py。
        // 注意：不要把商业引擎（如 AUGUST 的 Artemis、KID 等）混进来，否则会把商业作误判为同人作
        // （曾误加 artemis/vib/kaguya 导致「秽翼的尤斯蒂娅」被归入同人作，2026-08-10 已移除）。
        "rpg", "wolf", "tyrano", "ティラノ", "ツクール", "renpy",
    };
}
