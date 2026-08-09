# 插件侧边栏按钮开发指南

如果你的插件需要往软件主界面的侧边栏添加按钮，正确做法是在插件代码里调用 `IPotatoVnApi.RegisterSidebarButton(...)`。
不要直接修改宿主的 `ShellPage.xaml`、`SidebarService` 或 `GalgameManager.WinApp.Base`。

## 推荐接入方式

1. 在 `InitializeAsync(IPotatoVnApi hostApi)` 中保存 `_hostApi`。
2. 在初始化末尾调用一个只执行一次的方法（例如 `InitSidebarButtons()`）注册按钮。
3. 在回调里执行你自己的逻辑，例如弹窗、打开插件 UI、调用 `_hostApi.NavigateTo(...)` 或启动后台任务。
4. 如果按钮需要动态移除，调用 `_hostApi.UnregisterSidebarButton(buttonId)`。

```csharp
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private bool _sidebarInitialized;

    private void InitSidebarButtons()
    {
        if (_sidebarInitialized) return;

        _hostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
            Id = "open-demo",
            Text = "示例",
            Placement = SidebarButtonPlacement.Menu,
            FluentGlyph = "&#xE8A5;",
            FallbackGlyph = "\uE8A5",
        }, () =>
        {
            _hostApi.Info(InfoBarSeverity.Success, "点击了插件按钮");
            return Task.CompletedTask;
        });

        _sidebarInitialized = true;
    }
}
```

在 `InitializeAsync(...)` 中调用 `InitSidebarButtons();` 即可。

## `SidebarButtonInfo` 字段说明

* `Id`：按钮在“当前插件内部”的唯一标识。它必须稳定，不要每次启动都生成随机值，也不要直接使用本地化文本。宿主会把插件 `Info.Id` 和这个 `Id` 组合起来保存按钮显示状态。
* `Text`：按钮标题。宿主当前以单行显示标题，不会换行，所以请尽量保持简短。
* `Placement`：按钮位置。`Menu` 表示主导航区，`Footer` 表示底部区域（通常在设置按钮上方）。
* `FluentGlyph`：使用 Segoe Fluent Icons 时的图标。
* `FallbackGlyph`：Fluent 图标不可用时的回退图标。为了兼容性，建议同时设置 `FluentGlyph` 和 `FallbackGlyph`。

图标字符串可以直接写成 HTML 实体（例如 `&#xE8A5;`），也可以写成 C# 字符串转义（例如 `"\uE8A5"`）。

## 宿主行为

* 用户可以在设置里隐藏插件注册的侧边栏按钮，所以插件不能假设按钮一定可见。
* 重复注册同一个 `Id` 会覆盖旧按钮；适合用来更新标题、图标或点击行为。
* 点击回调会在 UI 线程执行。不要在回调里做长时间阻塞操作；耗时工作请使用异步实现、切后台线程，或通过宿主的后台任务 API 处理。
* 插件按钮不是内置导航项的扩展版。它点击后不会自动切页，也不会保持选中态；如果你想跳转页面，请在回调里显式调用 `_hostApi.NavigateTo(...)`。
* 插件被卸载或禁用时，宿主会自动取消注册该插件的所有侧边栏按钮。只有在你需要运行时动态移除某个按钮时，才需要手动调用 `UnregisterSidebarButton(...)`。
* 调用 `UnregisterSidebarButton(...)` 时，传入的是你注册时使用的原始 `Id`，不是宿主内部拼接后的全局唯一 Id。

## 常见建议

* `Id` 建议使用稳定的 ASCII 标识，例如 `open-demo`、`show-settings`、`sync-now`。
* 如果按钮点击后要弹出 `ContentDialog`，请继续阅读 `doc/dialog.md`。
* 如果按钮点击后要展示自定义控件或设置页，请继续阅读 `doc/ui.md`。
* 脚手架里已经有一个示例实现：`PotatoVN.App.PluginBase/Plugin_Ui.cs`。你可以直接修改它，或者保留其结构后替换内容。
