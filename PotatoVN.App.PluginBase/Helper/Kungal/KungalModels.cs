using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PotatoVN.App.PluginBase.Helper.Kungal;

/// <summary>kungal API 响应信封 {code, message, data}</summary>
internal class KungalEnvelope<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
}

/// <summary>搜索返回的轻量卡片（kungal v2：name=展示名，name_original=原名/日文名）</summary>
internal class KungalCard
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("name_original")] public string? NameOriginal { get; set; }
}

internal class KungalSearchData
{
    [JsonPropertyName("items")] public List<KungalCard> Items { get; set; } = new();
    [JsonPropertyName("total")] public int Total { get; set; }
}

/// <summary>游戏详情</summary>
internal class KungalDetail
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("vndb_id")] public string? VndbId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("name_original")] public string? NameOriginal { get; set; }
    [JsonPropertyName("introduction")] public List<KungalDetailIntro> Introduction { get; set; } = new();
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("tag")] public List<KungalTag> Tag { get; set; } = new();
    [JsonPropertyName("engine")] public List<KungalEngine> Engine { get; set; } = new();
    [JsonPropertyName("official")] public List<KungalOfficial> Official { get; set; } = new();
    [JsonPropertyName("ratings")] public List<KungalRating> Ratings { get; set; } = new();
    [JsonPropertyName("characters")] public List<KungalCharCard> Characters { get; set; } = new();
    [JsonPropertyName("external_ratings")] public List<KungalExternalRating> ExternalRatings { get; set; } = new();
}

/// <summary>详情简介条目（lang 如 zh-Hans/ja/en；zh-Hans 多为自动机器翻译）</summary>
internal class KungalDetailIntro
{
    [JsonPropertyName("lang")] public string? Lang { get; set; }
    [JsonPropertyName("intro")] public string? Intro { get; set; }
    [JsonPropertyName("machine")] public bool Machine { get; set; }
}

/// <summary>外部网站评分（kungal 聚合的 bangumi / vndb / erogamescape 评分）</summary>
internal class KungalExternalRating
{
    [JsonPropertyName("source")] public string? Source { get; set; }

    /// <summary>评分（bangumi/vndb 为 10 分制；erogamescape 为 100 分制）</summary>
    [JsonPropertyName("score")] public double? Score { get; set; }

    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
}

/// <summary>游戏详情的角色卡片（精简；角色简介需再拉角色详情接口）</summary>
internal class KungalCharCard
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("spoiler")] public int Spoiler { get; set; }
}

/// <summary>角色详情（/api/galgame-character/:id，匿名可访问）</summary>
internal class KungalCharacter
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("name_ja")] public string? NameJa { get; set; }
    [JsonPropertyName("name_original")] public string? NameOriginal { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("figure")] public string? Figure { get; set; }
    [JsonPropertyName("intro")] public string? Intro { get; set; }
    [JsonPropertyName("intros")] public List<KungalCharIntro> Intros { get; set; } = new();
    [JsonPropertyName("links")] public List<KungalCharLink> Links { get; set; } = new();
}

/// <summary>角色多语言简介条目（zh-Hans 为自动翻译，machine=true）</summary>
internal class KungalCharIntro
{
    [JsonPropertyName("lang")] public string? Lang { get; set; }
    [JsonPropertyName("intro")] public string? Intro { get; set; }
    [JsonPropertyName("machine")] public bool Machine { get; set; }
}

/// <summary>角色外部链接（VNDB / Bangumi，角色匹配锚点）</summary>
internal class KungalCharLink
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>用户评分（galgame_type 为用户的类型投票：moe/plot/ba_saku/daily…）</summary>
internal class KungalRating
{
    [JsonPropertyName("overall")] public int? Overall { get; set; }
    [JsonPropertyName("galgame_type")] public List<string>? GalgameType { get; set; }
}

internal class KungalTag
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("galgame_count")] public int GalgameCount { get; set; }
    [JsonPropertyName("spoiler_level")] public int SpoilerLevel { get; set; }
}

internal class KungalEngine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal class KungalOfficial
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
}
