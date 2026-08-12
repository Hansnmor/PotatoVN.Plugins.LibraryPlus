# PotatoVN kungal 数据源插件功能设计（开工前总结）

> 本文档是 LibraryPlus 插件新增「kungal 搜刮」功能的开工蓝图。
> 所有结论均经过源码阅读 + 线上 API 实测验证；「待验证」标注项为开工时第一个要确认的点。
> 讨论日期：2026-08-12

---

## 1. 目标与形态

**核心目标**：解决 PotatoVN 原生源中文简介缺失的问题，并顺带升级 LibraryPlus 的萌作/剧情作/拔作分类系统。

**形态决策（重要）**：**不新建插件**，把能力并入现有 LibraryPlus（一个插件同时实现 `IPlugin` + `IParserProvider`，官方支持）。原因：
- 共享 LibraryPlus 已有的页面/PluginData 持久化/反射基建
- 用户只需装一个插件
- "更多搜刮 → 分类升级"形成闭环：搜刮越充分，投票分类越准

**双通道使用形态**：
1. **原生通道**（免费获得）：插件注册为搜刮器后，原生"设置页→默认搜刮源"下拉（`SettingsPage.xaml:214`）和"游戏设置页→每游戏源选择"（`GalgameSettingViewModel.cs:70`）自动出现 kungal 源。单游戏搜刮走宿主原生全流程（`ParseGalInfoAsync → ParseAsync → ConfirmGalInfoDialog → SaveGalgameAsync`），插件零参与。
2. **插件通道**（LibraryPlus 新增）：扩展库页面"更多搜刮"批量处理 + 双轴分类 + 中文简介预览/应用。

**边界（已知且接受）**：kungal 不进"混合搜刮"流（`RssTypeHelper.UsablePhrasers` 在 Base 中硬编码 4 内置源，插件无法修改），kungal 是独立源，手动选择或设为默认源使用。

---

## 2. 数据源调研结论（kungal.com，全部实测）

### 2.1 API 基础

| 项 | 值 |
|---|---|
| Base URL | `https://www.kungal.com/api`（**不是** api.kungal.com，该域名不存在） |
| 响应信封 | `{"code":0,"message":"成功","data":...}`，`code!=0` 即失败 |
| 鉴权 | 全部 GET 匿名可读，无需 token |
| 限流 | 当前公开 GET 路由无限流中间件，但插件仍需节流（建议 200-500ms/请求）+ UA |
| 条目总量 | 7594（`/api/galgame` total 字段） |
| 图片 CDN | `https://image.kungal.iloverine.link/<hash前2位>/<前4位>/<hash>.webp`（可由 image_hash 推导） |

### 2.2 关键端点

| 端点 | 用途 | 备注 |
|---|---|---|
| `GET /api/galgame?page=&limit=` | 列表（limit max 50） | 匿名只返回 sfw，带 cookie 全量 |
| `GET /api/galgame/:gid` | 详情（约 60KB，含 staff/characters/ratings/tags） | **匿名可用，含 nsfw 条目** |
| `GET /api/search?keywords=&type=galgame&page=&limit=` | 游戏搜索（limit max 12） | **必须带 page 参数**，否则报 code:233 |
| `GET /api/galgame/:gid/link/all` | 外部链接（Steam/VNDB/Bangumi 等 URL） | — |
| `GET /api/galgame-tag(/-search/:id/multi)` | 标签列表/搜索/详情 | 匿名可用 |
| `GET /api/galgame-rating/all`、`/galgame-rating/:id` | 评分 | 匿名可读 |

### 2.3 详情字段（实测 gid=1/84/4971/4285/566）

- `id`（gid，int，唯一键）
- `vndb_id`（如 `v19658`）——**匹配锚点**
- `name`：四语言 `{en-us, ja-jp, zh-cn, zh-tw}`
- `introduction`：四语言简介，HTML + Markdown 双格式
- `banner` / `effective_banner_*`：封面
- `release_date` / `release_date_tba`（未定标记）
- `engine`：`[{id, name, alias, galgame_count}]`
- `official`：`[{id, name, link, category(game_brand…), roles(developer…), lang}]`
- `tag`：`[{id, name, category(content/meta), galgame_count, spoiler_level}]`
- `staff`：按职责分组 `[{role_key, role_name, people:[{id,name,latin}]}]`
- `characters`：`[{id, name, latin, kind, spoiler, image, figure, voices:[{id,name}]}]`
- `covers` / `screenshots`：`[{image_hash, cdn_url, width, height, sexual, violence, …}]`
- `ratings`：`[{user, recommend, overall, art, story, music, character, route, system, voice, replay_value, play_status, galgame_type, …}]`
- `alias`、`series`、`contributor`、`content_limit`、`age_limit`、`original_language`、`platform`

