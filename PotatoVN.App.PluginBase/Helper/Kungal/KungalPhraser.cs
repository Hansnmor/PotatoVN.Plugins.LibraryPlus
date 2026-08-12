using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GalgameManager.Contracts.Phrase;
using GalgameManager.Enums;
using GalgameManager.Models;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Helper.Kungal;

/// <summary>
/// 搜刮范围控制（GetGalgameInfo 按标志位决定填写哪些字段）
/// 默认仅 简介+标签：其余字段一律回传原值，避免宿主 ParseAsync 无条件覆盖
/// （Rating/ReleaseDate/ChineseName/OriginalName/Characters/ImageUrl 为无条件覆盖字段，必须回传保护）。
/// </summary>
[Flags]
public enum ScrapeFields
{
    Description = 1,
    Tags = 2,
    CnName = 4,
    Developer = 8,
    Engine = 16,
    ReleaseDate = 32,
}

/// <summary>
/// kungal 搜刮器（IParserProvider 注册给宿主的实例）
/// 匹配三层：gid 记忆 → vndb_id 搜索 → 名称搜索（Similarity 校验）
/// </summary>
public class KungalPhraser : IGalInfoPhraser
{
    /// <summary>插件自定义 RssType（须 &gt;100，官方建议随机防冲突）</summary>
    public const int ParserId = 921470;

    private readonly KungalClient _client = new();

    /// <summary>当前搜刮范围（插件页可修改此实例配置，官方文档允许）</summary>
    public ScrapeFields Fields { get; set; } = ScrapeFields.Description | ScrapeFields.Tags;

    public RssType GetPhraseType() => (RssType)ParserId;

    public void UpdateData(IGalInfoPhraserData data) { } // 插件搜刮器无需宿主数据

