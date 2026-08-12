using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Helper.Kungal;

/// <summary>
/// kungal API 客户端（https://www.kungal.com/api）
/// 匿名 GET 即可访问；统一带 SFW 解锁 cookie 防御未来过滤扩展。
/// </summary>
internal class KungalClient
{
    private const string BaseUrl = "https://www.kungal.com/api";
    private const string SfwCookie = "KUNGalgameSettings={\"showKUNGalgameContentLimit\":\"all\"}";

    /// <summary>批量搜刮时的礼貌节流（默认 200ms）</summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    /// <summary>GET 并解析 {code,message,data} 信封；code!=0 或解析失败返回 default</summary>
    public async Task<T?> GetAsync<T>(string path)
    {
        if (RequestDelay > TimeSpan.Zero) await Task.Delay(RequestDelay);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            // HttpClient 禁止直接设置 Cookie 头，用 TryAddWithoutValidation 绕过校验
            request.Headers.TryAddWithoutValidation("Cookie", SfwCookie);
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var envelope = JsonSerializer.Deserialize<KungalEnvelope<T>>(json);
            return envelope is { Code: 0 } ? envelope.Data : default;
        }
        catch (Exception e)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"kungal 请求失败: {path} ({e.Message})");
            return default;
        }
    }

    /// <summary>按关键词搜索游戏（支持 vndb_id / 名称）</summary>
    public Task<KungalSearchData?> SearchAsync(string keywords, int limit = 5)
        => GetAsync<KungalSearchData>($"/search?keywords={Uri.EscapeDataString(keywords)}&type=galgame&page=1&limit={limit}");

    /// <summary>按 gid 拉取详情</summary>
    public Task<KungalDetail?> GetDetailAsync(int gid)
        => GetAsync<KungalDetail>($"/galgame/{gid}");

    /// <summary>HTML 简介转纯文本（AngleSharp）</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        try
        {
            var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
            return System.Net.WebUtility.HtmlDecode(document.Body?.TextContent?.Trim() ?? "");
        }
        catch
        {
            return System.Net.WebUtility.HtmlDecode(html);
        }
    }
}
