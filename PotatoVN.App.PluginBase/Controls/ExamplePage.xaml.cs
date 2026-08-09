using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.Controls
{
    public sealed partial class ExamplePage : Page
    {
        private Random _random = new Random();
        public ExamplePage() => XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);

        private void ButtonBase_OnClick(object sender, RoutedEventArgs e)
        {
            var games = Plugin.HostApi.GetAllGames();
            if (games.Count == 0)
            {
                Button.Content = "没有找到游戏，请先添加游戏";
                return;
            }
            GalgamePrefab.Visibility = Visibility.Visible;
            GalgamePrefab.Galgame = games[_random.Next(0, games.Count)];
        }
    }
}
