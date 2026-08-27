using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using GalgameManager.Models; // 注意：Galgame 类的程序集是 WinApp.Base，但命名空间是 GalgameManager.Models
using GalgameManager.Models.Sources;
using GalgameManager.WinApp.Base.Models.Msgs;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 启动守卫：防止「只点开测试一下」的游戏被顶到原生主页「最后游玩」排序的最前面。
///
/// 背景（宿主源码实锤）：详情页点「开始游戏」时宿主无条件执行 Item.LastPlayTime = DateTime.Now，
/// 哪怕游戏 1 秒就关掉也会刷时间戳；而原生主页按 LastPlayTime 属性排序，于是纯测试打开会污染排序。
/// 原生页排序只认 Galgame.LastPlayTime 属性值本身，所以守卫策略是：
/// 订阅公开事件 GalPropertyChanged 观察 LastPlayTime 跳变 → 会话结束后若真实游玩累计
/// （TotalPlayTime 分钟增量）没达到阈值 → 把 LastPlayTime 还原成跳变前的值；达标则放行不动。
///
/// 「会话已结束」的判定优先用宿主官方钩子：开始游玩/停止游玩时宿主经 WeakReferenceMessenger 广播
/// GalgamePlayedMessage / GalgameStoppedMessage（都是 Base 公开消息类型）。收到「停止」时本次时长
/// 已统计入库，立即出结论（达标放行 / 不足还原），正常路径零等待。
///
/// 轮询兜底只覆盖收不到消息的异常会话（如未捕获到游戏进程、宿主崩溃），用双通道活性信号：
/// ① TotalPlayTime 每分钟滴答（INPC PropertyChanged）；② ProcessName 进程可见。任一出现都刷新
/// 活性时刻，连续安静超过 QuietGrace（2 分钟）才允许下「试玩结束」的结论。
///
/// 不删除任何游玩数据，可随时关闭回到原生行为。
///
/// 已知边界：
/// · Steam 源游戏不守卫——SteamService 刷新会用 Steam 服务器的最后游玩时间覆盖本地，
///   那个时间本身就只统计真实运行，没有污染问题；
/// · 守卫期间用户手动在对话框把「上次游玩」改成现在附近、且该游戏本轮累计不足阈值 → 可能被误还原
///   （改历史日期不受影响），极端场景接受现状。
/// </summary>
internal static class LaunchGuardHelper
{
    /// <summary>观察轮询间隔：TotalPlayTime 是宿主每分钟 +1 的，30 秒粒度足够</summary>
    private const int PollSeconds = 30;

    /// <summary>
    /// 安静宽限期：连续这么久没有任何活性证据（时长滴答/进程可见）才允许下「试玩结束」结论。
    /// 进程探测不可靠（游戏未配置过 ProcessName、exe 改名、启动器接管时都探不到），
    /// 只凭「进程不在」就还原会误杀正常游玩（v1 实测 bug），必须以双通道+宽限期判定。
    /// </summary>
    private static readonly TimeSpan QuietGrace = TimeSpan.FromMinutes(2);

    /// <summary>绝对放弃期限：超过此时长仍未形成判定则放行（不给结论也不能永远吊着）</summary>
    private static readonly TimeSpan MaxObserveDuration = TimeSpan.FromHours(6);

    private sealed class GuardState
    {
        public required Galgame Game;
        public DateTime PreviousLastPlay;      // 跳变前的 LastPlayTime（还原目标）
        public int TotalPlayTimeAtBump;        // 跳变时刻的 TotalPlayTime 快照（分钟）
        public DateTime BumpedAt;              // 最近一次跳变的墙钟时间
        public DateTime LastActivitySeen;      // 最近一次活性证据时刻（时长滴答或进程可见）
        public Timer? Timer;
    }

