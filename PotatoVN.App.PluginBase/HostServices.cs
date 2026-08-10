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

        MethodInfo? getService = appType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static);
        object? service = getService?.MakeGenericMethod(serviceType).Invoke(null, null)
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

    private static Type ServiceType() => Service.GetType();

    /// <summary>宿主 GameParseType 枚举的 All 常量（int.MaxValue）</summary>
    private const int GameParseTypeAll = int.MaxValue;
}
