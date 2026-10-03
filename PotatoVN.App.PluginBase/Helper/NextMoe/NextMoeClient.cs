using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Helper.NextMoe;

/// <summary>
/// NextMoe 开放 API v2 客户端（https://api.nextmoe.dev/v2）。
/// 凭据二选一（一条请求只带一个，服务端按前缀分道）：
/// - 应用密钥 nmk_live_/nmk_test_（按应用计配额：Free 档 60/分、50000/日）；
/// - 用户访问令牌（OAuth 授权码+PKCE 换发，按用户计配额：默认 100/分、10000/日）。
/// 分发的桌面客户端按官方要求走用户令牌（见 NextMoeAuth）；应用密钥仅限本地联调，绝不能写死分发。
/// 调用方通过 <see cref="CredentialProvider"/> 注入当前凭据（可为 null → 匿名，仅少数端点可用）。
/// </summary>
internal class NextMoeClient
{
    public const string BaseUrl = "https://api.nextmoe.dev";
    public const string AuthorizeEndpoint = "https://account.nextmoe.com/api/v1/oauth/authorize";
    public const string TokenEndpoint = "https://account.nextmoe.com/api/v1/oauth/token";

    /// <summary>
    /// 令牌桶节流：允许短时突发（一批并发请求同批放行），长期平均速率压在配额线下。
    /// </summary>
    /// <remarks>
    /// 为什么不能写成「锁内 await Task.Delay 保证最小间隔」——那是严格串行：
    /// FetchCharacterIntrosAsync 用 Task.WhenAll 并发拉角色详情，但每个请求都会先 await 节流，
    /// 于是 N 个请求的代价从 1×RTT 变成 N×(间隔+RTT)。
    /// 日志实测：一款 11 角色的游戏，详情到手到角色解析完成花了 24.7s（11×700ms 排队 + 网络），
    /// 换成令牌桶后同一批只花约 1 个 RTT。
    /// 突发安全性：NextMoe 的限流是窗口计数（响应头 RateLimit-Policy 形如 `100;w=60`），
    /// 不是容量为 1 的硬令牌桶，8 个突发远低于窗口上限。
    /// </remarks>
    private const double RefillPerSecond = 1.4; // ≈84 次/分，压在用户令牌 100/分 的线下

    private const double BurstCapacity = 8;     // 一批并发（如十几个角色详情）能一次放行

    private static readonly SemaphoreSlim ThrottleLock = new(1, 1);
    private static double _tokens = BurstCapacity;
    private static DateTime _lastRefillUtc = DateTime.UtcNow;

    private static async Task ThrottleAsync()
    {
        while (true)
        {
            TimeSpan wait;
            await ThrottleLock.WaitAsync();
            try
            {
                DateTime now = DateTime.UtcNow;
                _tokens = Math.Min(BurstCapacity,
                    _tokens + (now - _lastRefillUtc).TotalSeconds * RefillPerSecond);
                _lastRefillUtc = now;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
                wait = TimeSpan.FromSeconds((1 - _tokens) / RefillPerSecond);
            }
            finally
            {
                ThrottleLock.Release();
            }
            await Task.Delay(wait);
        }
    }

