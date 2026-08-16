using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Helper.Vndb;

/// <summary>VNDB 官方 API 查询响应（/kana/vn）</summary>
internal class VndbVnResponse
{
    [JsonPropertyName("results")] public System.Collections.Generic.List<VndbVn>? Results { get; set; }
}

internal class VndbVn
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }

    /// <summary>贝叶斯评分（0~10 浮点，与 bangumi 同量纲）</summary>
    [JsonPropertyName("rating")] public double Rating { get; set; }

    [JsonPropertyName("votecount")] public int VoteCount { get; set; }
}

/// <summary>
/// VNDB 官方 API 客户端（https://api.vndb.org/kana/vn，匿名可用）。
/// 按 vn id 直查评分（rating + votecount）；id 归一化：缺 "v" 前缀自动补。
/// </summary>
internal class VndbClient
{
    private const string Endpoint = "https://api.vndb.org/kana/vn";

    /// <summary>礼貌节流（匿名限速宽松，默认 200ms）</summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>VNDB API token（可选增强；宿主若配置则注入，匿名已可用）</summary>
    public string? Token { get; set; }

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    /// <summary>
    /// 按 id 查询评分；无评分/失败返回 null。
    /// 注意：VNDB API 的 rating 是 0~100 浮点（贝叶斯），此处归一化为 10 分制（÷10），与 bangumi 同量纲。
    /// </summary>
    public async Task<(double Rating, int VoteCount)?> GetScoreAsync(string vndbId)
    {
        if (RequestDelay > TimeSpan.Zero) await Task.Delay(RequestDelay);
        try
        {
            string id = vndbId.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? vndbId : "v" + vndbId;
            string body = JsonSerializer.Serialize(new
            {
                filters = new object[] { "id", "=", id },
                fields = "title,rating,votecount",
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.UserAgent.ParseAdd(
                "PotatoVN-LibraryPlus/1.0 (https://github.com/Hansnmor/PotatoVN.Plugins.LibraryPlus)");
            if (!string.IsNullOrEmpty(Token))
                request.Headers.TryAddWithoutValidation("Authorization", "token " + Token);

            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<VndbVnResponse>(json);
            VndbVn? vn = result?.Results?.FirstOrDefault();
            if (vn is null || vn.Rating <= 0 || vn.VoteCount <= 0) return null;
            return (Math.Round(vn.Rating / 10.0, 3), vn.VoteCount);
        }
        catch
        {
            return null;
        }
    }
}
