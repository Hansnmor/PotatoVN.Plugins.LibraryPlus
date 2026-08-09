using System;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase;

/// <summary>
/// 控制宿主侧边栏（NavigationView）的选中项。
///
/// 宿主 NavigationViewService.GetSelectedItem 只匹配设置了 NavigateToProperty 的内置项，
/// 插件按钮没有该属性，因此导航到插件页/详情页时宿主不会自动移动选中指示器（蓝色小条）。
/// 本类通过反射直接设置 NavigationView.SelectedItem，让蓝条跟随页面移动：
/// 进详情 → 移动到「游戏」项；返回插件页 → 移回「更多排序」项。
/// 目标均为宿主 x:Name 生成的字段 / 公开属性，跨小版本稳定。
/// </summary>
internal static class SidebarSelectionHelper
{
    /// <summary>把选中指示器移动到「更多排序」插件按钮</summary>
    public static void SelectPluginButton(string buttonId)
    {
        try
        {
            if (Plugin.HostApi.GetMainWindow()?.Content is not FrameworkElement root) return;
            if (GetNavigationView(root) is not { } navView) return;

            string uniqueId = CreatePluginButtonId(buttonId);
            foreach (object item in navView.MenuItems)
            {
                if (item is NavigationViewItem navItem && IsPluginItem(navItem, uniqueId, buttonId))
                {
                    navView.SelectedItem = navItem;
                    return;
                }
            }
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>
    /// 匹配插件侧边栏项：优先精确匹配宿主生成的 UniqueId（<c>plugin:{guid:N}:{buttonId}</c>），
    /// 再按后缀 <c>:{buttonId}</c> 兜底，避免宿主 UniqueId 格式变化导致匹配失败。
    /// </summary>
    private static bool IsPluginItem(NavigationViewItem navItem, string uniqueId, string buttonId)
    {
        if (navItem.Tag is not string tag) return false;
        if (tag == uniqueId) return true;
        return tag.EndsWith($":{buttonId}", StringComparison.Ordinal);
    }

    /// <summary>清除侧边栏选中项（详情页不显示蓝条，对齐原生行为）</summary>
    public static void ClearSelection()
    {
        try
        {
            if (Plugin.HostApi.GetMainWindow()?.Content is not FrameworkElement root) return;
            if (GetNavigationView(root) is { } navView) navView.SelectedItem = null;
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>把选中指示器移动到内置「游戏」（主页）导航项</summary>
    public static void SelectHome()
    {
        try
        {
            if (Plugin.HostApi.GetMainWindow()?.Content is not FrameworkElement root) return;
            if (GetNavigationView(root) is not { } navView) return;

            FieldInfo? homeField = root.GetType()
                .GetField("HomeNavItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (homeField?.GetValue(root) is NavigationViewItem homeItem)
                navView.SelectedItem = homeItem;
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>
    /// 构造宿主侧边栏插件的 UniqueId（Tag）：<c>plugin:{pluginId:N}:{buttonId}</c>。
    /// 优先反射调用宿主 SidebarButtonIds.CreatePluginButtonId，失败则按同格式兜底。
    /// </summary>
    private static string CreatePluginButtonId(string buttonId)
    {
        try
        {
            Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "GalgameManager");
            Type? ids = host?.GetType("GalgameManager.Models.SidebarButtonIds");
            MethodInfo? create = ids?.GetMethod("CreatePluginButtonId", BindingFlags.Public | BindingFlags.Static);
            if (create is not null)
                return (string)create.Invoke(null, new object[] { Plugin.StaticInfo.Id, buttonId })!;
        }
        catch
        {
            // 走兜底
        }
        return $"plugin:{Plugin.StaticInfo.Id:N}:{buttonId}";
    }

    private static NavigationView? GetNavigationView(FrameworkElement shellPage)
    {
        FieldInfo? field = shellPage.GetType()
            .GetField("NavigationViewControl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(shellPage) as NavigationView;
    }
}
