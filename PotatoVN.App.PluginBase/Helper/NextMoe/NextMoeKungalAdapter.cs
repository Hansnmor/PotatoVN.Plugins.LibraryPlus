using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PotatoVN.App.PluginBase.Helper.Kungal;
using PotatoVN.App.PluginBase.Helper.KungalWeb;

namespace PotatoVN.App.PluginBase.Helper.NextMoe;

/// <summary>
/// NextMoe → 旧 kungal 数据形状的适配层。
/// 目的：kungal 匿名 API 于 2026-09 起全部 401（{"code":205,"message":"用户登录失效"}），
/// 数据源整体迁移到 NextMoe 开放 API v2（work id 与旧 kungal gid 同值，已实测 work/1 = 枯れない世界と終わる花）。
/// 本类暴露与 <see cref="KungalClient"/> 相同的三个方法签名（SearchAsync / GetDetailAsync / GetCharacterAsync），
/// 让 <see cref="KungalPhraser"/> 的匹配与应用逻辑零改动换数据源。
/// 字段映射要点：
/// - work.display_name = 原名（多为日文），localized["zh-Hans"] = 简体中文名（可能缺）；
/// - tag_kind: content|meta；spoiler: none|minor|major → kungal 的 int spoiler_level 0/1/2；
/// - 角色 refs 里的 vndb/bangumi id 合成 URL 交给 KungalPhraser.ExtractLinkIds（vndb.org/c{id}、bgm.tv/character/{id}）；
/// - kungal 特有的用户「类型投票」（galgame_type → 分类用 TypeVotes）在 NextMoe 无对应数据，恒为空。
/// </summary>
internal class NextMoeKungalAdapter
{
    private readonly NextMoeClient _client = new();

    /// <summary>kungal 网站 SSR 抓取。详情数据的**主来源**（NextMoe API 只作回退）。</summary>
    private readonly KungalWebClient _webClient = new();

    /// <summary>
    /// 详情 include。只保留**被真正消费**的块（逐个核对过下游）：
    /// refs → VndbId 校验；intros → 简介；tags → 标签与分类热度；
    /// characters → 角色卡片（简介需再拉角色详情）；engines → Engine 字段与分类器；companies → 开发商。
    /// 刻意不带：ratings（适配出的 ExternalRatings 全插件无人读取）、playtimes（adapter 根本没映射）。
    /// </summary>
    private const string WorkInclude = "refs,intros,tags,characters,engines,companies";

    private const string CharacterInclude = "intros,refs,image,figure";

    /// <summary>
    /// 详情短缓存：同一款游戏在一次搜刮里会被拉两次（MatchGidAsync 的 vndb 校验 + FetchDetailAsync 取数），
    /// 命中缓存直接省掉一整个 RTT。批量重复搜刮同一款也受益。
    /// </summary>
    private readonly Dictionary<int, (KungalDetail Detail, DateTime At)> _detailCache = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly object _cacheLock = new();

    /// <summary>与 KungalClient.SearchAsync 同签名。keywords 为纯数字/v+数字时走 refs 精确反查（命中率高且省一次详情校验请求之外的开销）。</summary>
    public async Task<KungalSearchData?> SearchAsync(string keywords, int limit = 5)
    {
        string? vndbRef = ToVndbRef(keywords);
        if (vndbRef != null)
        {
            NextMoeList<NextMoeWork>? byRef = await _client.GetWorksByRefsAsync(vndbRef);
            if (byRef is not { Items.Count: > 0 }) return new KungalSearchData(); // 未命中 → 空集合，走下一变体/名称兜底
            List<KungalCard> cards = byRef.Items.Select(ToCard).ToList();
            // 反查命中唯一 → 服务端已精确匹配，标记可信（调用方免去一次全量详情校验）
            if (cards.Count == 1) cards[0].ExactRefMatch = true;
            return new KungalSearchData { Items = cards, Total = cards.Count };
        }

        NextMoeList<NextMoeSearchItem>? search = await _client.SearchWorksAsync(keywords, limit);
        if (search == null) return null;
        return new KungalSearchData
        {
            Items = search.Items.Where(i => i.TargetObject is null or "work").Select(ToCard).ToList(),
            Total = search.Total ?? search.Items.Count,
        };
    }

