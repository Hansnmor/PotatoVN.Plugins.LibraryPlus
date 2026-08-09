using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PotatoVN.App.PluginBase.Controls.Prefabs;

public sealed class StdAccountPanel : UserControl
{
    public string Title
    {
        get => _titleText.Text;
        set => _titleText.Text = value;
    }
    public string UserName
    {
        get => _userNameText.Text;
        set => _userNameText.Text = value;
    }
    public string Description
    {
        get => _descText.Text;
        set => _descText.Text = value;
    }
    public string? Avatar
    {
        get => _avatarPath;
        set { _avatarPath = value; UpdateAvatar(); }
    }
    public string DefaultAvatar
    {
        get => _defaultAvatarPath;
        set { _defaultAvatarPath = value; UpdateAvatar(); }
    }
    public bool Expand
    {
        get => _expander.IsExpanded;
        set => _expander.IsExpanded = value;
    }

    private readonly Expander _expander;
    private readonly ImageBrush _avatarBrush; // 边框背景
    private readonly TextBlock _titleText;
    private readonly TextBlock _userNameText;
    private readonly TextBlock _descText;

    private string? _avatarPath;
    private string _defaultAvatarPath = "ms-appx:///Assets/Pictures/Akkarin.webp";


    public StdAccountPanel(string title, string userName, string description, FrameworkElement accountContent,
        string? avatar = null, bool expand = false, string? defaultAvatar = null)
    {
        // ===== Header（头像 + 文本） =====
        // 外层 Expander
        _expander = new Expander
        {
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = expand
        };
        // Header Grid
        var headerGrid = new Grid { Padding = new Thickness(0, 18, 0, 18) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // 头像
        _avatarBrush = new ImageBrush();
        var avatarBorder = new Border
        {
            Width = 80,
            Height = 80,
            CornerRadius = new CornerRadius(15),
            Background = _avatarBrush,
        };
        _avatarPath = avatar;
        if (defaultAvatar != null) _defaultAvatarPath = defaultAvatar;
        headerGrid.Children.Add(avatarBorder);
        Grid.SetColumn(avatarBorder, 0);
        // 文本堆栈
        _titleText = new TextBlock { Text = title, FontSize = 18 };
        _userNameText = new TextBlock { Text = userName, Margin = new Thickness(0, 0, 0, 5) };
        _descText = new TextBlock { Text = description, FontSize = 12, Opacity = 0.6 };

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(10, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        textStack.Children.Add(_titleText);
        textStack.Children.Add(_userNameText);
        textStack.Children.Add(_descText);

        headerGrid.Children.Add(textStack);
        Grid.SetColumn(textStack, 1);

        _expander.Header = headerGrid;
        _expander.Content = accountContent;

        Content = _expander;
        UpdateAvatar();
    }

    private void UpdateAvatar()
    {
        var uri = ResolveImageUri(_avatarPath) ?? ResolveImageUri(_defaultAvatarPath);
        _avatarBrush.ImageSource = uri != null ? new BitmapImage(uri) : null;
    }

    private Uri? ResolveImageUri(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            // 已经是绝对 URI（ms-appx, ms-appdata, file, http…）
            if (Uri.TryCreate(path, UriKind.Absolute, out var abs)) return abs;
            if (Path.IsPathRooted(path)) // 尝试当作绝对文件路径
                return new Uri(path, UriKind.Absolute);
            return null;
        }
        catch
        {
            return null;
        }
    }
}