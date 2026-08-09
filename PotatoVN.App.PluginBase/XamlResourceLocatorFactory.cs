using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace PotatoVN.App.PluginBase
{
    /// <summary>
    /// 这是一个XAML资源定位器工厂类，用于生成插件中XAML资源的URI。
    /// 我们建议开发者不要使用XAML来定义UI，而是使用纯c#代码来定义UI，WinUI3的XAML定位有很多bug
    /// </summary>
    internal static class XamlResourceLocatorFactory
    {
        private static readonly string PackageName;
        public static string PackagePath = string.Empty;

        static XamlResourceLocatorFactory()
        {
            PackageName = typeof(XamlResourceLocatorFactory).Assembly.GetName().Name ?? throw new InvalidOperationException();
        }

        internal static Uri Create([CallerFilePath] string callerFilePath = "")
        {
            // This is not a foolproof solution, but it works well enough to get started
            var i = callerFilePath.LastIndexOf("Stamped", StringComparison.Ordinal) + 8;
            var componentPath = callerFilePath[i..^3];
            return new Uri($"ms-appx:///{PackagePath}\\{PackageName}\\{componentPath}");
        }
        
        internal static Uri CreateFromRelativePath(string relativePath)
        {
            relativePath = relativePath.Replace('/', '\\').TrimStart('\\');
            return new Uri($"ms-appx:///{PackagePath}\\{PackageName}\\{relativePath}");
        }
        
        /// <summary>
        /// 使用它来代替WinUI3自己生成的InitializeComponent()（参考UserControl1）
        /// </summary>
        /// <param name="contentLoaded"></param>
        /// <param name="ctrl"></param>
        /// <param name="callerFilePath"></param>
        internal static void PluginControlInit(ref bool contentLoaded, object ctrl,
            [CallerFilePath] string callerFilePath = "")
        {
            if (contentLoaded) return;
            contentLoaded = true;
            var resourceLocator = Create(callerFilePath);
            Application.LoadComponent(ctrl, resourceLocator, ComponentResourceLocation.Application);
        }
    }
}
