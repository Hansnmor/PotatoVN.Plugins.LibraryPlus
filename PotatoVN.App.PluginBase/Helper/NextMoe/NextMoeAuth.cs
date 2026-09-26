using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PotatoVN.App.PluginBase.Helper.NextMoe;

/// <summary>
/// NextMoe 用户访问令牌（OAuth 2.0 授权码 + PKCE，RFC 8252 原生桌面应用形态）。
/// 硬约束（官方文档 docs/native-app）：
/// - 强制 PKCE (S256)，public client，**无 client_secret**；
/// - 回调只收环回：127.0.0.1 / [::1]，localhost 按名拒、自定义 scheme 拒；端口无关匹配（注册不写端口，运行时任意端口）；
/// - 必须用系统浏览器，绝不用内嵌 WebView；
/// - access_token（JWT，15 分钟）留内存；refresh_token（不透明串）必须加密落盘（DPAPI CurrentUser）；
/// - 每次刷新都轮换 refresh_token——拿到新的必须原地覆盖；
/// - 不要用第一方 /api/v1/auth/refresh（拒 client-bound OAuth session），只走 /oauth/token。
/// </summary>
internal static class NextMoeAuth
{
    /// <summary>NextMoe 开发者平台控制台注册的 client_id（public client 标识，非机密，可随插件分发）。</summary>
    public const string ClientId = "a5926926f8161a82da01ee8f46e8c38d";
    /// <summary>控制台「用户登录」注册的回调（端口无关匹配；运行时用随机端口拼 redirect_uri）。</summary>
    public const string RedirectPath = "/callback";
    /// <summary>授权 scope。catalog:read 无需注册即可请求；openid 始终授予。</summary>
    public const string Scope = "openid profile catalog:read";
    private static readonly TimeSpan AuthorizeTimeout = TimeSpan.FromMinutes(5);

