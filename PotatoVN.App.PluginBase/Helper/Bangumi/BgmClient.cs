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
}