    /// <summary>
    /// 与 KungalClient.GetDetailAsync 同签名（gid = work id，与旧 kungal gid 同值）。
    /// **数据来源：kungal 网站 SSR 为主**（能拿到 NextMoe 缺的 tag spoiler_level 与用户类型投票
    /// game_types，且不需要登录）；NextMoe API 作回退（网页改版/不可用时至少还能搜刮）。
    /// </summary>
    public async Task<KungalDetail?> GetDetailAsync(int gid)
    {
        lock (_cacheLock)
        {
            if (_detailCache.TryGetValue(gid, out var hit))
            {
                if (DateTime.UtcNow - hit.At < CacheTtl) return hit.Detail;
                _detailCache.Remove(gid); // 过期
            }
        }

        KungalDetail? detail = await TryFetchFromWebAsync(gid);

        if (detail == null)
        {
            Plugin.HostApi?.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"kungal 网页数据不可用，回退 NextMoe API: gid={gid}" +
                "（本次将缺少 tag 剧透标记与用户类型投票）");
            NextMoeWork? work = await _client.GetWorkAsync(gid.ToString(), WorkInclude);
            if (work == null) return null;
            detail = ToDetail(work);
        }

        lock (_cacheLock)
        {
            if (_detailCache.Count > 200) _detailCache.Clear(); // 粗暴上限，防长会话无限增长
            _detailCache[gid] = (detail, DateTime.UtcNow);
        }
        return detail;
    }

    /// <summary>从 kungal 网站 SSR 取该作品的完整数据；失败返回 null（交给 NextMoe 回退）。</summary>
    private async Task<KungalDetail?> TryFetchFromWebAsync(int gid)
    {
        Dictionary<string, object?> pageData = await _webClient.FetchPageDataAsync(gid);
        if (pageData.Count == 0) return null;
        Dictionary<string, object?>? work = KungalWebMapper.PickWork(pageData, gid);
        if (work == null) return null;
        // work-ratings 是独立缓存项（用户评分列表，含 game_types → 类型投票）
        Dictionary<string, object?>? ratings = KungalWebClient.AsDict(
            pageData.GetValueOrDefault($"api:work-ratings:{gid}"));
        return KungalWebMapper.ToDetail(gid, work, ratings);
    }

    /// <summary>与 KungalClient.GetCharacterAsync 同签名（NextMoe character id）。</summary>
    public async Task<KungalCharacter?> GetCharacterAsync(int charId)
    {
        NextMoeCharacter? character = await _client.GetCharacterAsync(charId.ToString(), CharacterInclude);
        return character == null ? null : ToCharacter(character);
    }

    /// <summary>批量车道的单次 id 上限（服务端硬上限，超了是 400 TOO_MANY_IDS，自己分片）。</summary>
    private const int MaxIdsPerBatch = 100;

    /// <summary>
    /// 批量拉角色详情，消灭 N+1。替代「N 个角色各发一次 /characters/{id}」——
    /// 实测 20 个角色：批量水合 1 请求 / 0.77s / 15KB；逐个发则要 20 个请求（还要过令牌桶排队）。
    /// 找不到的 id 不会出现在返回字典里（对应 null）。
    /// </summary>
    public async Task<IReadOnlyDictionary<int, KungalCharacter>> GetCharactersAsync(IEnumerable<int> charIds)
    {
        var result = new Dictionary<int, KungalCharacter>();
        foreach (int[] chunk in charIds.Distinct().Chunk(MaxIdsPerBatch))
        {
            NextMoeList<NextMoeCharacter>? list =
                await _client.GetCharactersByIdsAsync(chunk, CharacterInclude);
            foreach (NextMoeCharacter c in list?.Items ?? new List<NextMoeCharacter>())
            {
                int id = ParseId(c.Id);
                if (id != 0) result[id] = ToCharacter(c);
            }
        }
        return result;
    }

    // ===== 映射 =====

    private static KungalCard ToCard(NextMoeSearchItem item)
    {
        string? zh = PickZh(item.Localized);
        return new KungalCard
        {
            Id = ParseId(item.Id),
            Name = zh ?? (KungalPhraser.IsChinese(item.DisplayName) ? item.DisplayName : null),
            NameOriginal = item.DisplayName,
        };
    }

    private static KungalCard ToCard(NextMoeWork work)
    {
        string? zh = PickZh(work.Localized);
        return new KungalCard
        {
            Id = ParseId(work.Id),
            Name = zh ?? (KungalPhraser.IsChinese(work.DisplayName) ? work.DisplayName : null),
            NameOriginal = work.DisplayName,
        };
    }

    private static KungalDetail ToDetail(NextMoeWork work)
    {
        string? zh = PickZh(work.Localized);
        return new KungalDetail
        {
            Id = ParseId(work.Id),
            VndbId = work.Refs?.FirstOrDefault(r =>
                r.Source?.Equals("vndb", StringComparison.OrdinalIgnoreCase) == true &&
                r.ExternalId != null && Regex.IsMatch(r.ExternalId, @"^v\d+$"))?.ExternalId,
            Name = zh ?? (KungalPhraser.IsChinese(work.DisplayName) ? work.DisplayName : null),
            NameOriginal = work.DisplayName,
            Introduction = (work.Intros ?? new()).Select(i => new KungalDetailIntro
            {
                Lang = i.Lang,
                Intro = i.Value ?? i.Intro,
                Machine = i.IsMachine,
            }).ToList(),
            ReleaseDate = work.ReleaseDate,
            Tag = (work.Tags ?? new()).Select(t => new KungalTag
            {
                Name = t.DisplayName ?? "",
                // is_sexual=true 的 tag 在 kungal 里属于独立的 "sexual" 类；NextMoe 把它们归在 tag_kind=content 下、
                // 另用 is_sexual 布尔标记（实测「拔作」= content + is_sexual=true）。
                // 必须还原成 "sexual"：否则 CleanTags 的 `Category == "content"` 过滤会失效，
                // 体位/行为类性 tag 会被写进玩家的游戏标签列表（v1.4.6 是排除的）。
                // 与分类器兼容：它判的是 `Category is "content" or "sexual"`，两类都收，行为不变。
                Category = t.IsSexual == true ? "sexual" : (t.TagKind ?? ""),
                GalgameCount = t.WorkCount ?? 0,
                SpoilerLevel = SpoilerToInt(t.Spoiler),
            }).ToList(),
            Engine = (work.Engines ?? new()).Select(e => new KungalEngine
            {
                Id = ParseId(e.Id),
                Name = e.DisplayName,
            }).ToList(),
            Official = (work.Companies ?? new()).Select(c => new KungalOfficial
            {
                Name = c.DisplayName,
                Category = c.CompanyKind ?? "",
                Roles = string.IsNullOrWhiteSpace(c.AttributionRole)
                    ? new List<string>()
                    : new List<string> { c.AttributionRole },
            }).ToList(),
            // kungal 特有的用户类型投票（galgame_type）在 NextMoe 无对应数据：分类功能退化，
            // GalgameClassifier 的投票路径拿空（tags 路径不受影响）。
            Ratings = new List<KungalRating>(),
            Characters = MapCharacters(work.Characters),
            ExternalRatings = (work.Ratings ?? new()).Select(r => new KungalExternalRating
            {
                Source = r.Source,
                Score = r.Score,
                VoteCount = r.VoteCount ?? 0,
            }).ToList(),
        };
    }

    private static KungalCharacter ToCharacter(NextMoeCharacter c)
    {
        string? zh = PickZh(c.Localized);
        var links = new List<KungalCharLink>();
        foreach (NextMoeRef? r in c.Refs ?? new())
        {
            switch (r.Source?.ToLowerInvariant())
            {
                case "vndb" when r.ExternalId?.StartsWith("c") == true:
                    links.Add(new KungalCharLink { Source = "vndb", Url = $"https://vndb.org/{r.ExternalId}" });
                    break;
                case "bangumi" when Regex.IsMatch(r.ExternalId ?? "", @"^\d+$"):
                    links.Add(new KungalCharLink { Source = "bangumi", Url = $"https://bgm.tv/character/{r.ExternalId}" });
                    break;
            }
        }
        return new KungalCharacter
        {
            Id = ParseId(c.Id),
            // 中文名 = localized zh-Hans（NextMoe 直接内嵌，v1.4.6 的 bgm 网页抓取兜底仅在缺失时触发）
            Name = zh ?? (KungalPhraser.IsChinese(c.DisplayName) ? c.DisplayName : null),
            NameOriginal = c.DisplayName,
            NameJa = null,
            Image = c.Image?.Url,
            Figure = c.Figure?.Url,
            Intros = (c.Intros ?? new()).Select(i => new KungalCharIntro
            {
                Lang = i.Lang,
                Intro = i.Value ?? i.Intro,
                Machine = i.IsMachine,
            }).ToList(),
            Links = links,
        };
    }

    /// <summary>角色名归一化（去半角/全角空格）：仅用于判重与"是否等于原名"的比较。</summary>
    private static string NormKey(string? name) => (name ?? "").Replace(" ", "").Replace("　", "");

    /// <summary>
    /// zh-Hans 有值**且不等于原名**才算「真正的中文名」。
    /// NextMoe 对没有译名的角色会把 zh-Hans 回退成原名（实测 gid=756 的「島田 辰子」「加鳥周平」），
    /// 若不加这层判断，日文名会被当成中文名写进中文字段。
    /// </summary>
    private static bool HasRealChineseName(NextMoeCharacter c)
    {
        string? zh = PickZh(c.Localized);
        return !string.IsNullOrWhiteSpace(zh) && NormKey(zh) != NormKey(c.DisplayName);
    }

    /// <summary>
    /// 角色卡片映射 + **按名去重**。
    /// NextMoe 常对同一角色返回两条：一条带 localized.zh-Hans 中文名、另一条只有原名
    /// （实测 gid=49：497「アンジェリーナ・菜夏・シーウェル→安洁莉娜·菜夏·史威尔」与
    ///  237990 同名条目无中文名；乾紗凪/瀬名愛理/瓜生桜乃 同样各有一对）。
    /// 不去重的话，没有中文名的那条会被判定为"无链接"，进而触发整轮 bgm 兜底往返
    /// （拉 bgm 角色列表 + 逐角色抓网页，实测吃掉 26 秒，而最终拿到的中文名与重复条目已有的完全一致）。
    /// 只在同组内确实存在「带真正中文名的条目」时才合并，避免误伤真正同名的不同角色。
    /// </summary>
    private static List<KungalCharCard> MapCharacters(List<NextMoeCharacter>? chars)
    {
        var result = new List<KungalCharCard>();
        if (chars is not { Count: > 0 }) return result;

        foreach (IGrouping<string, NextMoeCharacter> group in chars.GroupBy(c => NormKey(c.DisplayName)))
        {
            List<NextMoeCharacter> list = group.ToList();
            if (list.Count > 1)
            {
                List<NextMoeCharacter> withCn = list.Where(HasRealChineseName).ToList();
                if (withCn.Count > 0) list = new List<NextMoeCharacter> { withCn[0] };
            }
            foreach (NextMoeCharacter c in list)
                result.Add(new KungalCharCard
                {
                    Id = ParseId(c.Id),
                    Name = c.DisplayName,
                    Spoiler = SpoilerToInt(c.Spoiler),
                });
        }
        return result;
    }

    private static string? PickZh(Dictionary<string, NextMoeLocalizedValue>? localized)
    {
        if (localized == null) return null;
        if (localized.TryGetValue("zh-Hans", out NextMoeLocalizedValue? hans) &&
            !string.IsNullOrWhiteSpace(hans.Value)) return hans.Value.Trim();
        foreach ((string? key, NextMoeLocalizedValue v) in localized)
            if (key != null && key.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(v.Value)) return v.Value.Trim();
        return null;
    }

    private static int SpoilerToInt(string? spoiler) => spoiler switch
    {
        "minor" => 1,
        "major" => 2,
        _ => 0,
    };

    private static int ParseId(string? id)
    {
        return int.TryParse(id, out int v) ? v : 0;
    }

    /// <summary>「12345」/「v12345」→「vndb:v12345」（refs 反查格式）；其余返回 null（按名称搜索）。</summary>
    private static string? ToVndbRef(string? keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords)) return null;
        string k = keywords.Trim();
        if (Regex.IsMatch(k, @"^\d+$")) return "vndb:v" + k;
        if (Regex.IsMatch(k, @"^v\d+$")) return "vndb:" + k;
        return null;
    }
}
