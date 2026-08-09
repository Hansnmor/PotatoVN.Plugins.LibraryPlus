# 插件UI开发文档

PotatoVN为插件预留了丰富的插件UI注入接口。

PotatoVN 支持插件使用XAML定义UI（就像常规的WinUI控件那样），也支持使用c#文件直接描述UI。

## XAML描述UI
如果你计划使用XAML来编写UI，请确保以下几点：

1. 插件中的 `Page`、`UserControl`、自定义控件不要直接调用默认生成的 `InitializeComponent()`；请继续使用模板里提供的 `XamlResourceLocatorFactory.PluginControlInit()`（请参考下面的案例）。
2. 插件项目保持 WinUI 类库配置，并启用 `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`。 (默认模板已启用)
3. 打包插件时要保留生成出来的 `.pri` 文件，以及 `程序集名/...` 这一整套编译后的 XAML 资源目录 （这也是模板默认启用的）。

### 关于 namespace stamping

WinUI 3 的 XAML 加载机制对命名空间比较敏感。如果多个插件都从同一个模板创建，并保留相同的 `PotatoVN.App.PluginBase` namespace，就可能在宿主进程内出现同名 XAML 控件加载冲突。

为避免这个问题，模板在 MSBuild 阶段启用了 namespace stamping：编译前会把插件项目中的 `.cs` 和 `.xaml` 复制到 `obj/Stamped/`，并把 `PotatoVN.App.PluginBase` 替换为本次构建随机生成的 namespace。原始源码不会被修改，最终参与编译的是 `obj/Stamped/` 下的副本。

这带来一个调试限制：调试器看到的运行时代码来自 `obj/Stamped/`，而不是你正在编辑的源文件，因此断点有时无法绑定或无法命中。遇到这种情况时，建议优先使用宿主提供的提示接口输出调试信息，例如：

```csharp
_hostApi.Info(InfoBarSeverity.Informational, msg: $"当前状态：{value}");
```

如果不在 `Plugin` 类中，也可以使用模板里保存的静态引用：

```csharp
Plugin.HostApi.Info(InfoBarSeverity.Informational, msg: "调试信息");
```

注意：不要在业务代码中引用 `obj/Stamped/` 或随机生成的 namespace；它们只是构建产物，每次构建都可能变化。

以下为XAML描述UI的案例：
```xaml
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="PotatoVN.App.PluginBase.Controls.TestControl"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">
    <Grid>
        <Button Content="Hello World!"/>
    </Grid>
</UserControl>
```

```csharp
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.Controls
{
    public sealed partial class TestControl : UserControl
    {
        //_contentLoaded为UserControl使用XAML描述时自动生成的字段，不需要你自己定义
        public TestControl() => XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
    }
}
```

## C#描述UI

如果你计划采用c#描述UI，可以参考以下例子。

以下示例代码将生成一个包含嵌套插件控件、设置项和账户面板的 UI：

```csharp
public FrameworkElement CreateSettingUi()
{
    StdStackPanel panel = new();
    panel.Children.Add(new UserControl1().WarpWithPanel());
    panel.Children.Add(new StdSetting("设置标题", "这是一个设置",
        AddToggleSwitch(_data, nameof(_data.TestBool))).WarpWithPanel());
    StdAccountPanel accountPanel = new StdAccountPanel("title", "userName", "Description",
        new Button().WarpWithPanel());
    panel.Children.Add(accountPanel);
    return panel;
}
```

在`Controls/Prefabs`目录下，我们提供了一些预设的UI控件（如`StdSetting`、`StdAccountPanel`等），你可以直接使用它们来快速构建你的插件UI。

## UI注入软件
PotatoVN提供了多种UI注入接口，允许插件将自定义UI注入到应用的不同位置。你可以在应用公开库的`Contracts/PluginUi`目录下找到这些接口的定义。如果你需要的UI注入位置没有接口，你可以阅读软件本体的代码，并使用`harmony`库来创建新的UI注入点。

## 与宿主保持相同的风格
为了让插件UI与宿主应用保持一致的风格，建议使用PotatoVN提供的预设Style：Controls/Styles目录下有以下预设style，请务必考虑使用它们：
* FontSize：定义了应用中使用的字体大小。
* TextBlock： 定义了常见的TextBlock样式。
* Thickness：定义了各种常用的Margin和Padding值。

使用示例：
```xaml
<TextBlock Style="{ThemeResource DescriptionTextStyle}" Text="这是一段描述文本"/>
```