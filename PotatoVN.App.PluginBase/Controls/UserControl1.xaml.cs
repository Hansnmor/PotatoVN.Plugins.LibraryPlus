using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Models;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.
namespace PotatoVN.App.PluginBase.Controls
{
    public sealed partial class UserControl1 : UserControl
    {
        private PluginData _data;
        
        public UserControl1(PluginData data)
        {
            XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
            _data = data;
        }

        private void ButtonBase_OnClick(object sender, RoutedEventArgs e)
        {
            Button.Content = $"Button clicked at {DateTime.Now.ToShortTimeString()}!";
        }
    }
}
