using CommunityToolkit.Mvvm.ComponentModel;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>
/// 插件数据。[ObservableProperty] 属性变化后会自动触发 <see cref="Plugin.SaveData"/> 持久化。
/// </summary>
public partial class PluginData : ObservableRecipient
{
    /// <summary>是否启用「排除已玩过」（默认关闭；跨页面重建/应用重启保持）</summary>
    [ObservableProperty] private bool _hidePlayed;

    /// <summary>主排序键（SortKey 枚举名，默认按预计时长）</summary>
    [ObservableProperty] private string _primarySortKey = "ExpectedPlayTime";

    /// <summary>主排序键是否降序</summary>
    [ObservableProperty] private bool _primaryDescending;

    /// <summary>次排序键（SortKey 枚举名，默认无）</summary>
    [ObservableProperty] private string _secondarySortKey = "Default";

    /// <summary>次排序键是否降序</summary>
    [ObservableProperty] private bool _secondaryDescending;

    /// <summary>时长区间筛选键（All=全部/Under10/10to20/20to40/Over40，默认全部）</summary>
    [ObservableProperty] private string _rangeKey = "All";
}
