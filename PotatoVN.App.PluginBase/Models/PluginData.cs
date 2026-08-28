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

    /// <summary>
    /// 扩展库页工具栏搜索关键词（与排序/筛选等状态一致持久化）：
    /// 页面切走再切回/应用重启后恢复，直到用户手动删除（清空后置 ""，不再恢复）。
    /// </summary>
    [ObservableProperty] private string _searchKeyword = "";

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
    /// 用户手动形态覆盖：gameUuid → 形态分类名（TraditionalAdv/NonTraditionalAdv）。
    /// 优先级高于自动形态判定——数据极限案例（Summer 乡间性活等 Galgame.Tags 无法区分真伪 SLG）靠手动兜底。
    /// </summary>
    [ObservableProperty] private Dictionary<string, string> _userForm = new();

    /// <summary>
    /// Bangumi tag 投票数据：gameUuid → tag+count 列表（批量搜刮时采集，需宿主已登录 Bangumi）。
    /// 供分类器作社区投票信号（补充 kungal 盲区，如 kungal 无「拔作」tag 的游戏）。
    /// </summary>
    [ObservableProperty] private Dictionary<string, List<BgmTagData>> _bgmData = new();

    /// <summary>
    /// 加权评分缓存（统一，独立于 kungal）：gameUuid → 两站评分。
    /// 进详情页时从 VNDB / bangumi 官方 API 直查采集；卡片与「按加权评分」排序共用。
    /// 注意：字典整体替换赋值才会触发持久化（集合内修改不触发 PropertyChanged）。
    /// </summary>
    [ObservableProperty] private Dictionary<string, RatingData> _ratingCache = new();

    /// <summary>
    /// 启动守卫开关（默认关）：试玩（短开未达阈值即退出）后自动把「上次游玩时间」还原成启动前的值，
    /// 防止原生主页「最后游玩」排序被纯测试打开顶到最前。真玩够阈值不动任何数据；
    /// 仅对累计总时长低于阈值的游戏生效，已玩进去的老游戏回访完全不受影响。
    /// </summary>
    [ObservableProperty] private bool _launchGuardEnabled = false;

    /// <summary>守卫阈值（分钟）：既是试玩观察资格线（累计总时长低于它才受守卫管），也是本轮真玩达标线，默认 5</summary>
    [ObservableProperty] private int _launchGuardThresholdMinutes = 5;

    /// <summary>
    /// 扩展库页是否显示「非本地游戏」（原生称「虚拟游戏」：库里有条目但本机没有任何
    /// 本地文件夹/Steam 源——换机后经云同步只恢复元数据的记录即属此类）。默认不显示，
    /// 与原生游戏页 VirtualGameFilter 的默认行为对齐；关闭时这些条目不进列表/统计/批量操作。
    /// </summary>
    [ObservableProperty] private bool _displayVirtualGame = false;
}

/// <summary>加权评分缓存条目（VNDB / bangumi 官方 API 采集）</summary>
public class RatingData
{
    /// <summary>bangumi 评分（10 分制；无则 0）</summary>
    public double BgmScore { get; set; }

    public int BgmCount { get; set; }

    /// <summary>vndb 评分（10 分制，VNDB API 贝叶斯分；无则 0）</summary>
    public double VndbScore { get; set; }

    public int VndbCount { get; set; }

    /// <summary>拉取时间（缓存命中不再刷新；后续可加过期策略）</summary>
    public DateTime FetchedAt { get; set; }
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
