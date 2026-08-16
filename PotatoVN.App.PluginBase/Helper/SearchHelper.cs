using System;
using System.Linq;
using System.Reflection;
using GalgameManager.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 搜索辅助：复用宿主原生搜索逻辑（GalgameManager.Models.GalgameExtension.ApplySearchKey，
/// 匹配 Name / ChineseName / OriginalName / Developer / Tags）。
/// 宿主程序集不在插件编译期引用内 → 运行时反射调用；宿主类型缺失时降级为本地同语义复刻。
/// </summary>
internal static class SearchHelper
{
    private const string HostAssemblyName = "GalgameManager";
    private const string ExtensionTypeName = "GalgameManager.Models.GalgameExtension";
    private const string MethodName = "ApplySearchKey";

    private static MethodInfo? _applySearchKey;

    /// <summary>关键词为空恒 true；否则按宿主原生搜索语义匹配。</summary>
    public static bool ApplySearchKey(Galgame game, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        try
        {
            _applySearchKey ??= AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HostAssemblyName)
                ?.GetType(ExtensionTypeName)
                ?.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Static);
            if (_applySearchKey is not null)
                return (bool)_applySearchKey.Invoke(null, new object[] { game, keyword })!;
        }
        catch
        {
            // 反射失败降级本地复刻
        }
        return FallbackSearch(game, keyword);
    }

    /// <summary>本地复刻宿主语义：名称/中文名/原名/开发商/标签包含关键词（大小写不敏感）。</summary>
    private static bool FallbackSearch(Galgame game, string keyword)
    {
        bool Contain(string? text) => text?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false;
        if (Contain(game.Name?.Value) || Contain(game.ChineseName?.Value) || Contain(game.OriginalName?.Value) ||
            Contain(game.Developer?.Value))
            return true;
        return game.Tags?.Value?.Any(t => Contain(t)) ?? false;
    }
}