    /// <summary>跑一次完整授权：起环回监听 → 系统浏览器打开授权页 → 收回调换令牌。失败返回 null（已打日志）。</summary>
    public static async Task<NextMoeTokenResponse?> AuthorizeAsync()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (Exception e)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
                $"nextmoe 登录失败: 无法监听本地端口 ({e.Message})");
            return null;
        }
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirect = $"http://127.0.0.1:{port}{RedirectPath}";

        string url = $"{NextMoeClient.AuthorizeEndpoint}?client_id={Uri.EscapeDataString(ClientId)}" +
                     $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                     "&response_type=code" +
                     $"&scope={Uri.EscapeDataString(Scope)}" +
                     $"&state={Uri.EscapeDataString(state)}" +
                     $"&code_challenge={challenge}&code_challenge_method=S256";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            listener.Stop();
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
                $"nextmoe 登录失败: 无法打开浏览器 ({e.Message})");
            return null;
        }

        string? code = await WaitForCallbackAsync(listener, state);
        listener.Stop();
        if (code == null)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                "nextmoe 登录未完成（超时/取消/state 不匹配）");
            return null;
        }
        NextMoeTokenResponse? token = await ExchangeCodeAsync(code, verifier, redirect);
        if (token != null)
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
                "nextmoe 登录成功");
        return token;
    }

    /// <summary>刷新令牌（每次都会轮换——调用方必须把返回的新 refresh_token 覆盖保存）。</summary>
    public static Task<NextMoeTokenResponse?> RefreshAsync(string refreshToken)
    {
        string form = "grant_type=refresh_token" +
                      $"&refresh_token={Uri.EscapeDataString(refreshToken)}" +
                      $"&client_id={Uri.EscapeDataString(ClientId)}";
        return NextMoeClient.PostFormAsync(NextMoeClient.TokenEndpoint, form);
    }

    private static Task<NextMoeTokenResponse?> ExchangeCodeAsync(string code, string verifier, string redirect)
    {
        string form = "grant_type=authorization_code" +
                      $"&code={Uri.EscapeDataString(code)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                      $"&client_id={Uri.EscapeDataString(ClientId)}" +
                      $"&code_verifier={Uri.EscapeDataString(verifier)}";
        return NextMoeClient.PostFormAsync(NextMoeClient.TokenEndpoint, form);
    }

    /// <summary>环回收回调：逐连接解析 query，state 不匹配的回 400 并继续等；拿到 code 或超时返回。</summary>
    private static async Task<string?> WaitForCallbackAsync(TcpListener listener, string state)
    {
        Task<string?> acceptTask = Task.Run(() =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    return null; // listener 已停（超时/异常路径）
                }
                using (client)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        string? target = ReadRequestTarget(stream);
                        var query = ParseQuery(target);
                        if (query.ContainsKey("error"))
                        {
                            Respond(stream, 400, "Bad Request", "<html><body><h3>授权被拒绝</h3>可以关闭此页面。</body></html>");
                            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                                $"nextmoe 授权失败: {query.GetValueOrDefault("error")}");
                            return null;
                        }
                        if (query.TryGetValue("state", out string? gotState) && gotState == state &&
                            query.TryGetValue("code", out string? code))
                        {
                            Respond(stream, 200, "OK", "<html><body><h3>登录成功</h3>可以关闭此页面，回到 PotatoVN。</body></html>");
                            return code;
                        }
                        // state 不匹配 / favicon 等杂项连接 → 回 404 继续等
                        Respond(stream, 404, "Not Found", "<html><body>not found</body></html>");
                    }
                    catch
                    {
                        // 单连接失败不影响整体
                    }
                }
            }
        });

        Task finished = await Task.WhenAny(acceptTask, Task.Delay(AuthorizeTimeout));
        if (finished != acceptTask)
        {
            listener.Stop(); // 让阻塞的 AcceptTcpClient 抛异常退出
            return null;
        }
        return await acceptTask;
    }

    private static string? ReadRequestTarget(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
        string? line = reader.ReadLine(); // "GET /callback?code=… HTTP/1.1"
        if (string.IsNullOrWhiteSpace(line)) return null;
        string[] parts = line.Split(' ');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static void Respond(NetworkStream stream, int status, string statusText, string html)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(html);
            string head = $"HTTP/1.1 {status} {statusText}\r\n" +
                          "Content-Type: text/html; charset=utf-8\r\n" +
                          $"Content-Length: {body.Length}\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
        catch
        {
            // 浏览器可能已断开
        }
    }

    private static Dictionary<string, string> ParseQuery(string? target)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(target)) return result;
        int q = target.IndexOf('?');
        if (q < 0) return result;
        foreach (string pair in target[(q + 1)..].Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = Uri.UnescapeDataString(pair[..eq].Replace("+", " "));
            string value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace("+", " "));
            result[key] = value;
        }
        return result;
    }

    internal static string Base64Url(byte[] bytes)
    {
        string b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// NextMoe 令牌的加密落盘（DPAPI CurrentUser，crypt32 P/Invoke——不引额外包）。
/// 文件位于宿主 LocalDataPath\LibraryPlus\nextmoe_token.bin；**绝不**进 PluginData 导出 JSON。
/// access token（15 分钟 JWT）由 <see cref="NextMoeSession"/> 留内存，这里只管 refresh token。
/// </summary>
internal static class NextMoeTokenStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private const string FileName = "nextmoe_token.bin";

    private static string? FilePath()
    {
        string? baseDir = HostServices.GetHostLocalDataPath();
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        return Path.Combine(baseDir, "LibraryPlus", FileName);
    }

    public static void SaveRefreshToken(string token)
    {
        string? path = FilePath();
        if (path == null) throw new InvalidOperationException("拿不到宿主 LocalDataPath，无法保存令牌");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] plain = Encoding.UTF8.GetBytes(token);
        var inBlob = Alloc(plain);
        try
        {
            if (!CryptProtectData(ref inBlob, "LibraryPlus NextMoe refresh token", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out DATA_BLOB outBlob))
                throw new InvalidOperationException($"DPAPI 加密失败 (win32={Marshal.GetLastWin32Error()})");
            try
            {
                byte[] protected_ = ReadBlob(inBlob: outBlob);
                File.WriteAllBytes(path, protected_);
            }
            finally
            {
                Free(ref outBlob);
            }
        }
        finally
        {
            Free(ref inBlob);
        }
    }

    public static string? LoadRefreshToken()
    {
        string? path = FilePath();
        if (path == null || !File.Exists(path)) return null;
        try
        {
            byte[] data = File.ReadAllBytes(path);
            var inBlob = Alloc(data);
            try
            {
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out DATA_BLOB outBlob))
                    return null; // 解密失败（换用户/损坏）→ 视为未登录
                try
                {
                    return Encoding.UTF8.GetString(ReadBlob(inBlob: outBlob));
                }
                finally
                {
                    Free(ref outBlob);
                }
            }
            finally
            {
                Free(ref inBlob);
            }
        }
        catch
        {
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            string? path = FilePath();
            if (path != null && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删除失败不影响登出主流程
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public IntPtr cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    private static DATA_BLOB Alloc(byte[] data)
    {
        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = (IntPtr)data.Length, pbData = ptr };
    }

    private static byte[] ReadBlob(DATA_BLOB inBlob)
    {
        byte[] result = new byte[(int)inBlob.cbData];
        Marshal.Copy(inBlob.pbData, result, 0, result.Length);
        return result;
    }

    private static void Free(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
        blob.pbData = IntPtr.Zero;
        blob.cbData = IntPtr.Zero;
    }
}

