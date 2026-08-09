using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.Controls.Prefabs
{
    public sealed partial class Panel: UserControl
    {
        public Panel() =>  XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);

        public static readonly new DependencyProperty ContentProperty = DependencyProperty.Register(
            nameof(Content),
            typeof(UIElement),
            typeof(Panel),
            new PropertyMetadata(null, OnContentChanged));

        public new UIElement Content
        {
            get => (UIElement)GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Panel panel)
            {
                panel.ContentArea.Content = e.NewValue;
            }
        }
    }
}