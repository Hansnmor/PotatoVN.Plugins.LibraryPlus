using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PotatoVN.App.PluginBase.Helper.Kungal;

namespace PotatoVN.App.PluginBase.Helper.KungalWeb;

/// <summary>
/// 把 kungal 网站 SSR 数据（<see cref="KungalWebClient"/> 还原出的嵌套字典）
/// 映射成插件内部通用的 <see cref="KungalDetail"/>，好让 <see cref="KungalPhraser"/> 的
/// 匹配与写回逻辑一行都不用改。
/// 关键：work-ratings 的 <c>game_types</c> 映射进 <see cref="KungalDetail.Ratings"/> 的
/// <c>GalgameType</c>，于是 <c>BuildKungalData</c> 的拆票逻辑照旧生效（这是 NextMoe 拿不到的数据）。
/// </summary>
internal static class KungalWebMapper
{
    /// <summary>从抓取结果里挑出该 gid 的 work（优先 nsfw 完整视图，回退 sfw）。</summary>
    public static Dictionary<string, object?>? PickWork(
        Dictionary<string, object?> pageData, int gid)
    {
        foreach (string suffix in new[] { "nsfw", "sfw" })
            if (pageData.TryGetValue($"api:work:{gid}:{suffix}", out object? w) &&
                KungalWebClient.AsDict(w) is { } dict)
                return UnwrapData(dict);

        // 兜底：键名规则变了也要能找到（只认 work 形状的对象）
        return pageData.Values
            .Select(KungalWebClient.AsDict)
            .FirstOrDefault(d => d != null && KungalWebClient.Get(d, "display_name") != null
                                            && KungalWebClient.Get(d, "roster") != null);
    }

    /// <summary>把网页 work 对象转成 KungalDetail。</summary>
    public static KungalDetail ToDetail(int gid, Dictionary<string, object?> work,
        Dictionary<string, object?>? ratingsRoot)
    {
        string? displayName = KungalWebClient.AsStr(KungalWebClient.Get(work, "display_name"));
        string? zh = PickLocalized(KungalWebClient.Get(work, "localized"), "zh-Hans");

        return new KungalDetail
        {
            Id = gid,
            VndbId = PickVndbId(KungalWebClient.Get(work, "external_refs")),
            Name = zh,
            NameOriginal = displayName,
            ReleaseDate = KungalWebClient.AsStr(KungalWebClient.Get(work, "release_date")),
            Introduction = MapIntros(KungalWebClient.Get(work, "intros")),
            Tag = MapTags(KungalWebClient.Get(work, "tags")),
            Engine = MapEngines(KungalWebClient.Get(work, "engines")),
            Official = MapCompanies(KungalWebClient.Get(work, "companies")),
            Characters = MapRoster(KungalWebClient.Get(work, "roster")),
            ExternalRatings = MapExternalRatings(KungalWebClient.Get(work, "external_ratings")),
            // ★ 用户类型投票：网页 work-ratings 的每条评分各带 game_types[]
            Ratings = MapRatings(ratingsRoot),
        };
    }

    // ===== 各块映射 =====

    private static List<KungalDetailIntro> MapIntros(object? v)
    {
        var result = new List<KungalDetailIntro>();
        foreach (Dictionary<string, object?> item in Items(v))
            result.Add(new KungalDetailIntro
            {
                Lang = KungalWebClient.AsStr(KungalWebClient.Get(item, "locale")),
                Intro = KungalWebClient.AsStr(KungalWebClient.Get(item, "value")),
                Machine = KungalWebClient.AsBool(KungalWebClient.Get(item, "is_machine")),
            });
        return result;
    }

    /// <summary>
    /// tag 映射。与 NextMoe 侧同一套语义：
    /// NextMoe/kungal 网站都用 <c>is_sexual</c> 布尔表达性相关，而旧 kungal API 是独立的 "sexual" 类 ——
    /// 这里同样还原成 "sexual"，好让 CleanTags 的 `Category == "content"` 过滤恢复原行为。
    /// </summary>
    private static List<KungalTag> MapTags(object? v)
    {
        var result = new List<KungalTag>();
        foreach (Dictionary<string, object?> item in Items(v))
            result.Add(new KungalTag
            {
                Name = KungalWebClient.AsStr(KungalWebClient.Get(item, "display_name")) ?? "",
                Category = KungalWebClient.AsBool(KungalWebClient.Get(item, "is_sexual"))
                    ? "sexual"
                    : KungalWebClient.AsStr(KungalWebClient.Get(item, "tag_kind")) ?? "",
                GalgameCount = (int)(KungalWebClient.AsNum(
                    KungalWebClient.Get(item, "catalog_work_count")) ?? 0),
                SpoilerLevel = SpoilerToInt(KungalWebClient.AsStr(KungalWebClient.Get(item, "spoiler"))),
            });
        return result;
    }

    private static List<KungalEngine> MapEngines(object? v)
    {
        var result = new List<KungalEngine>();
        foreach (Dictionary<string, object?> item in Items(v))
            result.Add(new KungalEngine
            {
                Id = ParseInt(KungalWebClient.AsStr(KungalWebClient.Get(item, "id"))),
                Name = KungalWebClient.AsStr(KungalWebClient.Get(item, "display_name")),
            });
        return result;
    }