**⚠️ 纠正（实测推翻子代理报告）**：detail 的 `type` 字段是**资源类型**（`['game','collection','voice','image']`），**不是**题材分类。列表接口的 `game_type` 过滤参数（`all/ba_saku/plot/moe/daily/uncategorized`）疑似题材，**未验证，不作为信号源**。

### 2.4 R18 过滤（实测矩阵）

两个独立字段：`age_limit`（游戏自身分级，**不参与过滤**）与 `content_limit`（站点展示分级 sfw/nsfw，**唯一过滤维度**）。

| 操作 | 匿名 | 带 cookie `KUNGalgameSettings={"showKUNGalgameContentLimit":"all"}` |
|---|---|---|
| 列表 `/api/galgame` | 只返回 sfw（实测前50条: 32 sfw） | sfw+nsfw 全量（32+18） |
| 搜索 `/api/search`（vndb_id/中文名/日文名） | **nsfw 也能搜到**（实测 gid=4971 三种搜法全中） | 同样 |
| 直连详情 `/api/galgame/:gid` | **nsfw 完整数据照拿** | 同样 |

**结论**：R18 不构成障碍。保险措施：插件所有请求统一带该 cookie（防御站点未来把过滤扩展到搜索/详情）。

### 2.5 数据覆盖范围（实测推翻"只收 ADV"假设）

kungal **收录非传统 ADV**：
- SLG：兰斯07 战国兰斯（gid=566，engine=`AliceSoft System4.X`，tag 直接含"SLG"）
- 同人/RPG：勇者大战魔物娘三章整合（gid=4285，engine=`Ren'Py`，有 vndb_id v11849）

→ **"kungal 无数据 → 自动判同人"方案否决**（目录覆盖不完整 ≠ 类型；且反方向不成立：同人作也在 kungal 里）。kungal 无数据只触发 fallback。

---

## 3. 数据面对比（kungal vs 原生四源）

### 3.1 原生混合搜刮的 tag 合并逻辑（源码确认）

- **不是合并，是单源优先整组替换**：`MixedPhraser.GetValue`（`MixedPhraser.cs:228-231`）按 TagsOrder 取第一个 tag 数>0 的源
- 中文默认顺序：**Bangumi → VNDB → Steam**（`MixedPhraser.cs:424`）
- **Ymgal 不在 TagsOrder 且不填 Tags**（源码无 Tags 赋值）——对 tag 零贡献
- VNDB tag 有单游戏投票（Rating），排序用了但**存进 Galgame.Tags 时投票数被丢弃**
- ⚠️ DEVELOPMENT_GUIDE §5"VNDB 翻译 + Bangumi 混合"是早期版本描述，当前源码严格单源

### 3.2 kungal vs 原生（字段级）

| 数据项 | VNDB | Bangumi | Ymgal | Steam | kungal | 结论 |
|---|:---:|:---:|:---:|:---:|:---:|---|
| 中文简介 | ⚠️ | ✅不全 | ✅不全 | ✅部分 | ✅✅四语言 | **kungal 最强** |
| 中文名 | ⚠️极少 | ✅ | ✅ | ✅ | ✅含繁中 | 持平 |
| 制作商 | ✅ | ✅ | ✅ | ❌ | ✅带分类 | 持平 |
| 引擎 | ✅ | ❌ | ❌ | ❌ | ✅ | 持平 |
| 预计时长 | ✅唯一 | ❌ | ❌ | ❌ | ❌ | **VNDB 独有** |
| 发售日 | ✅ | ✅ | ✅ | ❌ | ✅含未定标记 | 持平 |
| 评分 | ✅ | ✅ | ❌ | ❌ | ✅**多维** | kungal 更强 |
| 标签 | ✅英文 | ✅中文 | ❌ | ✅ | ✅✅中文结构化 | **kungal 最强** |
| 头图 | ✅ | ❌ | ❌ | ✅ | ❌ | Steam 独有 |
| 角色 | ✅细节全 | ✅细节全 | ✅ | ❌ | ⚠️字段最少 | **VNDB/Bangumi 更强** |
| Staff | ✅ | ✅ | ✅ | ❌ | ✅按职责分组 | 持平 |
| 系列/别名 | ❌ | ⚠️ | ❌ | ❌ | ✅ | kungal 独有（模型无字段） |

