namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 插件本地化帮助类，如果你的插件不需要支持多语言，可以不使用这个类，直接在代码里写死字符串即可。
/// </summary>
public static class LocalizationExtensions
{
    public static string GetLoc(this string resourceKey, string fallback = "")
    {
        return PluginLocalization.GetStringOr(resourceKey, fallback);
    }
    
    public static string GetLocFormat(this string resourceKey, string fallback, params object[] args)
    {
        return PluginLocalization.GetStringOrFormat(resourceKey, fallback, args);
    }
}