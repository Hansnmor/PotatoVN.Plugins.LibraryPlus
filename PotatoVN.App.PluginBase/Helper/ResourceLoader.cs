using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 插件ResourceDictionary加载器，负责加载插件的XAML资源字典，并在插件卸载时清理它们。
/// 如果你希望新增ResourceDictionary的xaml，请务必在Initialize中调用Add方法加载它
/// </summary>
internal static class ResourceLoader
{
    private static readonly List<ResourceDictionary> Dictionaries = [];
    private static bool _loaded;

    public static void Initialize()
    {
        if (_loaded) return;
        Plugin.HostApi.InvokeOnMainThread(() =>
        {
            Add("Controls/Styles/FontSizes.xaml");
            Add("Controls/Styles/TextBlock.xaml");
            Add("Controls/Styles/Thickness.xaml");
        });
        _loaded = true;
    }

    private static void Add(string relativePath)
    {
        var dictionary = new ResourceDictionary
        {
            Source = XamlResourceLocatorFactory.CreateFromRelativePath(relativePath)
        };
        Application.Current.Resources.MergedDictionaries.Add(dictionary);
        Dictionaries.Add(dictionary);
    }

    public static void Unload()
    {
        Plugin.HostApi.InvokeOnMainThread(() =>
        {
            foreach (var dictionary in Dictionaries)
                Application.Current.Resources.MergedDictionaries.Remove(dictionary);
        });
        Dictionaries.Clear();
        _loaded = false;
    }
}