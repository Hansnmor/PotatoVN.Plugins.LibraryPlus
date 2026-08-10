using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>
/// 插件数据。[ObservableProperty] 属性变化后会自动触发 <see cref="Plugin.SaveData"/> 持久化。
/// </summary>
public partial class PluginData : ObservableRecipient
{
    /// <summary>
    /// 状态筛选模式：All=全部（不过滤）/ ToPlay=待玩预设（未标记+游玩中+想玩）/ Custom=自定义。
    /// </summary>
    [ObservableProperty] private string _filterMode = "All";

    /// <summary>
    /// 自定义筛选时要显示的游玩状态（PlayType 枚举名字符串列表）；仅 <see cref="FilterMode"/> 为 Custom 时生效，
    /// 空列表表示不过滤（等同全部）。
    /// </summary>
    [ObservableProperty] private List<string> _includedPlayTypes = new();

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