    /// <summary>凭据提供者（应用密钥或用户 access token）。null → 匿名请求。</summary>
    public Func<Task<string?>>? CredentialProvider { get; set; }

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            // 跟随系统代理（默认行为），**不要写 UseProxy = false**（2026-10-03 实测后改回）：
            // 迁移期（2026-09）直连确实略快（当时实测 1.07s vs 代理 1.99s），但现在反过来了，
            // 且直连抖动极大——同一请求各打两次的实测（2026-10-03）：
            //   /catalog/stats                        直连 0.95s / 6.23s   代理 1.12s / 0.67s
            //   作品详情（本插件同款 include）        直连 5.59s / 2.40s   代理 1.57s / 0.92s
            //   角色批量水合 21 个 id（同款 include） 直连 5.99s / 1.55s   代理 1.51s / 0.90s
            // 直连还会偶发 TLS 握手卡死（多次复现），后果是宿主日志里的 20 秒级尖刺
            // （实例：「kungal 角色详情: 11 个批量水合耗时 22296ms」——那是一个请求的耗时：
            //  11 个 id 在 GetCharactersAsync 里只发 1 次 ids= 批量请求，不是 N+1）。
            // 跟随系统代理则 Clash 开着走代理、关掉（ProxyEnable=0）自动回直连，自适应两种环境。
            // BgmClient 早有同款结论（bgm.tv 直连必超时）；KungalWebClient 2026-10-03 同样改为跟随系统代理。
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <summary>GET 并把响应体直接反序列化为 T（无信封）。失败（网络/4xx/5xx）返回 default 并打日志。</summary>
    public async Task<T?> GetAsync<T>(string path)
    {
        await ThrottleAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            await AddCredentialAsync(request);
            using HttpResponseMessage response = await Client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                LogProblem(path, response.StatusCode, body);
                return default;
            }
            return JsonSerializer.Deserialize<T>(body, NextMoeJson.Options);
        }
        catch (Exception e)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"nextmoe 请求失败: {path} ({e.Message})");
            return default;
        }
    }

    /// <summary>POST 表单（OAuth 端点用，裸 RFC 6749，无信封）。</summary>
    public static async Task<NextMoeTokenResponse?> PostFormAsync(string url, string formBody)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(formBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            using HttpResponseMessage response = await Client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<NextMoeTokenResponse>(body, NextMoeJson.Options);
            if (result is null || (!string.IsNullOrEmpty(result.Error) && string.IsNullOrEmpty(result.AccessToken)))
            {
                Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    $"nextmoe oauth 失败: {result?.Error} {result?.ErrorDescription} (http={(int)response.StatusCode})");
                return null;
            }
            return result;
        }
        catch (Exception e)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"nextmoe oauth 请求异常: {e.Message}");
            return null;
        }
    }

    /// <summary>标题搜索（object=work）。q= 走搜索索引；R18 必须显式 nsfw=true（缺省隐藏，galgame 绝大多数 R18）。</summary>
    public Task<NextMoeList<NextMoeSearchItem>?> SearchWorksAsync(string q, int limit = 5)
        => GetAsync<NextMoeList<NextMoeSearchItem>>(
            $"/v2/catalog/search?object=work&q={Uri.EscapeDataString(q)}&nsfw=true&limit={limit}");

    /// <summary>外部 id 反查（一次最多 100 个 source:external_id，未命中的回 missing[]，不会整请求 404）。</summary>
    public Task<NextMoeList<NextMoeWork>?> GetWorksByRefsAsync(string refs)
        => GetAsync<NextMoeList<NextMoeWork>>(
            $"/v2/catalog/works?refs={Uri.EscapeDataString(refs)}&nsfw=true");

    /// <summary>作品详情。include 写错 token 是 400 UNKNOWN_INCLUDE（不是静默少块）。</summary>
    public Task<NextMoeWork?> GetWorkAsync(string id, string include)
        => GetAsync<NextMoeWork>(
            $"/v2/catalog/works/{Uri.EscapeDataString(id)}?nsfw=true&include={Uri.EscapeDataString(include)}");

    /// <summary>角色详情（works/{id}?include=characters 给的精简块不带 intros/refs，intros 与外链 id 要走这里）。</summary>
    public Task<NextMoeCharacter?> GetCharacterAsync(string id, string include)
        => GetAsync<NextMoeCharacter>(
            $"/v2/catalog/characters/{Uri.EscapeDataString(id)}?nsfw=true&include={Uri.EscapeDataString(include)}");

    /// <summary>
    /// 角色**批量水合**（消灭 N+1 的批量车道）：一次最多 100 个 id，无分页，
    /// 请求了但不可见的 key 原样回 missing[]（不会让整个请求 404）。
    /// 实测字段与单品端点同形（include=intros,refs,image,figure 两边一致），可安全互换。
    /// </summary>
    public Task<NextMoeList<NextMoeCharacter>?> GetCharactersByIdsAsync(IEnumerable<int> ids, string include)
    {
        string joined = string.Join(",", ids);
        return GetAsync<NextMoeList<NextMoeCharacter>>(
            $"/v2/catalog/characters?ids={Uri.EscapeDataString(joined)}&nsfw=true&include={Uri.EscapeDataString(include)}");
    }

    private static bool _noCredentialWarned;

    private static async Task AddCredentialAsync(HttpRequestMessage request)
    {
        Func<Task<string?>>? provider = null;
        try { provider = InstanceCredentialProvider; } catch { /* 未接线 */ }
        string? credential = provider != null ? await provider() : null;
        if (string.IsNullOrWhiteSpace(credential))
        {
            // 未登录 → 请求会 401 MISSING_CREDENTIAL。只提示一次（每个进程），否则批量搜刮刷屏。
            if (!_noCredentialWarned)
            {
                _noCredentialWarned = true;
                Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
                    "nextmoe 未登录，搜刮无法使用：请在游戏库页「更多」→「登录 NextMoe 账号（搜刮数据源）」完成一次浏览器授权。");
            }
            return;
        }
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential}");
    }

    /// <summary>登录成功后调用：复位「未登录」提示，下次真的没凭据时还能再提示一次。</summary>
    public static void ResetCredentialNotice() => _noCredentialWarned = false;

    /// <summary>全局凭据提供者（由 Plugin 启动时接线；静态以便搜刮器多实例共享）。</summary>
    public static Func<Task<string?>>? InstanceCredentialProvider;

    private static void LogProblem(string path, System.Net.HttpStatusCode status, string body)
    {
        string detail = body;
        try
        {
            var problem = JsonSerializer.Deserialize<NextMoeProblem>(body, NextMoeJson.Options);
            if (problem?.Code != null)
                detail = $"{problem.Code}: {problem.Detail ?? problem.Title}";
        }
        catch { /* 非 problem+json，原样打 */ }
        Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
            $"nextmoe {path} → {(int)status} {detail}");
    }
}
