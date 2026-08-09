using System.Threading.Tasks;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Controls;

namespace PotatoVN.App.PluginBase;

public partial class Plugin : IGalgamePageLeftPanel, IGalgamePageRightPanel
{
    private bool _uiInit;
    
    private void InitUi()
    {
        if (_uiInit) return;
        _hostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
           Id = "sidebarButton1",
           Text = "插件按钮",
           Placement = SidebarButtonPlacement.Menu, 
           FluentGlyph = "&#xE709;",
        }, () =>
        {
            _hostApi.NavigateTo(typeof(ExamplePage), "Example Page");
            return Task.CompletedTask;
        });
        _uiInit = true;
    }

    public FrameworkElement CreateSettingUi() => new UserControl1(_data);
    
    public async Task<FrameworkElement> CreateLeftPanelUiAsync(Galgame game)
    {
        await Task.CompletedTask;
        return new TextBlock { Text = $"这是左侧面板，当前游戏：{game.Name.Value}" };
    }

    public Task<FrameworkElement> CreateRightPanelUiAsync(Galgame game)
    {
        return Task.FromResult<FrameworkElement>(new TextBlock { Text = $"这是右侧面板，当前游戏：{game.Name.Value}" });
    }
}