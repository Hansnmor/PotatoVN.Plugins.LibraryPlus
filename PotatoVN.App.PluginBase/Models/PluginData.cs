using CommunityToolkit.Mvvm.ComponentModel;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>
/// 插件数据。[ObservableProperty] 属性变化后会自动触发 <see cref="Plugin.SaveData"/> 持久化。
/// </summary>
public partial class PluginData : ObservableRecipient
{
    /// <summary>是否启用「排除已玩过」（默认关闭；跨页面重建/应用重启保持）</summary>
    [ObservableProperty] private bool _hidePlayed;
}
