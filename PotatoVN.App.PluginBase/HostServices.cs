using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts;

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

    private static Type ServiceType() => Service.GetType();

    /// <summary>宿主 GameParseType 枚举的 All 常量（int.MaxValue）</summary>
    private const int GameParseTypeAll = int.MaxValue;
}
