using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PotatoVN.App.PluginBase.Helper.NextMoe;

/// <summary>
/// NextMoe 开放 API v2（https://api.nextmoe.dev/v2，文档 https://developer.nextmoe.dev/docs）的数据模型。
/// 与旧 kungal API 的关键差异（迁移期硬约束，勿按 kungal 习惯写）：
/// - 响应体就是集合/资源本身，**没有** {code,message,data} 信封；
/// - id 一律是十进制**字符串**（库里 int64，JSON number 会静默失真）；
/// - 分页只认 next_cursor（末页直接不出现该键，没有 has_more）；
/// - include= 写错 token 是 400 UNKNOWN_INCLUDE，不是静默少块；
/// - 错误体是 RFC 9457 problem+json（{type,title,status,detail,code}）。
/// </summary>
internal static class NextMoeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>角色简介条目。NextMoe 在不同端点返回两种形状：{lang,value,is_machine} 对象或 [lang,value] 二元数组，
/// 统一用转换器吸收。</summary>
internal class NextMoeIntro
{
    [JsonPropertyName("lang")] public string? Lang { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("intro")] public string? Intro { get; set; } // 兼容对象形状的旧字段名
    [JsonPropertyName("is_machine")] public bool IsMachine { get; set; }

    [JsonConverter(typeof(NextMoeIntroConverter))]
    internal sealed class NextMoeIntroConverter : JsonConverter<NextMoeIntro>
    {
        public override NextMoeIntro Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement e = doc.RootElement;
            var intro = new NextMoeIntro();
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("lang", out JsonElement langEl)) intro.Lang = langEl.GetString();
                    if (e.TryGetProperty("value", out JsonElement valEl)) intro.Value = valEl.GetString();
                    else if (e.TryGetProperty("intro", out JsonElement introEl)) intro.Value = introEl.GetString();
                    if (e.TryGetProperty("is_machine", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.True) intro.IsMachine = true;
                    break;
                case JsonValueKind.Array:
                    {
                        int i = 0;
                        foreach (JsonElement item in e.EnumerateArray())
                        {
                            if (i == 0) intro.Lang = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                            else if (i == 1) intro.Value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                            i++;
                        }
                        break;
                    }
            }
            return intro;
        }

        public override void Write(Utf8JsonWriter writer, NextMoeIntro value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new { value.Lang, value.Value, value.IsMachine }, options);
    }
}

/// <summary>本地化名称块：{"zh-Hans": {"value": "…", "is_machine": false}, …}</summary>
internal class NextMoeLocalizedValue
{
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("is_machine")] public bool IsMachine { get; set; }
}

/// <summary>外部 id 反查锚点（refs）：{source, external_id}，source ∈ vndb/bangumi/dlsite/erogamescape/steam/dmm…</summary>
internal class NextMoeRef
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("external_id")] public string? ExternalId { get; set; }
}

/// <summary>图片：{url, hash, width, height, thumbhash, sexual, violence, source}</summary>
internal class NextMoeImage
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("sexual")] public string? Sexual { get; set; }
}

/// <summary>分源评分（刻度保持源原生，绝不归一）：vndb/bangumi/nextmoe 为 10 分制，erogamescape 为 100 分制。</summary>
internal class NextMoeRating
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("vote_count")] public int? VoteCount { get; set; }
    [JsonPropertyName("rank")] public int? Rank { get; set; }
}

/// <summary>分源预计时长（分钟）。</summary>
internal class NextMoePlaytime
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("minutes")] public int? Minutes { get; set; }
    [JsonPropertyName("vote_count")] public int? VoteCount { get; set; }
}

/// <summary>tag_kind 词表（closed）：content | meta；spoiler 词表（closed）：none | minor | major。</summary>
internal class NextMoeTag
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("tag_kind")] public string? TagKind { get; set; }
    [JsonPropertyName("tier")] public string? Tier { get; set; }
    [JsonPropertyName("spoiler")] public string? Spoiler { get; set; }
    [JsonPropertyName("is_sexual")] public bool? IsSexual { get; set; }
    [JsonPropertyName("work_count")] public int? WorkCount { get; set; }
}

internal class NextMoeEngine
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("work_count")] public int? WorkCount { get; set; }
}

internal class NextMoeCompany
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("company_kind")] public string? CompanyKind { get; set; }
    [JsonPropertyName("attribution_role")] public string? AttributionRole { get; set; }
    [JsonPropertyName("work_count")] public int? WorkCount { get; set; }
}

