using System;
using System.Collections.Generic;
using System.Linq;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.Models.Sources;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace PotatoVN.App.PluginBase.Controls.Converters;

public class ImagePathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is LockableProperty<string?> lo) value = lo.Value!;
        try
        {
            if ((value is string path && (string.IsNullOrEmpty(path) || path == Galgame.DefaultImagePath) || value is null)
                && parameter is "null_ignoreDefaultPotato")
                return new BitmapImage();
            if (value is string str && !string.IsNullOrEmpty(str))
                return new BitmapImage(new Uri(str));
            if (parameter is string para)
                return para == "null" ? new BitmapImage() : new BitmapImage(new Uri(para));
        }
        catch (Exception)
        {
            // ignored
        }
        return new BitmapImage(new Uri(Galgame.DefaultImagePath));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => string.Empty;
}

public class PlayTypeToSolidColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = value is PlayType playType ? ToColor(playType) : ToColor(PlayType.None);
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => PlayType.None;

    private static Color ToColor(PlayType playType)
    {
        return playType switch
        {
            PlayType.WantToPlay => Colors.Pink,
            PlayType.Played => Colors.LimeGreen,
            PlayType.Playing => Colors.Blue,
            PlayType.Shelved => Colors.Orange,
            PlayType.Abandoned => Colors.IndianRed,
            _ => Colors.Gray,
        };
    }
}

public class GameToOpacityConverter : IValueConverter
{
    public static bool SpecialDisplayVirtualGame;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galgame game)
            return SpecialDisplayVirtualGame && !game.IsLocalGame ? 0.5 : 1;
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => true;
}

internal class SourceToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not GalgameSourceBase source) return "\uE897";
        return source.SourceType switch
        {
            GalgameSourceType.LocalFolder => "\uE8B7",
            GalgameSourceType.LocalZip => "\uF012",
            GalgameSourceType.Virtual => "\ue8ff",
            GalgameSourceType.Steam => "\uE7FC",
            _ => "\uE897",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => GalgameSourceType.UnKnown;
}

public class SourcesToStringConverter : IValueConverter
{
    private readonly SourceToGlyphConverter _sourceToGlyphConverter = new();

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not IEnumerable<GalgameSourceBase> sources) return string.Empty;
        var glyphs = sources.Select(s =>
            _sourceToGlyphConverter.Convert(s, targetType, parameter, language) as string ?? string.Empty);
        return string.Join(" ", glyphs);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => null!;
}
