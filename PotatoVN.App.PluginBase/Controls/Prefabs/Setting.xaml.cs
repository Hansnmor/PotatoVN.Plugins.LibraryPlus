using System;
using Microsoft.UI.Xaml;

namespace PotatoVN.App.PluginBase.Controls.Prefabs
{
    public sealed partial class Setting
    {
        public Setting() => XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
        
        public new static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
            nameof(Content), typeof(UIElement), typeof(Setting),
            new PropertyMetadata(null, OnContentChanged));
        public new UIElement Content
        {
            get => (UIElement)GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }
        
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title),
            typeof(string), typeof(Setting), new PropertyMetadata(null));
        
        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }
        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description),
            typeof(string), typeof(Setting), new PropertyMetadata(null));
        

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Setting setting)
            {
                setting.ContentArea.Content = e.NewValue;
            }
        }

        private void Setting_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            double calculatedWidth = ActualWidth - Content.ActualSize.X - 40;
            // 文件夹名称过长时可能为负值，避免设置为负值
            DescriptionTextBlock.MaxWidth = Math.Max(0, calculatedWidth);
        }
    }
}