using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Helper.KungalWeb;

/// <summary>
/// kungal **网站** 数据抓取。
/// 背景：kungal 的 HTTP API（/api/*）已全站改为需登录（401 {"code":205}），
/// 但**网站本身的 SSR 仍然把完整数据内嵌在页面里**，匿名可读。
/// 机制：galgame 页 HTML 内含 Nuxt 3 的 SSR 载荷
/// &lt;script id="__NUXT_DATA__"&gt;[…]&lt;/script&gt;，键形如
/// <c>"api:work:{gid}:nsfw"</c>、<c>"api:work-ratings:{gid}"</c> ——
/// 服务端在渲染时已代为调用并把结果内嵌，所以客户端直接解析 HTML 即可绕过登录校验。
/// 带旧插件同款 SFW 解锁 cookie 可把视图从 <c>sfw</c> 切到 <c>nsfw</c>
/// （nsfw 视图才给全 tag 与 tag 的剧透标记，sfw 视图会把性相关 tag 全部滤掉）。
/// </summary>
internal class KungalWebClient
{
    private const string BaseUrl = "https://www.kungal.com";

    /// <summary>与旧 KungalClient 同款的 SFW 解锁 cookie；不带它只能拿到 sfw 精简视图。</summary>
    private const string SfwUnlockCookie =
        "KUNGalgameSettings={\"showKUNGalgameContentLimit\":\"all\"}";

    private static readonly Regex PayloadRegex = new(
        "<script[^>]*id=\"__NUXT_DATA__\"[^>]*>(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 静态节流（跨实例共享）：每次抓取都是一个约 1MB 的页面，批量搜刮几十款时
    /// （几十 MB 流量 + 对 kungal 网站的压力）必须限速。600ms ≈ 100 页/分。
    /// </summary>
    private static readonly System.Threading.SemaphoreSlim ThrottleLock = new(1, 1);
    private static DateTime _nextAllowedUtc = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(600);

    private static async Task ThrottleAsync()
    {
        await ThrottleLock.WaitAsync();
        try
        {
            TimeSpan wait = _nextAllowedUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _nextAllowedUtc = DateTime.UtcNow + MinInterval;
        }
        finally
        {
            ThrottleLock.Release();
        }
    }

    /// <summary>页面约 1MB，超时给宽一点。</summary>
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            // kungal 直连可用且更快（实测直连 1.2~2.6s 返回 200）→ 绕开系统代理。
            UseProxy = false,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return client;
    }

    /// <summary>
    /// 抓 galgame 页并还原出所有 <c>api:</c> 缓存项。
    /// 返回键 = 原始键名（如 <c>api:work:49:nsfw</c>），值 = 还原后的嵌套结构
    /// （Dictionary&lt;string,object?&gt; / List&lt;object?&gt; / string / double / bool / null）。
    /// </summary>
    public async Task<Dictionary<string, object?>> FetchPageDataAsync(int gid)
    {
        await ThrottleAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/galgame/{gid}");
            // HttpClient 禁止直接设 Cookie 头，用 TryAddWithoutValidation 绕过校验
            request.Headers.TryAddWithoutValidation("Cookie", SfwUnlockCookie);
            using HttpResponseMessage response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    $"kungal 网页抓取失败: gid={gid} http={(int)response.StatusCode}");
                return new Dictionary<string, object?>();
            }
            string page = await response.Content.ReadAsStringAsync();
            Dictionary<string, object?> data = ExtractPayload(page, gid);
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                $"kungal 网页抓取: gid={gid} 耗时 {sw.ElapsedMilliseconds}ms 页面 {page.Length / 1024}KB 还原 {data.Count} 项");
            return data;
        }
        catch (Exception e)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"kungal 网页抓取异常: gid={gid} ({e.Message})");
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// 从 HTML 里抠出 __NUXT_DATA__，**只还原与该 gid 相关的键**。
    /// </summary>
    /// <remarks>
    /// ⚠️ **绝对不要改成"全量还原所有 api: 键"**。页面里除了 work 还有
    /// <c>api:wall:galgame:{gid}</c>（评论墙）、<c>api:quizzes-work</c>、<c>api:work-resources</c> 等，
    /// 它们的对象图共享且互相引用；全量展开会指数级膨胀 —— 实测把 PotatoVN 吃到 **6.9GB 内存并卡死**
    /// （2026-09-26 事故）。要加新数据就显式加进白名单，别放开扫描。
    /// </remarks>
    internal static Dictionary<string, object?> ExtractPayload(string page, int gid)
    {
        var result = new Dictionary<string, object?>();
        Match m = PayloadRegex.Match(page);
        if (!m.Success) return result;

        string json = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        JsonElement[] pool = doc.RootElement.EnumerateArray().ToArray();
        var wanted = new HashSet<string>(StringComparer.Ordinal)
        {
            $"api:work:{gid}:nsfw",
            $"api:work:{gid}:sfw",
            $"api:work-ratings:{gid}",
        };
        var memo = new Dictionary<int, object?>();

        foreach (JsonElement element in pool)
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (!wanted.Contains(prop.Name)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Number &&
                    prop.Value.TryGetInt32(out int idx) && idx >= 0 && idx < pool.Length)
                    result[prop.Name] = Resolve(pool, idx, 0, memo);
            }
        }
        return result;
    }

    /// <summary>解引用深度上限（页面数据没这么深，超过就是撞上环了）。</summary>
    private const int MaxDepth = 40;

    /// <summary>
    /// 按下标解引用 devalue 池，**带记忆化**。
    /// 同一对象常被多处引用（这正是 devalue 扁平化去重的目的），不记忆化就会重复展开同一棵子树；
    /// 数据里一旦有互相引用，分支数立刻指数增长。
    /// </summary>
    private static object? Resolve(JsonElement[] pool, int index, int depth,
        Dictionary<int, object?> memo)
    {
        if (depth > MaxDepth) return null;
        if (memo.TryGetValue(index, out object? cached)) return cached;

        JsonElement value = pool[index];
        object? result;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>();
                foreach (JsonProperty p in value.EnumerateObject())
                    dict[p.Name] = Deref(pool, p.Value, depth + 1, memo);
                result = dict;
                break;
            }
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (JsonElement item in value.EnumerateArray())
                    list.Add(Deref(pool, item, depth + 1, memo));
                result = list;
                break;
            }
            default:
                result = Literal(value);
                break;
        }
        memo[index] = result;
        return result;
    }

    /// <summary>容器内的元素若是整数下标则继续解引用，否则就是字面量。</summary>
    private static object? Deref(JsonElement[] pool, JsonElement value, int depth,
        Dictionary<int, object?> memo)
        => value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out int idx) && idx >= 0 && idx < pool.Length
            ? Resolve(pool, idx, depth, memo)
            : Literal(value);

    private static object? Literal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // ===== 还原结果的取值助手（结构是 Dictionary/List 嵌套，取值处处要判型） =====

    internal static Dictionary<string, object?>? AsDict(object? v)
        => v as Dictionary<string, object?>;

    internal static List<object?>? AsList(object? v) => v as List<object?>;

    internal static string? AsStr(object? v) => v switch
    {
        string s => s,
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    internal static double? AsNum(object? v) => v switch
    {
        double d => d,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double r) => r,
        _ => null,
    };

    internal static bool AsBool(object? v) => v is true;

    /// <summary>取字典里的键（找不到返回 null）。</summary>
    internal static object? Get(Dictionary<string, object?>? dict, string key)
        => dict != null && dict.TryGetValue(key, out object? v) ? v : null;
}