/// <summary>角色（works/{id}?include=characters 给精简块；characters/{id} 详情端点可带 intros/refs/image/figure）。</summary>
internal class NextMoeCharacter
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("latin")] public string? Latin { get; set; }
    [JsonPropertyName("localized")] public Dictionary<string, NextMoeLocalizedValue>? Localized { get; set; }
    [JsonPropertyName("roster_role")] public string? RosterRole { get; set; }
    [JsonPropertyName("spoiler")] public string? Spoiler { get; set; }
    [JsonPropertyName("image")] public NextMoeImage? Image { get; set; }
    [JsonPropertyName("figure")] public NextMoeImage? Figure { get; set; }
    [JsonPropertyName("intros")] public List<NextMoeIntro>? Intros { get; set; }
    [JsonPropertyName("refs")] public List<NextMoeRef>? Refs { get; set; }
    [JsonPropertyName("aliases")] public List<NextMoeAlias>? Aliases { get; set; }
}

internal class NextMoeAlias
{
    [JsonPropertyName("lang")] public string? Lang { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("alias_kind")] public string? AliasKind { get; set; }
}

/// <summary>作品详情/集合条目。include= 决定哪些块出现（缺省瘦身，块缺失=未请求，不是没数据）。</summary>
internal class NextMoeWork
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("medium")] public string? Medium { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("latin")] public string? Latin { get; set; }
    [JsonPropertyName("localized")] public Dictionary<string, NextMoeLocalizedValue>? Localized { get; set; }
    [JsonPropertyName("olang")] public string? Olang { get; set; }
    [JsonPropertyName("content_rating")] public string? ContentRating { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("release_date_precision")] public string? ReleaseDatePrecision { get; set; }
    [JsonPropertyName("release_status")] public string? ReleaseStatus { get; set; }
    [JsonPropertyName("cover")] public NextMoeImage? Cover { get; set; }
    [JsonPropertyName("refs")] public List<NextMoeRef>? Refs { get; set; }
    [JsonPropertyName("intros")] public List<NextMoeIntro>? Intros { get; set; }
    [JsonPropertyName("ratings")] public List<NextMoeRating>? Ratings { get; set; }
    [JsonPropertyName("playtimes")] public List<NextMoePlaytime>? Playtimes { get; set; }
    [JsonPropertyName("tags")] public List<NextMoeTag>? Tags { get; set; }
    [JsonPropertyName("engines")] public List<NextMoeEngine>? Engines { get; set; }
    [JsonPropertyName("companies")] public List<NextMoeCompany>? Companies { get; set; }
    [JsonPropertyName("characters")] public List<NextMoeCharacter>? Characters { get; set; }
}

/// <summary>/v2/catalog/search 命中行（object=search_result）。</summary>
internal class NextMoeSearchItem
{
    [JsonPropertyName("object")] public string? Object { get; set; }
    [JsonPropertyName("target_object")] public string? TargetObject { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("latin")] public string? Latin { get; set; }
    [JsonPropertyName("localized")] public Dictionary<string, NextMoeLocalizedValue>? Localized { get; set; }
    [JsonPropertyName("sources")] public List<string>? Sources { get; set; }
    [JsonPropertyName("content_rating")] public string? ContentRating { get; set; }
}

/// <summary>集合响应（无信封）：{object:"list", items:[…], next_cursor?}。</summary>
internal class NextMoeList<T>
{
    [JsonPropertyName("object")] public string? Object { get; set; }
    [JsonPropertyName("items")] public List<T> Items { get; set; } = new();
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
    [JsonPropertyName("missing")] public List<string>? Missing { get; set; }
}

/// <summary>OAuth 令牌响应（裸 RFC 6749，无信封）；失败是 {error, error_description}。</summary>
internal class NextMoeTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

/// <summary>API 错误（RFC 9457 problem+json）。code ∈ MISSING_CREDENTIAL / INVALID_CREDENTIAL / SCOPE_REQUIRED /
/// NOT_FOUND / UNKNOWN_INCLUDE / RATE_LIMITED …</summary>
internal class NextMoeProblem
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("status")] public int? Status { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}

/// <summary>catalog 统计（匿名可用，用作连通性探测）。</summary>
internal class NextMoeCatalogStats
{
    [JsonPropertyName("works")] public int Works { get; set; }
    [JsonPropertyName("companies")] public int Companies { get; set; }
    [JsonPropertyName("characters")] public int Characters { get; set; }
}