/// <summary>
/// 会话管理：内存缓存 access token（15 分钟），过期或 401 时用 refresh token 刷新并**覆盖**落盘（每次刷新都轮换）。
/// NextMoeClient.InstanceCredentialProvider 接到 <see cref="GetAccessTokenAsync"/> 即完成接线。
/// </summary>
internal class NextMoeSession
{
    private static NextMoeSession? _instance;
    public static NextMoeSession Instance => _instance ??= new NextMoeSession();

    private string? _accessToken;
    private DateTime _expiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsLoggedIn => NextMoeTokenStore.LoadRefreshToken() != null;

    public async Task<string?> GetAccessTokenAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _expiry) return _accessToken;
            string? refreshToken = NextMoeTokenStore.LoadRefreshToken();
            if (refreshToken == null) return null;
            NextMoeTokenResponse? token = await NextMoeAuth.RefreshAsync(refreshToken);
            if (token?.AccessToken == null || string.IsNullOrEmpty(token.RefreshToken))
            {
                // refresh 失效（撤销/过期）→ 清掉本地凭据，让用户重新登录
                NextMoeTokenStore.Delete();
                _accessToken = null;
                return null;
            }
            _accessToken = token.AccessToken;
            _expiry = DateTime.UtcNow.AddSeconds(Math.Max(token.ExpiresIn ?? 900, 60) - 30);
            NextMoeTokenStore.SaveRefreshToken(token.RefreshToken); // 轮换：覆盖，不是追加
            return _accessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>完整登录（起浏览器授权）。成功返回 true。</summary>
    public async Task<bool> SignInAsync()
    {
        NextMoeTokenResponse? token = await NextMoeAuth.AuthorizeAsync();
        if (token?.AccessToken == null || string.IsNullOrEmpty(token.RefreshToken)) return false;
        await _lock.WaitAsync();
        try
        {
            _accessToken = token.AccessToken;
            _expiry = DateTime.UtcNow.AddSeconds(Math.Max(token.ExpiresIn ?? 900, 60) - 30);
            NextMoeTokenStore.SaveRefreshToken(token.RefreshToken);
            NextMoeClient.ResetCredentialNotice();
        }
        finally
        {
            _lock.Release();
        }
        return true;
    }

    public void SignOut()
    {
        NextMoeTokenStore.Delete();
        _accessToken = null;
        _expiry = DateTime.MinValue;
    }
}
