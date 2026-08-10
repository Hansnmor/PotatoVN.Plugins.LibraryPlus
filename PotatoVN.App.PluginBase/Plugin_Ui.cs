using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Models.Plugin;
using PotatoVN.App.PluginBase.Controls;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private bool _uiInit;

    private void InitUi()
    {
        if (_uiInit) return;
        _hostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
            Id = "libraryPlus",
            Text = "扩展库",
            Placement = SidebarButtonPlacement.Menu,
            FluentGlyph = "&#xE71C;",
        }, () =>
        {
            _hostApi.NavigateTo(typeof(SortPage), "扩展库");
            return Task.CompletedTask;
        });
        _uiInit = true;
    }
}
