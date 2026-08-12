using System;
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

    /// <summary>时长区间筛选键（All=全部/Under10/10to20/20to40/Over40/Unknown，默认全部）</summary>
    [ObservableProperty] private string _rangeKey = "All";

    /// <summary>内容分类筛选键（All=全部/Moe/Story/Nukige/Other，默认全部）</summary>
    [ObservableProperty] private string _categoryKey = "All";

    /// <summary>形态分类筛选键（All=全部/TraditionalAdv/NonTraditionalAdv，默认全部）</summary>
    [ObservableProperty] private string _formKey = "All";

    /// <summary>
    /// kungal 搜刮数据缓存：gameUuid → 数据（gid、tag 全量含热度/剧透分级、类型投票）。
    /// 注意：字典整体替换赋值才会触发持久化（集合内修改不触发 PropertyChanged）。
    /// </summary>
    [ObservableProperty] private Dictionary<string, KungalGameData> _kungalData = new();

    /// <summary>
    /// 用户手动分类覆盖：gameUuid → 内容轴分类名（Moe/Story/Nukige/Other）。
    /// 优先级高于自动分类（投票/热度/旧规则）——自动分类有边界，个人认知靠手动兜底。
    /// 空字符串/不存在 = 用自动分类。
    /// </summary>
    [ObservableProperty] private Dictionary<string, string> _userCategory = new();

    /// <summary>
    /// Bangumi tag 投票数据：gameUuid → tag+count 列表（批量搜刮时采集，需宿主已登录 Bangumi）。
    /// 供分类器作社区投票信号（补充 kungal 盲区，如 kungal 无「拔作」tag 的游戏）。
    /// </summary>
    [ObservableProperty] private Dictionary<string, List<BgmTagData>> _bgmData = new();
}

/// <summary>某款游戏的 kungal 数据（批量搜刮时从详情采集，供 M3 投票分类使用）</summary>
public class KungalGameData
{
    public int Gid { get; set; }

    public string? VndbId { get; set; }

    /// <summary>全量 tag（含 category/galgame_count/spoiler_level，不过滤）</summary>
    public List<KungalTagData> Tags { get; set; } = new();

    /// <summary>引擎列表（形态轴判定证据：RPG Maker 等非 ADV 引擎）</summary>
    public List<string> Engine { get; set; } = new();

    /// <summary>
    /// 用户类型投票（拆票值）：galgame_type 名（moe/plot/ba_saku/daily…）→ 拆票权重。
    /// 拆票语义：galgame_type 是多选属性标签（一个评分可勾多个类型），每个评分勾 N 个类型则各计 1/N——
    /// 「勾 plot」表示"有剧情属性"，不等于"这是剧情作"，拆票避免属性提及数被误当类型投票。
    /// </summary>
    public Dictionary<string, double> TypeVotes { get; set; } = new();

    public int RatingCount { get; set; }

    public double? AvgRating { get; set; }

    public DateTime FetchedAt { get; set; }
}

/// <summary>Bangumi tag（保持站点原始结构：标签名 + 投票数）</summary>
public class BgmTagData
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>kungal tag（保持站点原始结构）</summary>
public class KungalTagData
{
    public string Name { get; set; } = "";

    /// <summary>content（题材）/ meta（系统机制）</summary>
    public string Category { get; set; } = "";

    /// <summary>全站热度（挂该标签的游戏数），用于分类权重</summary>
    public int GalgameCount { get; set; }

    public int SpoilerLevel { get; set; }
}
