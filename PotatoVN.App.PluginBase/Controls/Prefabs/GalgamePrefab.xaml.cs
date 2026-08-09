using System;
using GalgameManager.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace PotatoVN.App.PluginBase.Controls.Prefabs;

public sealed partial class GalgamePrefab
{
    public static readonly DependencyProperty ImageStretchProperty = DependencyProperty.Register(
        nameof(ImageStretch), typeof(Stretch), typeof(GalgamePrefab),
        new PropertyMetadata(Stretch.UniformToFill));

    public Stretch ImageStretch
    {
        get => (Stretch)GetValue(ImageStretchProperty);
        set => SetValue(ImageStretchProperty, value);
    }

    public static readonly DependencyProperty GalgameProperty = DependencyProperty.Register(
        nameof(Galgame), typeof(Galgame), typeof(GalgamePrefab),
        new PropertyMetadata(null));

    public Galgame? Galgame
    {
        get => (Galgame?)GetValue(GalgameProperty);
        set => SetValue(GalgameProperty, value);
    }

    public static readonly DependencyProperty PlayTypeVisibilityProperty = DependencyProperty.Register(
        nameof(PlayTypeVisibility), typeof(Visibility), typeof(GalgamePrefab),
        new PropertyMetadata(Visibility.Collapsed));

    public Visibility PlayTypeVisibility
    {
        get => (Visibility)GetValue(PlayTypeVisibilityProperty);
        set => SetValue(PlayTypeVisibilityProperty, value);
    }

    public static readonly DependencyProperty SourceVisibilityProperty = DependencyProperty.Register(
        nameof(SourceVisibility), typeof(Visibility), typeof(GalgamePrefab),
        new PropertyMetadata(Visibility.Collapsed));

    public Visibility SourceVisibility
    {
        get => (Visibility)GetValue(SourceVisibilityProperty);
        set => SetValue(SourceVisibilityProperty, value);
    }

    public static readonly DependencyProperty FlyoutProperty = DependencyProperty.Register(
        nameof(Flyout), typeof(FlyoutBase), typeof(GalgamePrefab),
        new PropertyMetadata(null));

    public FlyoutBase? Flyout
    {
        get => (FlyoutBase?)GetValue(FlyoutProperty);
        set => SetValue(FlyoutProperty, value);
    }

    public static readonly DependencyProperty ItemScaleProperty = DependencyProperty.Register(
        nameof(ItemScale), typeof(double), typeof(GalgamePrefab),
        new PropertyMetadata(1.0, OnItemScaleChanged));

    public double ItemScale
    {
        get => (double)GetValue(ItemScaleProperty);
        set => SetValue(ItemScaleProperty, value);
    }

    public static readonly DependencyProperty TextHeightProperty = DependencyProperty.Register(
        nameof(TextHeight), typeof(double), typeof(GalgamePrefab),
        new PropertyMetadata(80.0));

    public double TextHeight
    {
        get => (double)GetValue(TextHeightProperty);
        set => SetValue(TextHeightProperty, value);
    }

    public static readonly DependencyProperty NameVisibilityProperty = DependencyProperty.Register(
        nameof(NameVisibility), typeof(Visibility), typeof(GalgamePrefab),
        new PropertyMetadata(Visibility.Visible));

    public Visibility NameVisibility
    {
        get => (Visibility)GetValue(NameVisibilityProperty);
        set => SetValue(NameVisibilityProperty, value);
    }

    public double MediumFontSize = 10.0;
    private Visibility _nameVisibility = Visibility.Visible;

    public GalgamePrefab()
    {
        if (Application.Current.Resources["MediumFontSize"] is double mediumFontSize)
            MediumFontSize = mediumFontSize;
        Loaded += (_, _) =>
        {
            _nameVisibility = NameVisibility;
            NameTextBlock.Visibility = _nameVisibility;
            MinHeight = CalcPrefabHeight(300);
        };
        XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
    }

    private static void OnItemScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GalgamePrefab prefab && e.NewValue is double newValue && newValue <= 0)
            prefab.ItemScale = 1.0;
    }

    public double CalcValue(double value) => value * ItemScale;

    public double CalcPrefabHeight(double originalHeight)
    {
        var height = originalHeight;
        if (_nameVisibility == Visibility.Collapsed)
            height -= TextHeight - 20;
        return Math.Max(height, 0) * ItemScale;
    }
}