    private static List<KungalOfficial> MapCompanies(object? v)
    {
        var result = new List<KungalOfficial>();
        foreach (Dictionary<string, object?> item in Items(v))
        {
            var roles = new List<string>();
            if (KungalWebClient.AsList(KungalWebClient.Get(item, "attribution_roles")) is { } raw)
                roles.AddRange(raw.Select(KungalWebClient.AsStr).Where(s => !string.IsNullOrWhiteSpace(s))!);
            result.Add(new KungalOfficial
            {
                Id = ParseInt(KungalWebClient.AsStr(KungalWebClient.Get(item, "id"))),
                Name = KungalWebClient.AsStr(KungalWebClient.Get(item, "display_name")),
                Category = KungalWebClient.AsStr(KungalWebClient.Get(item, "company_kind")) ?? "",
                Roles = roles,
            });
        }
        return result;
    }

    /// <summary>
    /// roster → 角色卡片。只需要 id（后续用 NextMoe 的 characters?ids= 批量补水简介与外链）
    /// 与 spoiler（剧透角色整条跳过）。
    /// </summary>
    private static List<KungalCharCard> MapRoster(object? v)
    {
        var result = new List<KungalCharCard>();
        foreach (Dictionary<string, object?> item in Items(v))
        {
            string? name = PickLocalized(KungalWebClient.Get(item, "localized"), "zh-Hans")
                           ?? KungalWebClient.AsStr(KungalWebClient.Get(item, "display_name"));
            result.Add(new KungalCharCard
            {
                Id = ParseInt(KungalWebClient.AsStr(KungalWebClient.Get(item, "id"))),
                Name = name,
                Spoiler = SpoilerToInt(KungalWebClient.AsStr(KungalWebClient.Get(item, "spoiler"))),
            });
        }
        return result;
    }

    private static List<KungalExternalRating> MapExternalRatings(object? v)
    {
        var result = new List<KungalExternalRating>();
        foreach (Dictionary<string, object?> item in Items(v))
            result.Add(new KungalExternalRating
            {
                Source = KungalWebClient.AsStr(KungalWebClient.Get(item, "site")),
                Score = KungalWebClient.AsNum(KungalWebClient.Get(item, "rating_value")),
                VoteCount = (int)(KungalWebClient.AsNum(KungalWebClient.Get(item, "vote_count")) ?? 0),
            });
        return result;
    }

    /// <summary>
    /// work-ratings → <see cref="KungalRating"/>（只取 <c>game_types</c>）。
    /// <c>BuildKungalData</c> 会按「一条评分勾 N 个类型各计 1/N」拆票，与旧 galgame_type 语义一致。
    /// </summary>
    private static List<KungalRating> MapRatings(Dictionary<string, object?>? ratingsRoot)
    {
        var result = new List<KungalRating>();
        object? items = null;
        // 形状：{ok, data:{items:[…]}}（实测）；容错再试一层 {items:[…]}
        if (KungalWebClient.Get(ratingsRoot, "data") is { } data)
            items = KungalWebClient.Get(KungalWebClient.AsDict(data), "items");
        items ??= KungalWebClient.Get(ratingsRoot, "items");

        foreach (Dictionary<string, object?> item in Items(items))
        {
            var types = new List<string>();
            if (KungalWebClient.AsList(KungalWebClient.Get(item, "game_types")) is { } raw)
                types.AddRange(raw.Select(KungalWebClient.AsStr)
                    .Where(s => !string.IsNullOrWhiteSpace(s))!);
            if (types.Count > 0)
                result.Add(new KungalRating { GalgameType = types });
        }
        return result;
    }

    // ===== 小工具 =====

    /// <summary>网页的 work 有时包一层 {ok,data}，有时直接是 work 对象。</summary>
    private static Dictionary<string, object?> UnwrapData(Dictionary<string, object?> dict)
        => KungalWebClient.Get(dict, "data") is { } d &&
           KungalWebClient.AsDict(d) is { } inner &&
           KungalWebClient.Get(inner, "display_name") != null
            ? inner
            : dict;

    private static IEnumerable<Dictionary<string, object?>> Items(object? v)
        => KungalWebClient.AsList(v)?
               .Select(KungalWebClient.AsDict)
               .Where(d => d != null)!
               .Select(d => d!) ?? Enumerable.Empty<Dictionary<string, object?>>();

    /// <summary>取 localized（{lang:{value,is_machine}}）里的指定语言。</summary>
    private static string? PickLocalized(object? v, string lang)
    {
        if (KungalWebClient.AsDict(v) is not { } dict) return null;
        if (KungalWebClient.AsDict(dict.GetValueOrDefault(lang)) is { } entry)
            return KungalWebClient.AsStr(KungalWebClient.Get(entry, "value"))?.Trim();
        return null;
    }

    /// <summary>external_refs 里找 vndb 的 vNNNN（字段名各版本不一，宽松匹配）。</summary>
    private static string? PickVndbId(object? v)
    {
        foreach (Dictionary<string, object?> item in Items(v))
        {
            string? site = KungalWebClient.AsStr(KungalWebClient.Get(item, "site"))
                           ?? KungalWebClient.AsStr(KungalWebClient.Get(item, "source"));
            if (!string.Equals(site, "vndb", StringComparison.OrdinalIgnoreCase)) continue;
            string? id = KungalWebClient.AsStr(KungalWebClient.Get(item, "external_id"))
                         ?? KungalWebClient.AsStr(KungalWebClient.Get(item, "external_ref_id"))
                         ?? KungalWebClient.AsStr(KungalWebClient.Get(item, "id"))
                         ?? KungalWebClient.AsStr(KungalWebClient.Get(item, "value"));
            if (id != null && Regex.IsMatch(id, @"^v?\d+$"))
                return id.StartsWith('v') ? id : "v" + id;
        }
        return null;
    }

    private static int SpoilerToInt(string? spoiler) => spoiler switch
    {
        "minor" => 1,
        "major" => 2,
        _ => 0,
    };

    private static int ParseInt(string? s) => int.TryParse(s, out int v) ? v : 0;
}
