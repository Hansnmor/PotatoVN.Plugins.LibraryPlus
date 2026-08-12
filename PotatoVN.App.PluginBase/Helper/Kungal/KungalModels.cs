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

/// <summary>多语言名称/简介（缺的语言为空串）</summary>
internal class KungalLang
{
    [JsonPropertyName("en-us")] public string EnUs { get; set; } = "";
    [JsonPropertyName("ja-jp")] public string JaJp { get; set; } = "";
    [JsonPropertyName("zh-cn")] public string ZhCn { get; set; } = "";
    [JsonPropertyName("zh-tw")] public string ZhTw { get; set; } = "";
}

/// <summary>搜索返回的轻量卡片</summary>
internal class KungalCard
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public KungalLang? Name { get; set; }
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
    [JsonPropertyName("name")] public KungalLang? Name { get; set; }
    [JsonPropertyName("introduction")] public KungalLang? Introduction { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("tag")] public List<KungalTag> Tag { get; set; } = new();
    [JsonPropertyName("engine")] public List<KungalEngine> Engine { get; set; } = new();
    [JsonPropertyName("official")] public List<KungalOfficial> Official { get; set; } = new();
    [JsonPropertyName("ratings")] public List<KungalRating> Ratings { get; set; } = new();
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