**角色数据决策**：kungal 角色只有 7 字段（name/latin/kind/spoiler/image/figure/voices），VNDB/Bangumi 10+ 字段（简介/性别/身高/三围/血型/生日）；且 PotatoVN `GalgameCharacter` 模型**无声优字段**，kungal 的 voices/kind/spoiler **无处安放** → **插件不碰角色数据**，保持原生源供给。

---

## 4. 插件架构设计

### 4.1 总体

```
LibraryPlus（现有工程）
├── Plugin.cs            ← 增加 IParserProvider 接口实现
├── KungalClient.cs      ← 新：kungal API 客户端（HttpClient + JSON）
├── KungalPhraser.cs     ← 新：IGalInfoPhraser 实现（宿主注册用）
├── Controls/SortPage    ← 增加"更多搜刮"批量入口 + 结果面板 + 搜刮范围开关
├── Models/PluginData    ← 增加 kungal 数据字典（gameUuid → KungalGameData）
├── HostServices.cs      ← 增加 SaveGalgame 反射方法
└── Helper/GalgameClassifier.cs  ← 升级为双轴分类器（内容轴投票 + 形态轴证据）
```

### 4.2 字段控制（核心机制，源码确认）

宿主 `ParseAsync`（`GalgameCollectionService.cs:398+`）合并规则——**插件返回什么就写什么**：

| 字段 | 宿主行为 | "仅简介+标签"模式策略 |
|---|---|---|
| Description | 无条件覆盖 | 填 kungal 中文简介；搜不到**回传原值** |
| Tags | 条件（count>0 且未锁） | 填 kungal content 类 tag |
| Developer/Engine/ExpectedPlayTime | 条件（≠DefaultString） | 留默认值 → 宿主跳过 |
| Rating/ReleaseDate/ChineseName/OriginalName | **无条件覆盖** | **必须回传原值**，否则被清 |
| Characters | **无条件替换** | 必须回传原集合引用 |
| ImageUrl | **无条件替换** | 必须回传原值 |

**设计**：phraser 实例持有 `ScrapeFields` 配置（标志位枚举），**默认 `Description|Tags`**，可选全量；`GetGalgameInfo` 每次调用读当前配置决定填什么。官方文档明确允许改 phraser 实例（`IParserProvider` 注释）。扩展库页提供开关（全局默认 + 每游戏覆盖存 PluginData）。

**补充保险**：`LockableProperty.Value` setter 带 IsLock 保护（`if (IsLock) return;`），用户锁定的字段宿主覆盖不了（原生详情页是否有锁 UI 待验证）。

### 4.3 匹配策略（三层）

1. **vndb_id 搜索**（主路径）：`/api/search?keywords=<vndb_id>&type=galgame`（实测匿名可搜 nsfw）
2. **gid 记忆**：首次匹配成功存 `Galgame.IdForPlugins`（`Ids` 索引器对 RssType≥100 自动路由到该字典，随游戏持久化），二次搜刮零成本直连详情
3. **标题搜索兜底**：中文/日文名搜索 + 宿主 `IGalInfoPhraser.Similarity`（Jaro-Winkler）校验

### 4.4 扩展库页"更多搜刮"交互（草案）

- 批量选择游戏 → 逐游戏走匹配流程（节流 200-500ms）→ 结果存 PluginData
- 结果面板：中文简介预览 + "应用"按钮（反射 `SaveGalgameAsync`；仅应用"当前简介为空或非中文"的游戏需用户确认，或列表勾选）
- 搜刮范围开关：简介+标签（默认）/ 全量

### 4.5 已知原生小瑕疵（记录不解决）

游戏设置页 `SearchUri`（搜索链接）映射表是内置源写死的，kungal 无对应条目，选中时 fallback 默认链接——不影响搜刮功能。

---

## 5. 分类系统升级：方案 B（双轴）

**背景**：现有 `GalgameClassifier` v4.7 关键词规则（萌/剧情/拔/同人/其他 五分类）过于死板；"同人作"是制作状态轴与内容轴混搭的异类，且"无 VNDB → 同人"是缺席推断（v3 误伤夜羊社/秽翼的历史教训）。

**新分类体系（双轴正交）**：
- **内容轴**：萌作 / 剧情作 / 拔作 / 其他
- **形态轴**：传统ADV / 非传统ADV（正面证据：tag 类型词 SLG/RPG/模拟… + 引擎）

**"同人作"退役**：被双轴吸收（同人 ADV 按内容归类，同人 RPG/SLG 归形态轴）。可选附加小旗子"同人"（置信度标记，不参与主分类，待定）。

### 5.1 信号强度（三层）