    public async Task<Galgame?> GetGalgameInfo(Galgame galgame)
    {
        try
        {
            var fetched = await FetchDetailAsync(galgame);
            if (fetched == null) return null;
            return BuildResult(galgame, fetched.Value.Detail);
        }
        catch (Exception e)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"kungal 搜刮失败: {galgame.Name.Value} ({e.Message})");
            return null;
        }
    }

    /// <summary>
    /// 批量搜刮用：匹配（三层）+ 拉详情。返回 null 表示匹配失败或详情拉取失败。
    /// 匹配成功后把 gid 写入 <see cref="Galgame.IdForPlugins"/>（随游戏持久化，二次搜刮直连）。
    /// </summary>
    internal async Task<(int Gid, KungalDetail Detail)?> FetchDetailAsync(Galgame galgame)
    {
        int? gid = await MatchGidAsync(galgame);
        if (gid == null) return null;
        KungalDetail? detail = await _client.GetDetailAsync(gid.Value);
        if (detail == null) return null;
        galgame.IdForPlugins[ParserId] = gid.Value.ToString();
        return (gid.Value, detail);
    }

    /// <summary>从 kungal 详情构造搜刮结果（宿主合并用：字段控制 + 无条件覆盖字段回传保护）</summary>
    internal Galgame BuildResult(Galgame galgame, KungalDetail detail)
    {
        int gid = detail.Id;
        Galgame result = new()
        {
            RssType = GetPhraseType(),
            Id = gid.ToString(),
        };

        // ===== 无条件覆盖字段：一律回传原值保护 =====
        result.Rating = galgame.Rating.Value;
        result.ReleaseDate = galgame.ReleaseDate.Value;
        result.CnName = galgame.ChineseName.Value;
        result.Name = galgame.OriginalName.Value; // 宿主取 tmp.Name 写 OriginalName
        result.Characters = galgame.Characters;   // 宿主无条件替换 Characters
        result.ImageUrl = galgame.ImageUrl;       // 宿主无条件替换封面

        // ===== 按搜刮范围填写 =====
        if (Fields.HasFlag(ScrapeFields.Description))
        {
            string? intro = PickIntroduction(detail);
            // 宿主对 Description 无条件覆盖：搜不到更好的必须回传原简介，防清空
            result.Description = string.IsNullOrWhiteSpace(intro)
                ? galgame.Description.Value
                : intro ?? "";
        }
        else
        {
            result.Description = galgame.Description.Value;
        }

        // 宿主 SyncCollection 是「替换」语义（结果=other），不是并集——
        // 因此这里必须自己合并：原 tags ∪ kungal tags（去重），否则冷门游戏的少量
        // kungal tag 会把 Bangumi 的丰富 tag 整个顶掉
        if (Fields.HasFlag(ScrapeFields.Tags))
        {
            var merged = new System.Collections.ObjectModel.ObservableCollection<string>();
            foreach (string? t in galgame.Tags.Value ?? [])
                if (!string.IsNullOrWhiteSpace(t) && !merged.Contains(t))
                    merged.Add(t);
            foreach (string t in CleanTags(detail.Tag))
                if (!merged.Contains(t))
                    merged.Add(t);
            result.Tags = new LockableProperty<System.Collections.ObjectModel.ObservableCollection<string>>(merged);
        }
        else
        {
            // Tags 模式关闭时回传原 tags，否则宿主替换成空集合会清空全部 tag
            result.Tags = galgame.Tags;
        }

        if (Fields.HasFlag(ScrapeFields.CnName) && !string.IsNullOrWhiteSpace(detail.Name?.ZhCn))
            result.CnName = detail.Name.ZhCn;

        if (Fields.HasFlag(ScrapeFields.Developer))
        {
            string? developer = detail.Official
                .FirstOrDefault(o => o.Roles.Contains("developer"))?.Name;
            result.Developer = string.IsNullOrWhiteSpace(developer)
                ? galgame.Developer.Value
                : developer ?? "";
        }
        else
        {
            result.Developer = galgame.Developer.Value;
        }

        if (Fields.HasFlag(ScrapeFields.Engine))
        {
            string? engine = detail.Engine.FirstOrDefault()?.Name;
            result.Engine = string.IsNullOrWhiteSpace(engine) ? galgame.Engine.Value : engine ?? "";
        }
        else
        {
            result.Engine = galgame.Engine.Value;
        }

        if (Fields.HasFlag(ScrapeFields.ReleaseDate) &&
            IGalInfoPhraser.GetDateTimeFromString(detail.ReleaseDate) is { } releaseDate &&
            releaseDate != DateTime.MinValue)
            result.ReleaseDate = releaseDate;

        Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
            $"kungal 搜刮: gid={gid} 简介={result.Description.Value?.Length ?? 0}字 " +
            $"tags={result.Tags.Value?.Count ?? 0}个 原tags={galgame.Tags.Value?.Count ?? 0}个 fields={Fields}");

        return result;
    }

    /// <summary>从 kungal 详情采集完整数据（全量 tag + 类型投票），供批量搜刮写入 PluginData（M3 投票分类用）</summary>
    internal static KungalGameData BuildKungalData(KungalDetail detail)
    {
        var data = new KungalGameData
        {
            Gid = detail.Id,
            VndbId = detail.VndbId,
            FetchedAt = DateTime.Now,
            Tags = detail.Tag.Select(t => new KungalTagData
            {
                Name = t.Name ?? "",
                Category = t.Category ?? "",
                GalgameCount = t.GalgameCount,
                SpoilerLevel = t.SpoilerLevel,
            }).ToList(),
        };
        // 用户类型投票：每个评分可勾多个 galgame_type
        foreach (var rating in detail.Ratings)
        {
            foreach (string type in rating.GalgameType ?? [])
                data.TypeVotes[type] = data.TypeVotes.GetValueOrDefault(type) + 1;
        }
        return data;
    }

    /// <summary>
    /// 判断文本是否中文（批量简介应用规则：已有中文简介不覆盖）。
    /// 注意：日文含汉字（CJK 区间），单纯查汉字会误判日文为中文——
    /// 含日文假名（平假名 / 片假名）即视为非中文；但片假名区间的
    /// 「・」（U+30FB 中黑点，中文也常用）与「ー」（U+30FC 长音符）不算假名。
    /// </summary>
    internal static bool IsChinese(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool hasHan = false;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) hasHan = true;
            if (c >= '\u3040' && c <= '\u30FF' && c != '\u30FB' && c != '\u30FC')
                return false; // 假名（排除 ・ 和 ー）→ 日文
        }
        return hasHan;
    }

    /// <summary>三层匹配：gid 记忆 → vndb_id 搜索 → 名称搜索</summary>
    private async Task<int?> MatchGidAsync(Galgame galgame)
    {
        // ① gid 记忆（IdForPlugins 随游戏持久化）
        if (galgame.IdForPlugins.GetValueOrDefault(ParserId) is { } remembered &&
            int.TryParse(remembered, out int gid) && gid > 0)
            return gid;

        // ② vndb_id 搜索（主路径）
        // 注意：PotatoVN 存的 VNDB ID 不带 v 前缀（VndbPhraser 去掉了），kungal 索引带 v —— 需尝试两种格式
        // 命中后校验候选详情的 vndb_id 与目标一致（归一化），防错误命中
        string? vndbId = galgame.Ids[(int)RssType.Vndb];
        if (!string.IsNullOrEmpty(vndbId) && vndbId != "-1")
        {
            foreach (string variant in VndbIdVariants(vndbId))
            {
                var byVndb = await _client.SearchAsync(variant);
                if (byVndb is not { Items.Count: > 0 }) continue;
                foreach (KungalCard candidate in byVndb.Items.Take(3))
                {
                    KungalDetail? candDetail = await _client.GetDetailAsync(candidate.Id);
                    if (candDetail?.VndbId != null &&
                        NormalizeVndb(candDetail.VndbId) == NormalizeVndb(vndbId))
                        return candidate.Id;
                }
            }
        }

        // ③ 名称搜索兜底（优先中文名，其次原名/显示名）
        // 注意：Jaro-Winkler 对词序敏感（「后宫双子洛丽塔」vs「双子洛丽塔后宫」），
        // 用前 3 候选 + 阈值放宽 + 字符重叠率（Dice 系数，词序免疫）双重校验
        foreach (string name in new[] { galgame.ChineseName.Value, galgame.OriginalName.Value, galgame.Name.Value }
                     .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
        {
            var byName = await _client.SearchAsync(name!);
            if (byName is not { Items.Count: > 0 }) continue;
            foreach (KungalCard candidate in byName.Items.Take(3))
            {
                string? candName = candidate.Name?.ZhCn ?? candidate.Name?.ZhTw ?? candidate.Name?.JaJp;
                if (candName == null) continue;
                if (IGalInfoPhraser.Similarity(candName, name!) >= 0.5 ||
                    CharOverlap(candName, name!) >= 0.7)
                    return candidate.Id;
            }
        }

        return null;
    }

    /// <summary>字符集重叠率（Dice 系数）：对词序/翻译差异免疫，如「后宫双子洛丽塔」与「双子洛丽塔后宫」=1.0</summary>
    private static double CharOverlap(string a, string b)
    {
        var setA = a.ToHashSet();
        var setB = b.ToHashSet();
        int inter = setA.Count(setB.Contains);
        return setA.Count + setB.Count == 0 ? 0 : 2.0 * inter / (setA.Count + setB.Count);
    }

    /// <summary>VNDB ID 归一化：去 v 前缀（"v17284" → "17284"）</summary>
    private static string NormalizeVndb(string id) => id.StartsWith("v") ? id[1..] : id;

    /// <summary>VNDB ID 搜索变体：原始格式 + 补/去 v 前缀（PotatoVN 存无前缀，kungal 索引有前缀）</summary>
    private static IEnumerable<string> VndbIdVariants(string id)
    {
        yield return id;
        if (id.StartsWith("v")) yield return id[1..];
        else yield return "v" + id;
    }

    /// <summary>简介取语言优先级：zh-cn → zh-tw → ja-jp → en-us</summary>
    private static string? PickIntroduction(KungalDetail detail)
    {
        KungalLang? intro = detail.Introduction;
        if (intro == null) return null;
        foreach (string text in new[] { intro.ZhCn, intro.ZhTw, intro.JaJp, intro.EnUs })
            if (!string.IsNullOrWhiteSpace(text))
                return KungalClient.HtmlToText(text);
        return null;
    }

    /// <summary>
    /// tag 清洗：仅 content 类、非剧透（spoiler_level==0）、
    /// 跳过别名拼接的超长 tag（含 / 、 ； 分隔符）、去重、限 30 个
    /// </summary>
    private static System.Collections.ObjectModel.ObservableCollection<string> CleanTags(List<KungalTag> tags)
    {
        var result = new System.Collections.ObjectModel.ObservableCollection<string>();
        var seen = new HashSet<string>();
        foreach (KungalTag tag in tags
                     .Where(t => t.Category == "content" && t.SpoilerLevel == 0)
                     .OrderByDescending(t => t.GalgameCount))
        {
            string? name = tag.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 40) continue;
            if (name.Contains('/') || name.Contains('、') || name.Contains('；')) continue; // 别名拼接
            if (!seen.Add(name)) continue;
            result.Add(name);
            if (result.Count >= 30) break;
        }
        return result;
    }
}
