using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Contracts.Phrase;
using GalgameManager.Enums;
using GalgameManager.Models;
using PotatoVN.App.PluginBase.Helper.Bangumi;
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
            Galgame result = BuildResult(galgame, fetched.Value.Detail);

            // 原生设置页「使用游戏ID从数据源更新数据」选 kungal 时（galgame.RssType 已为 kungal），
            // 绑定插件角色功能：角色简介（空/非中文才填）+ 简体中文名替换 + 已有缺图角色补图。
            // 注意：此路径无确认弹窗，不补齐缺失角色（避免静默新增角色）；补齐只在批量搜刮对话框勾选时进行。
            // 混合搜刮等其他调用路径 RssType 非 kungal，不触发。
            if (galgame.RssType == (RssType)ParserId)
            {
                var (charApps, _, _, charNeedsImages) =
                    await FetchCharacterIntrosAsync(galgame, fetched.Value.Detail, addMissing: false);
                foreach (var (character, intro) in charApps)
                    character.Summary = intro;
                // 缺图角色并发下载（反射宿主 DownloadHelper；失败保持默认图，不影响主流程）
                if (charNeedsImages.Count > 0)
                {
                    using var sem = new SemaphoreSlim(4);
                    var imgTasks = charNeedsImages.Select(async c =>
                    {
                        await sem.WaitAsync();
                        try { await HostServices.DownloadCharacterImagesAsync(c); }
                        finally { sem.Release(); }
                    });
                    await Task.WhenAll(imgTasks);
                }
            }
            return result;
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
            Engine = detail.Engine.Select(e => e.Name ?? "").Where(n => n != "").ToList(),
            Tags = detail.Tag.Select(t => new KungalTagData
            {
                Name = t.Name ?? "",
                Category = t.Category ?? "",
                GalgameCount = t.GalgameCount,
                SpoilerLevel = t.SpoilerLevel,
            }).ToList(),
        };
        // 用户类型投票：galgame_type 是多选属性标签，拆票采集（勾 N 个类型各计 1/N）
        foreach (var rating in detail.Ratings)
        {
            List<string>? types = rating.GalgameType;
            if (types is null || types.Count == 0) continue;
            double split = 1.0 / types.Count;
            foreach (string type in types)
                data.TypeVotes[type] = data.TypeVotes.GetValueOrDefault(type) + split;
            data.RatingCount++;
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

    /// <summary>
    /// 批量搜刮用：并发拉取游戏全部角色的 kungal 详情后：
    /// ① 匹配：Ids 锚点（VNDB/Bangumi 角色 id）优先，名称匹配（归一化精确 → Dice 重叠率）贪心唯一配对——
    ///    解决 bgm 与 kungal 角色列表来源不一致（kungal 也会从 VNDB 收录角色、繁简体/空格差异）导致的漏配；
    /// ② 简介：匹配上的角色空/非中文才填（复用游戏简介规则，已有中文不动）；
    /// ③ 名字：日文名角色（含假名）从 bgm 角色页解析「简体中文名」替换（页面 HTML 是唯一来源，
    ///    v0 API 无 name_cn 字段；无标签/抓取失败不动）；
    /// ④ 补齐（addMissing）：kungal 有而库中没有的角色新建进 <see cref="Galgame.Characters"/>
    ///    （简体中文名优先、中文简介，Ids 槽位回填 bgm/vndb 角色 id 供后续稳定匹配）；
    /// ⑤ 图片：新增角色 + 已匹配但无本地图的角色填 kungal 图片 URL（PreviewImageUrl/ImageUrl 是内存字段，
    ///    由调用方反射宿主 DownloadHelper 下载落盘：image=头像（预览），figure=立绘（大图），figure 缺失用 image 兜底）。
    /// 剧透角色（spoiler&gt;0，隐藏女主/真结局角色）整条跳过——不匹配/不简介/不补齐，避免剧透。
    /// </summary>
    /// <returns>(应应用的 (角色, 中文简介) 列表, 已存在角色被改名的数量, 新增角色列表, 需要下载图片的角色列表)</returns>
    internal async Task<(List<(GalgameCharacter Character, string Intro)> Intros, int Renamed,
        List<GalgameCharacter> Added, List<GalgameCharacter> NeedsImages)>
        FetchCharacterIntrosAsync(Galgame game, KungalDetail detail, bool addMissing = false)
    {
        var result = new List<(GalgameCharacter, string)>();
        var addedChars = new List<GalgameCharacter>();
        var needsImages = new List<GalgameCharacter>();
        int renamed = 0;
        var cards = detail.Characters.Where(c => c.Spoiler <= 0).ToList(); // 剧透角色整条跳过
        if (cards.Count == 0) return (result, renamed, addedChars, needsImages);

        // 并发拉取全部角色详情（HttpClient 异步 IO 不占线程；每请求 200ms 节流同时生效）
        KungalCharacter?[] details = await Task.WhenAll(
            cards.Select(c => _client.GetCharacterAsync(c.Id)));
        var kcs = details.Where(k => k != null).Cast<KungalCharacter>().ToList();
        if (kcs.Count == 0) return (result, renamed, addedChars, needsImages);

        // 收集需要 bgm 简体中文名的角色：勾选补齐时拉全部（cn 参与匹配，防「Ymgal 中文名角色 vs kungal
        // 日文名+cn」配对不上导致的重复补齐）；否则只拉日文名角色（仅用于改名）
        var bgmClient = new BgmClient();
        var cnMap = new Dictionary<int, string>();
        var nameNeed = new List<(int BgmId, int Ki)>();
        for (int i = 0; i < kcs.Count; i++)
        {
            var (_, bgmId) = ExtractLinkIds(kcs[i].Links);
            if (bgmId == null || !int.TryParse(bgmId, out int bgm)) continue;
            if (addMissing || IsJapaneseName(kcs[i].Name)) nameNeed.Add((bgm, i));
        }
        using (var sem = new SemaphoreSlim(3)) // 网页服务礼貌限流
        {
            var tasks = nameNeed.Select(async n =>
            {
                await sem.WaitAsync();
                try
                {
                    string? cn = await bgmClient.GetCharacterCnNameAsync(n.BgmId);
                    if (!string.IsNullOrWhiteSpace(cn)) cnMap[n.BgmId] = cn;
                }
                finally
                {
                    sem.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        // 匹配：Ids 锚点 + 名称贪心唯一配对（bgm 简体中文名也参与名称匹配——Ymgal 源角色只有
        // 中文名、Ids 无 bgm/vndb id 时，靠 cn 与库角色名配对，防重复补齐）
        (GalgameCharacter? Target, double Score)[] matched = MatchCharacters(game, kcs, cnMap);

        // 应用：简介 / 改名 / 补齐
        for (int i = 0; i < kcs.Count; i++)
        {
            KungalCharacter kc = kcs[i];
            var (vndbId, bgmId) = ExtractLinkIds(kc.Links);
            string? intro = PickCharacterIntro(kc);
            string? cn = null;
            if (bgmId != null && int.TryParse(bgmId, out int bgmKey))
                cnMap.TryGetValue(bgmKey, out cn);

            if (matched[i].Target is { } target)
            {
                // 简介：空或非中文才填
                if (!string.IsNullOrWhiteSpace(intro) && !IsChinese(target.Summary))
                    result.Add((target, intro));
                // 名字：日文名 → 简体中文名
                if (!string.IsNullOrWhiteSpace(cn) && cn != target.Name && IsJapaneseName(target.Name))
                {
                    target.Name = cn;
                    renamed++;
                }
                // 图片：已匹配但无本地图的角色补 kungal 图（上轮补齐/手动添加的角色缺图）
                if (!string.IsNullOrWhiteSpace(kc.Image) &&
                    (string.IsNullOrWhiteSpace(target.PreviewImagePath) ||
                     target.PreviewImagePath == Galgame.DefaultCharacterImagePath))
                {
                    target.PreviewImageUrl = kc.Image;
                    target.ImageUrl = string.IsNullOrWhiteSpace(kc.Figure) ? kc.Image : kc.Figure;
                    needsImages.Add(target);
                }
            }
            else if (addMissing)
            {
                // 补齐：kungal 有而库中没有的角色一律新建（拿全角色列表）。
                // 无中文简介/无 bgm 简体中文名时保留日文名与空简介（有中文优先）；Ids 回填供后续匹配；
                // 图片 URL 填内存字段，由调用方下载落盘：image=头像（预览），figure=立绘（大图），figure 缺失用 image 兜底
                var nc = new GalgameCharacter
                {
                    Name = cn ?? kc.Name ?? "",
                    PreviewImageUrl = kc.Image,
                    ImageUrl = string.IsNullOrWhiteSpace(kc.Figure) ? kc.Image : kc.Figure,
                };
                if (vndbId != null) nc.Ids[(int)RssType.Vndb] = vndbId;
                if (bgmId != null) nc.Ids[(int)RssType.Bangumi] = bgmId;
                game.Characters.Add(nc);
                addedChars.Add(nc);
                needsImages.Add(nc);
                if (!string.IsNullOrWhiteSpace(intro))
                    result.Add((nc, intro));
            }
        }
        return (result, renamed, addedChars, needsImages);
    }

    /// <summary>是否日文名（含假名即日文；「・」与「ー」不算假名，与 <see cref="IsChinese"/> 对称）</summary>
    internal static bool IsJapaneseName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
            if (c >= '\u3040' && c <= '\u30FF' && c != '\u30FB' && c != '\u30FC')
                return true;
        return false;
    }

    /// <summary>
    /// 检测同游戏内「确定重复」的角色（bgm 角色 id 相同，或 vndb 角色 id 相同——同一实体，零误判）。
    /// 注意：bgm id 与 vndb id 是不同命名空间（都可能是纯数字），key 加前缀区分防误判。
    /// 只检测不删除：删除是破坏性操作，重复角色的处置由用户决定。
    /// </summary>
    /// <returns>重复角色组列表：(组内首个角色名, 组内角色数)；无重复返回空列表</returns>
    internal static List<(string Name, int Count)> DetectDuplicateCharacters(Galgame game)
    {
        var groups = new Dictionary<string, List<string>>();
        foreach (GalgameCharacter c in game.Characters)
        {
            string? bgm = c.Ids[(int)RssType.Bangumi];
            string? vndb = c.Ids[(int)RssType.Vndb];
            string? id = null;
            if (!string.IsNullOrWhiteSpace(bgm) && bgm != "-1") id = "b:" + bgm;
            else if (!string.IsNullOrWhiteSpace(vndb) && vndb != "-1") id = "v:" + vndb;
            if (id is null) continue; // 无外链 id 的角色无法确定重复
            if (!groups.TryGetValue(id, out List<string>? list)) groups[id] = list = new();
            list.Add(c.Name);
        }
        var result = new List<(string, int)>();
        foreach (List<string> g in groups.Values)
            if (g.Count > 1) result.Add((g[0], g.Count));
        return result;
    }

    /// <summary>角色简介取中文：intros 里 zh-Hans/zh-CN 优先，其次任意 zh，兜底单语言 intro 字段（须中文）</summary>
    private static string? PickCharacterIntro(KungalCharacter kc)
    {
        string? fallback = null;
        foreach (KungalCharIntro it in kc.Intros)
        {
            string lang = it.Lang?.ToLowerInvariant() ?? "";
            string text = it.Intro ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            string clean = KungalClient.HtmlToText(text).Trim();
            if (clean.Length == 0) continue;
            if (lang.StartsWith("zh-hans") || lang == "zh-cn") return clean;
            if (lang.StartsWith("zh") && fallback == null) fallback = clean;
        }
        if (fallback != null) return fallback;
        if (!string.IsNullOrWhiteSpace(kc.Intro) && IsChinese(kc.Intro))
            return KungalClient.HtmlToText(kc.Intro).Trim();
        return null;
    }

    /// <summary>
    /// 批量角色匹配：Ids 锚点（VNDB/Bangumi 角色 id）优先；名称匹配（归一化精确 → Dice 重叠率）按得分降序贪心，
    /// 每个 PotatoVN 角色只配一个 kungal 角色（防相似名双胞胎错配）。
    /// 名称候选：kungal 名 / latin 名 / bgm 简体中文名（cnMap）——简体中文名参与匹配是为了
    /// 覆盖「库角色是中文名（Ymgal 源）而 kungal 名是日文」的场景，防重复补齐。
    /// 得分：2=Ids 锚点，1=归一化名称精确，0.6~1=Dice 重叠（吸收繁简/空格/词序差异）。
    /// 返回与 kcs 等长的数组。
    /// </summary>
    private static (GalgameCharacter? Target, double Score)[] MatchCharacters(
        Galgame game, List<KungalCharacter> kcs, Dictionary<int, string> cnMap)
    {
        var result = new (GalgameCharacter? Target, double Score)[kcs.Count];
        var pool = game.Characters.Select(c => (Char: c, Name: NormalizeName(c.Name))).ToList();
        var used = new HashSet<GalgameCharacter>();

        // 第一轮：Ids 锚点（跨来源匹配的关键——kungal 角色 links 与 PotatoVN 角色 Ids 槽位对齐）
        for (int i = 0; i < kcs.Count; i++)
        {
            var (vndbId, bgmId) = ExtractLinkIds(kcs[i].Links);
            foreach (var (c, _) in pool)
            {
                if (used.Contains(c)) continue;
                // VNDB 角色 id 归一化：PotatoVN 存 "c165241"（VndbPhraser 只去 v 前缀），kungal 侧已去 c——统一比较
                if ((vndbId != null && NormalizeVndbCharId(c.Ids[(int)RssType.Vndb]) == vndbId) ||
                    (bgmId != null && c.Ids[(int)RssType.Bangumi] == bgmId))
                {
                    result[i] = (c, 2.0);
                    used.Add(c);
                    break;
                }
            }
        }

        // 第二轮：名称匹配（精确 → Dice），候选集按得分降序贪心分配
        var cands = new List<(int Ki, GalgameCharacter C, double Score)>();
        for (int i = 0; i < kcs.Count; i++)
        {
            if (result[i].Target != null) continue;
            string? n1 = NormalizeName(kcs[i].Name);
            string? n2 = NormalizeName(kcs[i].NameJa);
            string? n3 = null;
            var (_, bgmId) = ExtractLinkIds(kcs[i].Links);
            if (bgmId != null && int.TryParse(bgmId, out int b) && cnMap.TryGetValue(b, out string? cn))
                n3 = NormalizeName(cn);
            foreach (var (c, poolName) in pool)
            {
                if (used.Contains(c) || poolName == null) continue;
                double s = 0;
                foreach (string? nx in new[] { n1, n2, n3 })
                {
                    if (nx == null) continue;
                    if (poolName == nx)
                    {
                        s = 1.0; // 归一化后精确相等（吸收空格/全角/大小写差异）
                        break;
                    }
                    // Dice 字符重叠率：吸收繁简差异（如「倉田サナエ」vs「仓田サナエ」≈0.67）；短名不参与防误配
                    if (poolName.Length >= 2 && nx.Length >= 2)
                    {
                        double d = CharOverlap(poolName, nx);
                        if (d > s) s = d;
                    }
                }
                if (s >= 0.6) cands.Add((i, c, s));
            }
        }
        foreach (var (ki, c, s) in cands.OrderByDescending(x => x.Score))
        {
            if (result[ki].Target != null || used.Contains(c)) continue;
            result[ki] = (c, s);
            used.Add(c);
        }
        return result;
    }

    /// <summary>从 links 提取 VNDB/Bangumi 角色 id（vndb.org/c165241 → "165241"；bgm.tv/character/211740 → "211740"）</summary>
    private static (string? VndbId, string? BgmId) ExtractLinkIds(List<KungalCharLink> links)
    {
        string? vndb = null, bgm = null;
        foreach (KungalCharLink link in links)
        {
            string? url = link.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;
            string? last = url.TrimEnd('/').Split('/').LastOrDefault();
            if (last == null) continue;
            switch (link.Source?.ToLowerInvariant())
            {
                case "vndb" when last.StartsWith("c"): vndb = last[1..]; break;
                case "bangumi": bgm = last; break;
            }
        }
        return (vndb, bgm);
    }

    /// <summary>名称归一化：去空白、全角转半角、小写（角色名兜底匹配用）</summary>
    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c >= '\uFF01' && c <= '\uFF5E') sb.Append((char)(c - 0xFEE0)); // 全角→半角
            else sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>VNDB 角色 id 归一化：去 c/v 前缀（"c165241"/"v165241" → "165241"）。
    /// PotatoVN VndbPhraser 存角色 id 时只去 v 前缀不去 c（"c165241"），kungal 侧 ExtractLinkIds 已去 c。</summary>
    private static string? NormalizeVndbCharId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return id.StartsWith("c") || id.StartsWith("v") ? id[1..] : id;
    }

    /// <summary>三层匹配：gid 记忆 → vndb_id 搜索 → 名称搜索</summary>
    private async Task<int?> MatchGidAsync(Galgame galgame)
    {
        // ① gid 记忆（IdForPlugins 随游戏持久化；原生设置页「使用游戏ID从数据源更新数据」在
        //    RssType=kungal 时 Gal.Id 属性路由到同一槽位——按 ID 更新即命中此层）
        if (galgame.IdForPlugins.GetValueOrDefault(ParserId) is { } remembered &&
            int.TryParse(remembered, out int gid) && gid > 0)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                $"kungal 按 ID 直连: gid={gid} ({(galgame.RssType == (RssType)ParserId ? "设置页按 ID 更新" : "历史记忆")})");
            return gid;
        }

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
