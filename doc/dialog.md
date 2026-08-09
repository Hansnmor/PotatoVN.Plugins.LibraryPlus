# 插件Dialog开发指南
在插件端定义自定义dialog与在普通WinUI应用中定义dialog基本相同，但有一些细节需要注意：
* XamlRoot需要设置为Host窗口的XamlRoot：`XamlRoot = Plugin.HostApi.GetMainWindow()!.Content!.XamlRoot;`
* 建议将ContentDialog的RequestedTheme设置为与Host窗口一致：`RequestedTheme = mainWindow.Content is FrameworkElement element ? element.RequestedTheme : RequestedTheme;`
* 由于宿主设置了dialog的最小宽高，普通的设置`Width`和`Height`属性没有用，请使用类似于下面的写法来设置：
```csharp
Resources["ContentDialogMinWidth"] = 600d;
Resources["ContentDialogMaxWidth"] = 600d;
Resources["ContentDialogMaxHeight"] = 450d;
```

---
以下为一个Dialog示例：
```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls.Prefabs;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Controls;

public sealed class PageSettingDialog : ContentDialog
{
    public bool IsPrimaryButtonClicked { get; private set; }
    private readonly PluginData _data;
    private readonly ToggleSwitch _showDescriptionSwitch;
    private readonly ToggleSwitch _showTagSwitch;
    private readonly ToggleSwitch _showCharacterSwitch;
    private readonly ToggleSwitch _showStaffSwitch;

    private readonly ObservableCollection<PanelItem> _order;

    public PageSettingDialog(PluginData data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));

        // Dialog shell
        var mainWindow = Plugin.HostApi.GetMainWindow();
        XamlRoot = mainWindow!.Content!.XamlRoot;
        RequestedTheme = mainWindow.Content is FrameworkElement element
            ? element.RequestedTheme
            : RequestedTheme;

        Title = L("OldGamePage_SettingsDialog_Title.Text", "详情页设置");
        PrimaryButtonText = L("OldGamePage_SettingsDialog_Save.Text", "保存");
        CloseButtonText = L("OldGamePage_SettingsDialog_Cancel.Text", "取消");
        DefaultButton = ContentDialogButton.Primary;

        _showDescriptionSwitch = new ToggleSwitch { IsOn = _data.ShowDescription, OnContent = L("OldGamePage_Settings_On.Text", "开"), OffContent = L("OldGamePage_Settings_Off.Text", "关") };
        _showTagSwitch = new ToggleSwitch { IsOn = _data.ShowTag, OnContent = L("OldGamePage_Settings_On.Text", "开"), OffContent = L("OldGamePage_Settings_Off.Text", "关") };
        _showCharacterSwitch = new ToggleSwitch { IsOn = _data.ShowCharacter, OnContent = L("OldGamePage_Settings_On.Text", "开"), OffContent = L("OldGamePage_Settings_Off.Text", "关") };
        _showStaffSwitch = new ToggleSwitch { IsOn = _data.ShowStaff, OnContent = L("OldGamePage_Settings_On.Text", "开"), OffContent = L("OldGamePage_Settings_Off.Text", "关") };

        _order = new ObservableCollection<PanelItem>(_data.Order.Select(p =>
            new PanelItem(p, PanelToDisplayName(p))));
        
        Content = BuildContent();

        PrimaryButtonClick += OnPrimaryButtonClick;
        
        Resources["ContentDialogMinWidth"] = 600d;
        Resources["ContentDialogMaxWidth"] = 600d;
        Resources["ContentDialogMaxHeight"] = 450d;
    }

    private UIElement BuildContent()
    {
        var root = new ScrollViewer
        {
            Content = new StdStackPanel
            {
                Children =
                {
                    new TextBlock { Text = L("OldGamePage_SettingsDialog_VisibleSection.Text", "显示") }
                        .WithThemeStyle("SubtitleTextBlockStyle"),
                    new StdSetting(
                        title: L("OldGamePage_Settings_ShowDescription_Title.Text", "简介"),
                        description: L("OldGamePage_Settings_ShowDescription_Desc.Text", "是否显示简介面板"),
                        rightContent: _showDescriptionSwitch),
                    new StdSetting(
                        title: L("OldGamePage_Settings_ShowTag_Title.Text", "标签"),
                        description: L("OldGamePage_Settings_ShowTag_Desc.Text", "是否显示标签面板"),
                        rightContent: _showTagSwitch),
                    new StdSetting(
                        title: L("OldGamePage_Settings_ShowCharacter_Title.Text", "角色"),
                        description: L("OldGamePage_Settings_ShowCharacter_Desc.Text", "是否显示角色面板"),
                        rightContent: _showCharacterSwitch),
                    new StdSetting(
                        title: L("OldGamePage_Settings_ShowStaff_Title.Text", "Staff"),
                        description: L("OldGamePage_Settings_ShowStaff_Desc.Text", "是否显示 Staff 面板"),
                        rightContent: _showStaffSwitch),
                    new TextBlock
                    {
                        Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0),
                        Text = L("OldGamePage_SettingsDialog_OrderSection.Text", "面板顺序（从上到下）")
                    }.WithThemeStyle("SubtitleTextBlockStyle"),
                    new TextBlock
                    {
                        Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 0),
                        Text = L("OldGamePage_SettingsDialog_OrderSectionHint.Text", "拖动来排序")
                    }.WithThemeStyle("DescriptionTextStyle"),
                    new ListView
                    {
                        SelectionMode = ListViewSelectionMode.Single,
                        ItemsSource = _order,
                        DisplayMemberPath = nameof(PanelItem.Name),
                        MaxHeight = 220,
                        CanDragItems = true,
                        CanReorderItems = true,
                        AllowDrop = true,
                    },
                }
            }
        };
        return root;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        IsPrimaryButtonClicked = true;
        _data.ShowDescription = _showDescriptionSwitch.IsOn;
        _data.ShowTag = _showTagSwitch.IsOn;
        _data.ShowCharacter = _showCharacterSwitch.IsOn;
        _data.ShowStaff = _showStaffSwitch.IsOn;
        _data.Order = new List<PanelType>(_order.Select(i => i.Panel));
        _data.Normalize();
    }

    private static string PanelToDisplayName(PanelType panel) => panel switch
    {
        PanelType.Header => L("OldGamePage_Panel_Header.Text", "基本信息"),
        PanelType.Description => L("OldGamePage_Panel_Description.Text", "简介"),
        PanelType.Tag => L("OldGamePage_Panel_Tag.Text", "标签"),
        PanelType.Character => L("OldGamePage_Panel_Character.Text", "角色"),
        PanelType.Staff => L("OldGamePage_Panel_Staff.Text", "Staff"),
        _ => panel.ToString(),
    };

    private static string L(string key, string fallback) => PluginLocalization.GetStringOr(key, fallback);

    private sealed record PanelItem(PanelType Panel, string Name);
}

```