    private static readonly object Lock = new();
    /** 待观察状态（有 LastPlayTime 跳变待判定的游戏） */
    private static readonly Dictionary<Guid, GuardState> Pendings = new();
    /** 已订阅 GalPropertyChanged 的游戏（防重复订阅） */
    private static readonly HashSet<Guid> Subscribed = new();
    /** 我们所知的各游戏当前 LastPlayTime（判断跳变方向 & 防自还原来回震荡） */
    private static readonly Dictionary<Guid, DateTime> LastKnown = new();
    private static bool _initialized;
    /** 初始化重试：插件装载时宿主库可能尚未从 LiteDB 加载完（GetAllGames 为空），轮询直到非空或放弃 */
    private static Timer? _initRetry;
    private const int MaxInitRetries = 100; // 3 秒一轮 ×100 ≈ 5 分钟，足够覆盖慢盘冷启动

    public static void Initialize()
    {
        lock (Lock)
        {
            if (_initialized) return;
            _initialized = true;
            foreach (Galgame game in GetGamesSafe())
                Watch(game);
            if (Subscribed.Count == 0)
                _initRetry = new Timer(_ => RetryInitTick(), null,
                    TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }
        HookMessages();
        Log($"守卫: 初始化完成，当前监听 {CountLocked()} 款游戏" +
            (_initRetry != null ? "（游戏库暂未加载完，自动重试中）" : ""));
    }

    // ===== 宿主官方钩子：开始/停止游玩广播消息（WeakReferenceMessenger，Base 公开消息类型） =====

    /// <summary>弱引用表的 recipient 根对象：静态持有保活，否则弱表会回收掉处理器</summary>
    private static readonly object MsgRecipient = new();
    private static bool _msgHooked;

    private static void HookMessages()
    {
        if (_msgHooked) return;
        try
        {
            // 先验证宿主 DI 的 IMessenger 与插件侧 WeakReferenceMessenger.Default 是同一实例
            //（两侧共享 CommunityToolkit.Mvvm 程序集时成立）；不同则放弃快速路径，只靠轮询兜底
            Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "GalgameManager");
            MethodInfo? getService = host?.GetType("GalgameManager.App")
                ?.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static);
            object? hostMessenger = getService?.MakeGenericMethod(typeof(IMessenger)).Invoke(null, null);
            if (!ReferenceEquals(hostMessenger, WeakReferenceMessenger.Default))
            {
                Log("守卫: 宿主与插件的 Messenger 不是同一实例，跳过消息钩子（仅轮询兜底）");
                return;
            }
            WeakReferenceMessenger.Default.Register<GalgamePlayedMessage>(MsgRecipient,
                (_, m) => OnGamePlayed(m.Value));
            WeakReferenceMessenger.Default.Register<GalgameStoppedMessage>(MsgRecipient,
                (_, m) => OnGameStopped(m.Value));
            _msgHooked = true;
            Log("守卫: 已挂载开始/停止游玩消息钩子");
        }
        catch (Exception ex)
        {
            Log($"守卫: 消息钩子挂载失败（仅轮询兜底）: {ex.Message}");
        }
    }

    /// <summary>开始游玩：此刻宿主已完成 LastPlayTime 跳变——重复进入既有判定分支是幂等的；
    /// 事件订阅竞态漏掉的启动也能由此补上</summary>
    private static void OnGamePlayed(Galgame? game)
    {
        if (game is null) return;
        try { HandleLastPlayTimeChange(game); }
        catch { /* 不外抛到 Messenger 派发链 */ }
    }

    /// <summary>停止游玩：宿主在统计完本次时长并保存后才广播——立即出结论，正常路径零等待</summary>
    private static void OnGameStopped(Galgame? game)
    {
        if (game is null) return;
        try
        {
            var thresholdMin = Math.Max(1, Plugin.Data.LaunchGuardThresholdMinutes);
            int gained;
            lock (Lock)
            {
                if (!Pendings.TryGetValue(game.Uuid, out GuardState? state)) return;
                state.LastActivitySeen = DateTime.Now;
                gained = game.TotalPlayTime - state.TotalPlayTimeAtBump;
            }
            if (!Plugin.Data.LaunchGuardEnabled)
            {
                Finish(game.Uuid, reverted: false, reason: "守卫已关闭");
                return;
            }
            if (gained >= thresholdMin)
                Finish(game.Uuid, reverted: false, reason: $"真玩放行（停止消息，本轮 {gained} 分钟）");
            else
                Finish(game.Uuid, reverted: true, reason: $"试玩还原（停止消息，本轮 {gained}/{thresholdMin} 分钟）");
        }
        catch
        {
            // 不外抛到 Messenger 派发链
        }
    }

    private static int CountLocked()
    {
        lock (Lock) return Subscribed.Count;
    }

    private static void RetryInitTick()
    {
        try
        {
            foreach (Galgame game in GetGamesSafe())
            {
                lock (Lock) Watch(game);
            }
        }
        catch
        {
            // 宿主服务未就绪等异常，下轮再试
        }

        lock (Lock)
        {
            if (Subscribed.Count == 0 && --_retriesLeft > 0) return; // 库仍是空的，继续等
            try { _initRetry?.Dispose(); } catch { /* 已释放 */ }
            _initRetry = null;
        }
        if (CountLocked() > 0)
            Log($"守卫: 游戏库已就绪，共监听 {CountLocked()} 款游戏");
    }

    private static int _retriesLeft = MaxInitRetries;

    private static List<Galgame> GetGamesSafe()
    {
        try
        {
            return Plugin.HostApi.GetAllGames();
        }
        catch
        {
            return []; // 宿主集合服务尚不可用
        }
    }

    public static void Uninitialize()
    {
        if (_msgHooked)
        {
            try { WeakReferenceMessenger.Default.UnregisterAll(MsgRecipient); } catch { /* 已回收 */ }
            _msgHooked = false;
        }
        lock (Lock)
        {
            foreach (GuardState s in Pendings.Values)
                try { s.Timer?.Dispose(); } catch { /* 已销毁 */ }
            Pendings.Clear();
            LastKnown.Clear();
            Subscribed.Clear();
            try { _initRetry?.Dispose(); } catch { /* 已释放 */ }
            _initRetry = null;
            _initialized = false;
        }
    }

    private static void Watch(Galgame game)
    {
        if (!Subscribed.Add(game.Uuid)) return;
        LastKnown[game.Uuid] = game.LastPlayTime;
        // 公开事件，签名 Action<Galgame, string, object?>（WinApp.Base/Galgame.cs）
        game.GalPropertyChanged += OnGamePropertyChanged;
        // 标准 INPC 事件：宿主游玩计时任务每分钟 TotalPlayTime++ 都会触发——
        // 这是「游戏正在被真实游玩」的最可靠活性信号（不依赖 ProcessName 配置正确）
        game.PropertyChanged += OnInpcPropertyChanged;
    }

    /// <summary>宿主 GalgameAddedEvent 回调（HostServices 反射订阅）：新入库游戏立即纳入守卫</summary>
    public static void WatchNewGame(Galgame game)
    {
        try
        {
            lock (Lock) Watch(game);
            Plugin.HostApi.Log(InfoBarSeverity.Informational, $"守卫: 新游戏「{game.Name.Value}」已纳入监听");
        }
        catch
        {
            // 事件回调里绝不能抛
        }
    }

    private static void OnGamePropertyChanged(Galgame game, string property, object? value)
    {
        if (!string.Equals(property, nameof(Galgame.LastPlayTime), StringComparison.Ordinal)) return;
        try
        {
            HandleLastPlayTimeChange(game);
        }
        catch
        {
            // 守卫自身异常绝不能外抛到宿主事件链
        }
    }

    /// <summary>时长滴答：真玩期间宿主每分钟 TotalPlayTime++ 触发，刷新观察状态活性并顺带评估能否提前放行</summary>
    private static void OnInpcPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(Galgame.TotalPlayTime), StringComparison.Ordinal)) return;
        if (sender is not Galgame game) return;
        try
        {
            var thresholdMin = Math.Max(1, Plugin.Data.LaunchGuardThresholdMinutes);
            int gained;
            lock (Lock)
            {
                if (!Pendings.TryGetValue(game.Uuid, out GuardState? state)) return;
                state.LastActivitySeen = DateTime.Now;
                gained = game.TotalPlayTime - state.TotalPlayTimeAtBump;
            }
            // 锁外收尾（Finish 内部会再拿锁，嵌套调用同一把锁虽是同线程可重入，保持简洁不依赖它）
            if (gained >= thresholdMin)
                Finish(game.Uuid, reverted: false,
                    reason: $"真玩放行（时长滴答 {gained} 分钟 ≥ 阈值 {thresholdMin}）");
        }
        catch
        {
            // 不外抛到宿主事件链
        }
    }

    private static void HandleLastPlayTimeChange(Galgame game)
    {
        DateTime now = game.LastPlayTime;
        DateTime known;
        lock (Lock) LastKnown.TryGetValue(game.Uuid, out known);

        // 认知表无条件跟进（所有后续跳变方向判断都依赖它是最新的）
        lock (Lock) LastKnown[game.Uuid] = now;

        // 值往回走（还原写入 / 手动改小 / 清除工具归零）= 无污染风险
        if (now <= known) return;

        // 关闭状态 / Steam 源 / 已经认真玩过的游戏：放行不做观察。
        // 「认真玩过」= 累计总时长已达阈值——守卫防的是「新游戏随手测一下」，而久别重逢的老游戏
        // 回来玩几分钟是完全正常的回访，更新上次游玩时间正是用户预期（v1.5 初版漏了这条，
        // 曾把总时长 14 分钟的老游戏当试玩还原）；阈值动态读取，切大档会把更多老游戏纳入豁免。
        var thresholdMin = Math.Max(1, Plugin.Data.LaunchGuardThresholdMinutes);
        if (!Plugin.Data.LaunchGuardEnabled || !IsGuardable(game)
            || game.TotalPlayTime >= thresholdMin
            || now - DateTime.Now > TimeSpan.FromMinutes(2))
            return;

        StartPending(game, known); // 还原基线必须用更新认知表之前的旧值
    }

    /// <summary>跳变是否值得观察：非 Steam 源才守卫（Steam 刷新会用服务器真值覆盖，无污染）</summary>
    private static bool IsGuardable(Galgame game)
    {
        try
        {
            // Sources / GalgameSourceType 都是 WinApp.Base 公开类型，插件可直接访问
            return !game.Sources.Any(s => s.SourceType == GalgameSourceType.Steam);
        }
        catch
        {
            return false; // 读不到源信息就别多管
        }
    }

    private static void StartPending(Galgame game, DateTime previousKnown)
    {
        lock (Lock)
        {
            if (Pendings.TryGetValue(game.Uuid, out GuardState? existing))
            {
                // 观察期内的连续启动（测试中反复开关游戏）：保留最早的还原基线与计时快照——
                // 多次短开的时长应合并计算，反复重置基线会把每轮都变成"零增长"导致误还原
                existing.BumpedAt = DateTime.Now;
                RestartTimer(existing);
                return;
            }

            GuardState state = new()
            {
                Game = game,
                PreviousLastPlay = previousKnown,
                TotalPlayTimeAtBump = game.TotalPlayTime,
                BumpedAt = DateTime.Now,
                LastActivitySeen = DateTime.Now, // 跳变本身视作首次活性，宽限期由此起算
            };
            state.Timer = new Timer(_ => Tick(state), null, TimeSpan.FromSeconds(PollSeconds),
                TimeSpan.FromSeconds(PollSeconds));
            Pendings[game.Uuid] = state;
            LastKnown[game.Uuid] = game.LastPlayTime;
        }
        Log($"守卫: 观察「{game.Name.Value}」 上次游玩基线={previousKnown:yyyy-MM-dd HH:mm}");
    }

    private static void RestartTimer(GuardState state)
    {
        try { state.Timer?.Change(TimeSpan.FromSeconds(PollSeconds), TimeSpan.FromSeconds(PollSeconds)); }
        catch { /* 对象已释放 */ }
    }

    private static void Tick(GuardState state)
    {
        try
        {
            Galgame game = state.Game;
            var thresholdMin = Math.Max(1, Plugin.Data.LaunchGuardThresholdMinutes);
            int gained = game.TotalPlayTime - state.TotalPlayTimeAtBump;

            // 中途关掉守卫：放行（保留本次跳变，等价原生行为）
            if (!Plugin.Data.LaunchGuardEnabled)
            {
                Finish(game.Uuid, reverted: false, reason: "守卫已关闭");
                return;
            }

            // 真玩：累计增量达到阈值 → 放行；超时未决也放行（证据不足不误伤）
            if (gained >= thresholdMin || DateTime.Now - state.BumpedAt > MaxObserveDuration)
            {
                Finish(game.Uuid, reverted: false,
                    reason: $"真玩（{gained} 分钟 ≥ 阈值 {thresholdMin}）或观察超时");
                return;
            }

            // 活性双通道：进程可见 / 时长滴答（OnInpcPropertyChanged 已在滴答时刷新 LastActivitySeen）。
            // 进程探测到也刷新活性——它不是必要条件，只是证据之一。
            if (IsProcessAlive(game))
            {
                lock (Lock) state.LastActivitySeen = DateTime.Now;
                return; // 进程还跑着，继续观察
            }

            // 安静未超宽限期：给慢启动的游戏和偶尔探测不到的进程留余地，继续观察
            TimeSpan quietFor = DateTime.Now - state.LastActivitySeen;
            if (quietFor < QuietGrace) return;

            // 连续安静超过宽限期且进程不在 → 会话已结束，按时长判定
            if (gained >= thresholdMin)
                Finish(game.Uuid, reverted: false, reason: $"真玩（{gained} 分钟 ≥ 阈值 {thresholdMin}）");
            else
                Finish(game.Uuid, reverted: true,
                    reason: $"仅试玩 {gained}/{thresholdMin} 分钟，安静 {quietFor.Minutes} 分钟无活性");
        }
        catch
        {
            Finish(state.Game.Uuid, reverted: false, reason: "判定流程异常"); // 出错宁可放行也不要卡死/误伤
        }
    }

    /// <summary>
    /// 进程探测只是活性证据之一（不可靠：未配置 ProcessName / exe 改名 / 启动器接管时探不到），
    /// 判定主体是 LastActivitySeen 的安静时长，此处返回 false 不代表「没在玩」。
    /// </summary>
    private static bool IsProcessAlive(Galgame game)
    {
        string? name = game.ProcessName;
        if (string.IsNullOrWhiteSpace(name)) return false; // 无名可查 → 交给滴答信号与宽限期
        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Finish(Guid uuid, bool reverted, string reason)
    {
        GuardState? state;
        lock (Lock)
        {
            if (!Pendings.Remove(uuid, out state)) return;
        }
        try { state.Timer?.Dispose(); } catch { /* 已销毁 */ }

        string name = state.Game.Name.Value ?? "";
        Log($"守卫: {(reverted ? "还原" : "放行")}「{name}」（{reason}）");
        if (!reverted) return;

        // 还原必须可见可查：明细进全局 InfoBar（判定路径/本轮分钟数），否则出了分歧无从定位
        try
        {
            Plugin.HostApi.Info(InfoBarSeverity.Informational, title: "试玩守卫",
                msg: $"已还原《{name}》的上次游玩时间（{reason}）");
        }
        catch
        {
            // 提示失败不影响还原本身
        }

        Galgame game = state.Game;
        // 游戏可能已被移出库：移库后 Upsert 会把它整条复活进数据库，必须复核
        if (GetGamesSafe().All(g => g.Uuid != uuid))
        {
            Log($"守卫:「{game.Name.Value}」已不在库中，跳过还原");
            return;
        }
        Plugin.HostApi.InvokeOnMainThread(() =>
        {
            try
            {
                game.LastPlayTime = state.PreviousLastPlay; // 触发的事件走"往回走"分支，天然无重入
                _ = HostServices.SaveGameAsync(game);       // 总分/总时长/PlayedTime 不动，还原纯改时间戳
            }
            catch
            {
                // 还原失败保持跳变值（等同原生行为），不影响后续观察
            }
        });
    }

    /// <summary>清除工具清零记录后调用：丢弃待观察状态并同步认知，避免守卫拿旧基线又把归零值"还原"回去</summary>
    public static void OnRecordsCleared(IEnumerable<Galgame> games)
    {
        foreach (Galgame g in games)
        {
            Finish(g.Uuid, reverted: false, reason: "记录已被清除工具清零");
            lock (Lock) LastKnown[g.Uuid] = g.LastPlayTime;
        }
    }

    private static void Log(string msg)
    {
        try { Plugin.HostApi.Log(InfoBarSeverity.Informational, msg); } catch { /* 日志失败不影响功能 */ }
    }
}
