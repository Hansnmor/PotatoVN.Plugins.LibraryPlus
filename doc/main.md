> 本文档计划用于给ai agent阅读，建议开发者请阅读[网页版的文档](https://potatovn.net/development/client-plugin/quick-start.html)获得更好的阅读体验~

## 项目大纲
这个工程是PotatoVN的插件脚手架，其用于给软件本体补充功能。
解决方案中包含两个项目:
* 应用公开库（`GalgameManager.WinApp.Base`）: 这是主项目 (PotatoVN 应用本体) 的一部分，通过 git submodule 的方式引入；
* 插件本体（`PotatoVN.App.PluginBase`）: 这是你编写插件代码的核心项目。

### 应用公开库 (GalgameManager.WinApp.Base)：
插件本体项目依赖于应用公开库中的`GalgameManager.WinApp.Base`项目。这个项目至关重要，因为它定义了：
* 基础模型: 如游戏类 (Galgame.cs)、游戏库类 (GalgameSourceBase.cs) 等。
* 功能接口: 以接口形式定义了插件能够注入的各种功能。
* 应用 API: 定义了应用本体暴露给插件调用的 API。

> 重要提示：
> * 在任何时候，你都**不应该**编辑 GalgameManager.WinApp.Base 项目中的任何内容。
> * 虽然 PotatoVN 会尽可能保证插件的兼容性，但建议你在工作开始前通过 git submodule update --remote 命令获取最新的 WinApp.Base，以避免潜在的兼容性问题。

### 插件本体
在插件本体项目中：
* **插件主类**: 必须包含一个插件主类（如此模板中的`Plugin.cs`），这个类**至少**要实现 IPlugin 接口，以表明它是一个插件。
* **功能实现**: 如果插件希望实现其他功能，请实现公开库中定义的各种功能接口。例如：
  * 实现`IParserProvider`接口表示插件能提供一个游戏数据搜刮器。
  * 实现`IPluginSetting`接口表示插件能提供一个设置 UI，应用会将其展示在插件管理界面（详见第五节）。
* **预设文件**: 项目中包含一些预设的 UI 控件（位于`Controls/Prefabs`文件夹下）以及 UI 注入所需的基础类（如XamlResourceLocatorFactory.cs）。

## 调用PotatoVN API
调用 PotatoVN 应用本体提供的功能主要通过 HostApi 实现。

当你的插件被加载和启用时，PotatoVN 会调用你的插件主类（实现了`IPlugin`接口的类）中的`InitializeAsync(IPotatoVnApi hostApi)`方法。

这个方法会传入一个`IPotatoVnApi`接口的实例，它就是 HostApi。你需要在你的插件代码中找到一个合适的位置（例如，一个静态字段或单例属性）来保存这个 hostApi 对象的引用，以便在插件的生命周期内随时调用其提供的方法。

PotatoVN本体中已经内置了`harmony`库的支持。因此，如果现有的API无法满足你的需求，你可以使用`harmony`库来修改PotatoVN的行为。PotatoVN的软件本体代码位于项目`PotatoVN/GalgameManager`下，其文档位于`PotatoVN/.kilocode/rules/project-info-galgamemanager.md`下，在使用`harmony`库进行修改时，建议你参考这些代码和文档以了解PotatoVN的内部结构和实现细节。注意：插件项目没有预装`harmony`库，你需要自己通过NuGet安装它。

## 完成开发

在你完成第一个任务（也就是用户交给你的任务）之前，请务必完成写在`Plugin.cs`里的TODO事项。完成todo后，你可以把那些TODO注释给删除掉。

此外，请你修改仓库根目录下的README.md，把它里面的脚手架内容删掉，改为和当前插件一致的README。


## 其他目录
* `doc/ui.md`：如果你的插件需要实现自定义的UI，请你阅读这个文档了解UI相关的开发细节。
* `doc/sidebar.md`：如果你的插件需要向软件侧边栏添加一个按钮，请阅读这个文档。
* `doc/dialog.md`：如果你的插件需要实现自定义的对话框，请你阅读这个文档了解对话框相关的开发细节。
* `doc/data.md`：如果你的插件需要保存/读写自己的插件数据，请阅读这个文档。
* `doc/parser.md`：如果你的插件需要实现一个游戏信息搜刮器，请阅读这个文档。
