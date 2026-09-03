using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Helper.Bangumi;

/// <summary>Bangumi tag 投票数据（用户打的标签 + 投票数）</summary>
internal class BgmTag
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
}

internal class BgmSubjectResponse
{
    [JsonPropertyName("tags")] public List<BgmTag>? Tags { get; set; }
}

/// <summary>v1 搜索接口响应（搜索是匿名访问 R18 条目的唯一可靠路径）</summary>
internal class BgmSearchResult
{
    [JsonPropertyName("list")] public List<BgmSearchItem>? List { get; set; }
}

internal class BgmSearchItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("rating")] public BgmSearchRating? Rating { get; set; }
}

internal class BgmSearchRating
{
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
}

/// <summary>v0 详情接口响应（评分部分，带 token 拉取用）</summary>
internal class BgmSubjectRatingResponse
{
    [JsonPropertyName("rating")] public BgmSearchRating? Rating { get; set; }
}

/// <summary>v0 subject 角色列表项（/v0/subjects/:id/characters，带 token 拉取，R18 亦可见）</summary>
internal class BgmSubjectCharacter
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>资料卡 key-value 列表（简体中文名在 key="简体中文名" 处）</summary>
    [JsonPropertyName("infobox")] public List<JsonElement>? Infobox { get; set; }
}

/// <summary>
/// Bangumi API 客户端（https://api.bgm.tv）
/// 匿名 API 对部分条目返回 404（需登录才全量可见）——用宿主 Bangumi OAuth token（Bearer）鉴权。
/// 仅采集 tag 投票数据（name + count），供分类器作社区投票信号。
/// </summary>
internal class BgmClient
{
    private const string BaseUrl = "https://api.bgm.tv";

    /// <summary>Bangumi OAuth token（批量开始前从宿主设置反射读取）</summary>
    public string? Token { get; set; }

    /// <summary>批量采集节流（Bangumi 匿名限 60/min，登录后宽松，保守 1s）</summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>角色页抓取节流（网页服务比 API 宽松，保守 0.5s）</summary>
    public TimeSpan PageRequestDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    private static readonly HttpClient Client = new();

    /// <summary>拉取条目的 tag 投票列表；失败/未登录返回 null</summary>
    public async Task<List<BgmTag>?> GetTagsAsync(int subjectId)
    {
        if (RequestDelay > TimeSpan.Zero) await Task.Delay(RequestDelay);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v0/subjects/{subjectId}");
            request.Headers.UserAgent.ParseAdd("PotatoVN.LibraryPlus/1.0 (plugin)");
            if (!string.IsNullOrEmpty(Token))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null; // 404（条目不存在/匿名受限）→ 跳过
            var json = await response.Content.ReadAsStringAsync();
            var subject = JsonSerializer.Deserialize<BgmSubjectResponse>(json);
            return subject?.Tags;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// v1 搜索接口补拉条目评分（匿名可用，v0 详情接口对部分条目 404）。
    /// 匹配策略：① 结果中 id 精确锚定 <paramref name="bgmId"/>；② 未命中取第一个有评分的（v1 按相关度排序）。
    /// 返回 (score, count)；无匹配返回 null。
    /// </summary>
    public async Task<(double Score, int Count)?> SearchScoreAsync(int bgmId, string keyword)
    {
        if (RequestDelay > TimeSpan.Zero) await Task.Delay(RequestDelay);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/search/subject/{Uri.EscapeDataString(keyword)}?type=4&responseGroup=large");
            request.Headers.UserAgent.ParseAdd("PotatoVN.LibraryPlus/1.0 (plugin)");
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<BgmSearchResult>(json);
            if (result?.List is null) return null;

            BgmSearchItem? hit = result.List.FirstOrDefault(s => s.Id == bgmId && s.Rating is { Score: > 0, Total: > 0 });
            hit ??= result.List.FirstOrDefault(s => s.Rating is { Score: > 0, Total: > 0 });
            if (hit?.Rating is not { } rating || rating.Score <= 0 || rating.Total <= 0) return null;
            return (rating.Score, rating.Total);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// v0 详情接口 + Bearer token（宿主登录态）拉取评分。
    /// 匿名 v0 对部分条目（R18 等）404，带 token 全量可见——作为搜索关键词未命中时的终极兜底。
    /// </summary>
    public async Task<(double Score, int Count)?> GetScoreByTokenAsync(int subjectId, string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v0/subjects/{subjectId}");
            request.Headers.UserAgent.ParseAdd("PotatoVN.LibraryPlus/1.0 (plugin)");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            var subject = JsonSerializer.Deserialize<BgmSubjectRatingResponse>(json);
            if (subject?.Rating is not { } rating || rating.Score <= 0 || rating.Total <= 0) return null;
            return (rating.Score, rating.Total);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拉取 subject（游戏）的**全部角色**（带 token，R18 亦可见），返回 (bgm 角色 id, bgm 角色名)。
    /// 用途：kungal 角色无 bgm 链接时的「游戏级兜底」——调用方拿 kungal 日文原名在这里按名字匹配，
    /// 拿到 bgm 角色 **id** 后，再对该 id 抓角色网页解析「简体中文名」。
    /// 注意：不要试图从此接口的 infobox 里取「简体中文名」——实测 v0 characters 接口没有该数据
    /// （简体中文名只存在于角色网页 HTML），infobox 只给 日文原名/罗马音等；必须经网页拿简体中文名。
    /// 返回 (角色 id, 角色名) 列表；失败/无数据返回空列表。
    /// </summary>
    public async Task<List<(int CharId, string Name)>> GetSubjectCharactersAsync(int subjectId)
    {
        var result = new List<(int, string)>();
        if (string.IsNullOrEmpty(Token)) return result; // 无 token 匿名拉 R18 条目 404，直接跳过
        if (RequestDelay > TimeSpan.Zero) await Task.Delay(RequestDelay);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/v0/subjects/{subjectId}/characters");
            request.Headers.UserAgent.ParseAdd("PotatoVN.LibraryPlus/1.0 (plugin)");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return result; // 条目不存在/条目无角色/受限
            var json = await response.Content.ReadAsStringAsync();
            List<BgmSubjectCharacter>? chars = JsonSerializer.Deserialize<List<BgmSubjectCharacter>>(json);
            if (chars is null) return result;
            foreach (BgmSubjectCharacter ch in chars)
            {
                if (ch.Id <= 0 || string.IsNullOrWhiteSpace(ch.Name)) continue;
                result.Add((ch.Id, ch.Name.Trim()));
            }
            return result;
        }
        catch
        {
            return result;
        }
    }

    /// <summary>
    /// 抓 bgm 角色页解析「简体中文名」标签。
    /// 注意：v0 API 的 characters 接口没有 name_cn 字段（实测 v1/v2 版本头都没有），
    /// 简体中文名只存在于页面 HTML（&lt;span class="tip"&gt;简体中文名: &lt;/span&gt;XXX）。匿名可访问。
    /// </summary>
    public async Task<string?> GetCharacterCnNameAsync(int characterId)
    {
        if (PageRequestDelay > TimeSpan.Zero) await Task.Delay(PageRequestDelay);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://bgm.tv/character/{characterId}");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var html = await response.Content.ReadAsStringAsync();
            var match = System.Text.RegularExpressions.Regex.Match(html,
                "<span class=\"tip\">简体中文名: </span>([^<]+)");
            if (!match.Success) return null;
            return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
        }
        catch
        {
            return null;
        }
    }
}
