using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase;

/// <summary>
/// 反射调用宿主内部服务，补齐插件 API 未覆盖的游戏操作（保存/删除/搜刮信息）。
/// 宿主程序集 GalgameManager 不在插件的编译期引用内，因此全部通过 AppDomain 反射调用；
/// 目标成员均为宿主公开成员，跨小版本稳定。
/// </summary>
internal static class HostServices
{
    private const string HostAssemblyName = "GalgameManager";
    private const string ServiceTypeName = "GalgameManager.Services.GalgameCollectionService";
    private const string ServiceInterfaceName = "GalgameManager.Contracts.Services.IGalgameCollectionService";
    private const string PvnExceptionTypeName = "GalgameManager.Models.PvnException";

    private static object? _service;
    private static MethodInfo? _saveGame, _removeGame, _parseInfo;

    private static object Service => _service ??= CreateService();

    private static object CreateService()
    {
        Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == HostAssemblyName)
            ?? throw new InvalidOperationException("Host assembly not loaded");
        Type? appType = host.GetType("GalgameManager.App");
        Type? serviceType = host.GetType(ServiceTypeName);
        if (appType is null || serviceType is null)
            throw new InvalidOperationException("Host service type not found");

        // 注意：App.GetService<T>() 的泛型参数必须是 DI 容器注册的类型（宿主注册的是接口
        // IGalgameCollectionService，不是具体类）——传具体类会抛 "needs to be registered"。
        // 实例用接口解析；方法反射仍从具体类拿（运行时实例类型匹配，可正常 Invoke）。
        MethodInfo? getService = appType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static);
        Type? serviceInterface = host.GetType(ServiceInterfaceName)
            ?? throw new InvalidOperationException("Host service interface not found");
        object? service = getService?.MakeGenericMethod(serviceInterface).Invoke(null, null)
            ?? throw new InvalidOperationException("Cannot resolve host service");
        _saveGame = serviceType.GetMethod("SaveGalgameAsync");
        _removeGame = serviceType.GetMethod("RemoveGalgame");
        _parseInfo = serviceType.GetMethod("ParseGalInfoAsync");
        return service;
    }

    /// <summary>持久化游戏改动（如修改游玩状态后调用）</summary>
    public static async Task SaveGameAsync(Galgame game)
    {
        MethodInfo? m = _saveGame ?? ServiceType().GetMethod("SaveGalgameAsync");
        await (Task)m.Invoke(Service, new object[] { game })!;
    }

    /// <summary>从游戏库删除游戏（不删除磁盘文件）</summary>
    public static async Task RemoveGameAsync(Galgame game)
    {
        MethodInfo? m = _removeGame ?? ServiceType().GetMethod("RemoveGalgame");
        await (Task)m.Invoke(Service, new object[] { game, false })!;
    }

    /// <summary>搜刮游戏信息（使用游戏当前的 RssType，不弹出确认框）</summary>
    public static async Task ParseGalInfoAsync(Galgame game)
    {
        MethodInfo? m = _parseInfo ?? ServiceType().GetMethod("ParseGalInfoAsync");
        // GameParseType 枚举位于宿主程序集，通过 Enum.ToObject 反射构造 All（int.MaxValue）
        object parseType = Enum.ToObject(
            ServiceType().Assembly.GetType("GalgameManager.Enums.GameParseType")!, GameParseTypeAll);
        await (Task)m.Invoke(Service, new object[] { game, GalgameManager.Enums.RssType.None, false, parseType })!;
    }

    /// <summary>
    /// 清除宿主「上次运行崩溃」的已知历史残留（KeyValues.LastError）。
    /// 宿主 App_UnhandledException 把未处理异常累加存进 lastError 且从不清理，
    /// 导致每次从托盘恢复窗口都弹"上次运行崩溃了"（内容是历史旧崩溃）。
    /// 仅当 lastError 包含已知的历史残留特征（热重载缺陷的 GalgamePrefab cast、
    /// 旧版 DispatcherQueue 崩溃）时才清除——避免无差别清掉未来真实的崩溃报告
    ///（宿主崩溃→写入→自动重启→插件初始化清除 的链条会掩盖新崩溃）。失败时静默。
    /// </summary>
    public static void ClearLastError()
    {
        try
        {
            Plugin.HostApi.InvokeOnMainThread(() =>
            {
                try
                {
                    Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == HostAssemblyName);
                    if (host is null) return;
                    Type? appType = host.GetType("GalgameManager.App");
                    Type? settingsService = host.GetType("GalgameManager.Contracts.Services.ILocalSettingsService");
                    if (appType is null || settingsService is null) return;

                    MethodInfo? getService = appType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static);
                    object? svc = getService?.MakeGenericMethod(settingsService).Invoke(null, null);
                    if (svc is null) return;

                    MethodInfo? read = settingsService.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "ReadSettingAsync" && m.IsGenericMethodDefinition)
                        ?.MakeGenericMethod(typeof(string));
                    MethodInfo? save = settingsService.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "SaveSettingAsync" && m.IsGenericMethodDefinition)
                        ?.MakeGenericMethod(typeof(string));
                    if (read is null || save is null) return;

                    // 反射 Invoke 必须传全部参数（默认值不自动应用）：
                    // ReadSettingAsync<T>(key, isLarge=false, converters=null, typeNameHandling=false)
                    var readTask = (Task<string?>)read.Invoke(svc,
                        new object[] { "lastError", false, null, false })!;
                    string? error = readTask.GetAwaiter().GetResult();
                    Plugin.HostApi.Log(InfoBarSeverity.Informational, $"ClearLastError: read='{error?[..Math.Min(60, error.Length)]}'");

                    if (error is null) return;
                    // 已知的历史残留特征（均为宿主缺陷产生的噪音，非插件真实 bug）：
                    // 1) 热重载 GalgamePrefab cast 错误（新旧 PluginLoadContext 双加载）
                    // 2) 旧版 DispatcherQueue 崩溃（宿主旧 WinUI 无该 API）
                    // 3) XamlParseException（点×进托盘=AppInstance.Restart 重启，新进程删热重载目录后，
                    //    任务栏旧实例激活加载插件 XAML 资源失效——宿主多进程竞态）
                    if (!error.Contains("GalgamePrefab cannot be cast", StringComparison.Ordinal)
                        && !error.Contains("get_DispatcherQueue", StringComparison.Ordinal)
                        && !error.Contains("XamlParseException", StringComparison.Ordinal))
                        return; // 非已知噪音 → 保留，让宿主正常提示

                    // SaveSettingAsync<T>(key, value, isLarge=false, triggerEventWhenNull=false, converters=null, typeNameHandling=false)
                    _ = save.Invoke(svc,
                        new object[] { "lastError", (string?)null, false, false, null, false });
                    Plugin.HostApi.Log(InfoBarSeverity.Informational, "ClearLastError: cleared");
                }
                catch (Exception ex)
                {
                    // 记录失败原因（便于排查），不影响插件功能
                    Plugin.HostApi.Log(InfoBarSeverity.Warning, $"ClearLastError failed: {ex.Message}");
                }
            });
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>
    /// 反射读取宿主 Bangumi OAuth token（BgmAccount.BangumiAccessToken，设置键 bangumiAccount）。
    /// 用于插件调 Bangumi API 拉 tag 投票数据（匿名 API 对部分条目 404，登录后才全量可见）。
    /// 未登录/读取失败返回 null，调用方跳过 Bangumi 采集（不影响 kungal 功能）。
    /// </summary>
    public static async Task<string?> GetBgmTokenAsync()
    {
        try
        {
            Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HostAssemblyName);
            if (host is null) return null;
            Type? appType = host.GetType("GalgameManager.App");
            Type? settingsService = host.GetType("GalgameManager.Contracts.Services.ILocalSettingsService");
            Type? bgmAccount = host.GetType("GalgameManager.Models.BgmAccount");
            if (appType is null || settingsService is null || bgmAccount is null) return null;

            MethodInfo? getService = appType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static);
            object? svc = getService?.MakeGenericMethod(settingsService).Invoke(null, null);
            if (svc is null) return null;

            MethodInfo? read = settingsService.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ReadSettingAsync" && m.IsGenericMethodDefinition)
                ?.MakeGenericMethod(bgmAccount);
            if (read is null) return null;
            // ReadSettingAsync<T>(key, isLarge=false, converters=null, typeNameHandling=false)
            var task = (Task)read.Invoke(svc, new object[] { "bangumiAccount", false, null, false })!;
            await task.ConfigureAwait(false);
            object? account = task.GetType().GetProperty("Result")?.GetValue(task);
            if (account is null) return null;
            // BangumiAccessToken 是公开字段
            FieldInfo? tokenField = bgmAccount.GetField("BangumiAccessToken");
            string? token = tokenField?.GetValue(account) as string;
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch
        {
            return null; // 未登录/反射失败 → 跳过 Bangumi 采集
        }
    }

    /// <summary>
    /// 下载角色图片（反射宿主 GalgameManager.Helpers.DownloadHelper.DownloadAndSaveImageWithDiffThread，
    /// 保存到宿主数据目录 images 文件夹）。补齐角色的 PreviewImageUrl/ImageUrl 是内存字段
    /// （[JsonIgnore] 不持久化），下载成功后将本地路径写入 PreviewImagePath/ImagePath 随游戏保存。
    /// 失败保持默认图，静默（不影响批量）。
    /// </summary>
    public static async Task DownloadCharacterImagesAsync(GalgameCharacter character)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(character.PreviewImageUrl) &&
                string.IsNullOrWhiteSpace(character.ImageUrl))
                return;
            Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HostAssemblyName);
            Type? helper = host?.GetType("GalgameManager.Helpers.DownloadHelper");
            MethodInfo? m = helper?.GetMethod("DownloadAndSaveImageWithDiffThread",
                BindingFlags.Public | BindingFlags.Static);
            if (m is null) return;
            // 反射 Invoke 必须传全部参数（默认值不自动应用）：
            // (imageUrl, retry=0, fileNameWithoutExtension, onException=null, client=null, targetFolder=null)
            if (!string.IsNullOrWhiteSpace(character.PreviewImageUrl))
            {
                var previewTask = (Task<string?>)m.Invoke(null, new object?[]
                    { character.PreviewImageUrl, 0, $"{character.Name}_Preview", null, null, null })!;
                if (await previewTask is { } preview)
                    character.PreviewImagePath = preview;
            }
            if (!string.IsNullOrWhiteSpace(character.ImageUrl))
            {
                var imageTask = (Task<string?>)m.Invoke(null, new object?[]
                    { character.ImageUrl, 0, $"{character.Name}_Large", null, null, null })!;
                if (await imageTask is { } image)
                    character.ImagePath = image;
            }
        }
        catch
        {
            // 图片下载失败保持默认图，不影响批量
        }
    }

    private static Type ServiceType() => Service.GetType();

    /// <summary>当前已订阅宿主 PhrasedEvent 的处理器（单一，页面重建会覆盖）</summary>
    private static Action? _phrasedSubscriber;

    /// <summary>
    /// 订阅宿主 GalgameCollectionService.PhrasedEvent（搜刮信息完成时触发）。
    /// 用于插件页在搜刮完成后自动刷新列表（与原生页行为对齐）。失败时静默。
    /// </summary>
    public static void SubscribePhrased(Action handler)
    {
        try
        {
            EventInfo? evt = ServiceType().GetEvent("PhrasedEvent");
            if (evt is null) return;
            evt.AddEventHandler(Service, handler);
            _phrasedSubscriber = handler;
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>退订宿主 PhrasedEvent（页面销毁时调用，防事件泄漏）</summary>
    public static void UnsubscribePhrased()
    {
        try
        {
            if (_phrasedSubscriber is null) return;
            EventInfo? evt = ServiceType().GetEvent("PhrasedEvent");
            evt?.RemoveEventHandler(Service, _phrasedSubscriber);
            _phrasedSubscriber = null;
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>
    /// 反射触发宿主 PhrasedEvent（批量搜刮完成时调用）：
    /// 让所有订阅了该事件的页面（主页/详情页/重建后的扩展库页）统一刷新——
    /// 解决"批量进行中切走页面，切回来看到中间状态"的问题。失败时静默。
    /// </summary>
    public static void TriggerPhrased()
    {
        try
        {
            FieldInfo? field = ServiceType().GetField("PhrasedEvent",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field?.GetValue(Service) is not Action handler) return;
            Plugin.HostApi.InvokeOnMainThread(() =>
            {
                try
                {
                    handler();
                }
                catch
                {
                    // 静默：刷新失败不影响批量结果
                }
            });
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>宿主 GameParseType 枚举的 All 常量（int.MaxValue）</summary>
    private const int GameParseTypeAll = int.MaxValue;

    private static Action<GalgameManager.Models.Galgame>? _galAddedSubscriber;

    /// <summary>
    /// 订阅宿主 GalgameCollectionService.GalgameAddedEvent（新游戏入库时触发，签名 Action&lt;Galgame&gt;）。
    /// 启动守卫用它给后续新增的游戏补挂 GalPropertyChanged 监听。失败时静默。
    /// </summary>
    public static void SubscribeGalgameAdded(Action<GalgameManager.Models.Galgame> handler)
    {
        try
        {
            EventInfo? evt = ServiceType().GetEvent("GalgameAddedEvent");
            if (evt is null) return;
            evt.AddEventHandler(Service, handler);
            _galAddedSubscriber = handler;
        }
        catch
        {
            // 静默：新增游戏暂不被守卫观察，重启插件后恢复全覆盖
        }
    }

    /// <summary>退订宿主 GalgameAddedEvent</summary>
    public static void UnsubscribeGalgameAdded()
    {
        try
        {
            if (_galAddedSubscriber is null) return;
            EventInfo? evt = ServiceType().GetEvent("GalgameAddedEvent");
            evt?.RemoveEventHandler(Service, _galAddedSubscriber);
            _galAddedSubscriber = null;
        }
        catch
        {
            // 静默
        }
    }
}