```
① ratings[].galgame_type 聚合投票   ← 真·每游戏投票（用户评分时勾选的类型）
② kungal content-tag 热度加权打分   ← 主力（galgame_count 为标签可信度权重）
③ 旧 GalgameClassifier v4.7 规则    ← kungal 无数据时 fallback（含旧同人判定）
```

**⚠️ 语义澄清**：`galgame_count` 是**全站标签热度**（挂该标签的游戏数），不是某款游戏上的投票——它是"标签可信度先验"（500 款游戏在用的"悬疑"比 5 款用的词更可靠），不是"这款游戏是剧情作的票"。

### 5.2 数据卫生（必须处理）

1. **meta 类 tag 排除**：系统机制（分支剧情/立绘查看器/音乐欣赏）与题材无关，只看 `category=content`
2. **脏数据清洗**：实测有重复（哲学×2）、别名拼接超长 tag（"不小心漏出内裤/パンチラ/无意漏出内裤/…"）——词表匹配需归一化/前缀匹配
3. **词表映射继承 §5 经验库**（DEVELOPMENT_GUIDE）：强拔作词只放硬核 R18 行为词；角色属性词（萝莉/人妻/姐/妹）必须与显式拔作标签组合才构成强信号——只是从"规则顺序"换成"加权票数"
4. **剧透分级**：spoiler_level>0 的 tag 权重打折或排除
5. **投票权重公式**：① 与 ② 的融合、galgame_count 归一化——**开工前先定死，避免开发时凭感觉调参**

### 5.3 覆盖与降级

- 覆盖率依赖 kungal 条目（7594，冷门/新作可能无）→ 三层 fallback 是必须项
- 旧规则判同人 ∧ kungal 无数据 → 同人置信度加分（不硬判）

### 5.4 UI 影响（后期优化）

统计条同时显示内容轴分布 + 形态轴分布；筛选可交叉（如"非传统ADV ∧ 拔作"）。页面写法待设计阶段细化（用户已认可，可后期解决）。

---

## 6. 里程碑拆分

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **M0 验证**（开工第一步） | ratings 分页/全量确认；列表 game_type 参数语义；cookie 在 HttpClient 的效果；匹配正确率抽样（50 游戏） | 全部结论落档 |
| **M1 接入** | KungalClient + KungalPhraser + IParserProvider；默认简介+标签模式；原生源选择器出现 kungal | 原生单游戏搜刮出中文简介 |
| **M2 批量** | 扩展库"更多搜刮"；PluginData 存储；中文简介应用（含确认逻辑）；节流 | 批量搜刮 100 游戏不崩、不误清字段 |
| **M3 分类** | 双轴分类器（投票① + 热度② + fallback③）；统计/筛选双轴 UI | 与旧分类对比误判率下降 |
| **M4 发布** | Release 构建 → tag → GitHub Release 上传 `plugin.pvnplugin.zip` | 正式安装验证 |

---

## 7. 风险与约束清单

| 风险/约束 | 说明 | 应对 |
|---|---|---|
| kungal API 变动 | 社区站 v6 演进中（协作编辑机制新） | `code!=0` 即放弃返回 null；宿主优雅降级；解析失败不抛异常 |
| 限流 | 当前无公开限流，但需礼貌 | 节流 + UA |
| 混合流不含插件源 | Base 硬编码，插件无法改 | 接受，独立源定位 |
| 无条件覆盖字段 | 宿主行为不可改 | 回传原值保护（§4.2 表） |
| 简介被清空风险 | Description 无条件赋值 | 搜不到回传原简介 |
| 覆盖率 | 7594 条，冷门可能无 | 三层 fallback |
| 角色数据不碰 | kungal 角色字段少且模型无 voices | 保持原生源供给 |
| 原生 SearchUri 瑕疵 | 插件源无搜索链接映射 | 记录，不解决 |

---

## 8. 开发注意事项（LibraryPlus 经验复用）

- 改代码后**完全退出 PotatoVN（含托盘）再重启**，不用热重载（§3.4/§3.5）
- 插件 XAML 不写 `IsChecked` 字面量（§4.2）
- 线程调度统一 `IPotatoVnApi.InvokeOnMainThread`，禁用 DispatcherQueue（§4.1）
- 反射 Invoke 传全参、别静默吞错（§4.4/§4.6）
- 增量构建偶发 XAML 报错 → `rm -rf bin obj` 全量重建（§4.5）
- 宿主日志 `LocalState\Logs\log{yyyyMMdd}.txt`，排查按时间戳取最新
- 新功能优先走官方 API + 页面内数据，宿主内部能力没接口就诚实标注（§9）
