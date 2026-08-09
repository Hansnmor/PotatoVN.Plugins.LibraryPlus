using CommunityToolkit.Mvvm.ComponentModel;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>
/// 插件数据类示例
///
/// 其中，[ObservableProperty] 特性是用来给UI绑定的，如果你的某个数据需要在UI上实时更新（即代码里修改变量会实时反馈到UI上，反之也成立）
/// 对于不需要反应到UI上的数据，可以直接使用普通的属性。
/// </summary>
public partial class PluginData : ObservableRecipient
{
    //标记为ObservableProperty的变量会自动生成一个大写开头的属性，如这里会生成一个TestBool属性，之后应该永远使用这个属性而不是字段本身
    [ObservableProperty] private bool _testBool; 
}