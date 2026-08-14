using System;
using System.Collections.Generic;
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
