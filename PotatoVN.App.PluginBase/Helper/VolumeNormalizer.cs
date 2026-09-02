using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Models.Msgs;
using Microsoft.UI.Xaml.Controls;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 音量规范化：galgame 大多默认音量过大。每款游戏【首次】启动时，
/// 用 Windows Core Audio 找到该游戏进程的应用会话音量，压到设定档位（默认 30%，功能默认关）。
///
/// 实现（对照宿主 GameMuteTask/AudioHelper 的成熟写法，COM IID 与宿主一致——此前
/// IAudioSessionEnumerator 的 IID 写错导致 GetSessionEnumerator 出参 QI 恒失败是"不生效"根因）：
/// · CLSID_MMDeviceEnumerator → 默认渲染端点（Multimedia 角色）→ IAudioSessionManager2
///   → IAudioSessionEnumerator 枚举会话 → IAudioSessionControl2.GetProcessId 匹配进程
///   → ISimpleAudioVolume.SetMasterVolume 设音量。
/// · 进程匹配优先 ProcessName（宿主手动指定，含 Steam/引导器场景的权威值），
///   否则回退 ExePath 文件名（宿主 TryGetProcessFromName 同款推导）。
/// · 启动内匹配的多个同名会话全部设置；音频会话在游戏出声后才存在，故启动后轮询等会话。
/// · 触发：主路 = 宿主「开始游玩」消息（GalgamePlayedMessage，与启动守卫同款挂载/校验）；
///   兜底 = GalPropertyChanged(LastPlayTime) 跳变（与守卫同款观察），两条路径并发由 in-flight 去重。
/// · 「仅首次 + 移动检测」：成功设置后记录 uuid 与其 exe 路径（PluginData.VolumeNormalizedGames/Paths）。
///   之后启动时短窗比对：exe 路径与记录一致 → 没移动，尊重用户手动调整不再动；路径变化 → Windows 已把
///   该进程音量重置回 100%，重新压一次并更新记录。旧版记录（无路径）下次启动补压一次以记录路径。
/// · 结果用 InfoBar 呈现（项目铁律：宿主日志看不到写回，诊断必须可见）。
/// </summary>
internal static class VolumeNormalizer
{
    private const int PollIntervalMs = 500;
    private const int MaxAttempts = 60; // 60 × 500ms = 30s，覆盖慢启动/引导器游戏
    private const int ShortPollAttempts = 16; // 16 × 500ms = 8s，已压过且路径非空的游戏只需短窗比对路径

    private static readonly object Lock = new();
    /** 已压过音量的游戏 uuid（内存倒影，避免每轮都碰字典） */
    private static HashSet<string> _done = new();
    /** 压音量时记录的进程 exe 路径快照（uuid → 路径；缺失/空 = 旧记录无路径信息） */
    private static readonly Dictionary<string, string> _donePaths = new();
    /** 正在处理的游戏（防消息路 + 属性路并发重复压制） */
    private static readonly HashSet<Guid> InFlight = new();
    /** 已订阅 GalPropertyChanged 的游戏（防重复订阅） */
    private static readonly HashSet<Guid> Watched = new();
    private static bool _initialized;
    private static bool _msgHooked;
    private static Timer? _initRetry;
    private static int _retriesLeft;
    private const int MaxInitRetries = 100; // 3 秒一轮 ×100 ≈ 5 分钟，覆盖慢盘冷启动

    /// <summary>「首次启动」判定用的累计时长阈值（分钟）：<see cref="Galgame.TotalPlayTime"/> 小于它且 PlayCount==0 才视为没玩过。
    /// 对齐宿主 MinPlayTimeRecordThreshold 默认 5 分钟——PlayCount 只在该值以上才 +1，故 PlayCount==0 && TotalPlayTime&lt;5 是最贴合的"基本没玩过"。</summary>
    private const int FirstPlayTotalMinutes = 5;

    /// <summary>弱引用表的 recipient 根对象：静态持有保活，否则弱表会回收掉处理器</summary>
    private static readonly object MsgRecipient = new();

    public static void Initialize()
    {
        lock (Lock)
        {
            if (_initialized) return;
            _initialized = true;
            _done = new HashSet<string>(Plugin.Data.VolumeNormalizedGames);
            _donePaths.Clear();
            foreach (KeyValuePair<string, string> kv in Plugin.Data.VolumeNormalizedPaths)
                _donePaths[kv.Key] = kv.Value;
            _retriesLeft = MaxInitRetries;
            foreach (Galgame g in GetGamesSafe()) Watch(g);
            if (Watched.Count == 0)
                _initRetry = new Timer(_ => RetryInitTick(), null,
                    TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }
        HookMessages();
    }

    public static void Uninitialize()
    {
        if (_msgHooked)
        {
            try { WeakReferenceMessenger.Default.UnregisterAll(MsgRecipient); }
            catch { /* 已回收 */ }
            _msgHooked = false;
        }
        lock (Lock)
        {
            try { _initRetry?.Dispose(); } catch { /* 已释放 */ }
            _initRetry = null;
            Watched.Clear();
            InFlight.Clear();
            _done.Clear();
            _donePaths.Clear();
            _initialized = false;
        }
    }

    /// <summary>清空「已压过」记录：下次启动游戏时重新压制</summary>
    public static void ResetRecords()
    {
        lock (Lock)
        {
            _done.Clear();
            _donePaths.Clear();
            Plugin.Data.VolumeNormalizedGames = new HashSet<string>();
            Plugin.Data.VolumeNormalizedPaths = new Dictionary<string, string>();
        }
    }

    /// <summary>清空单款游戏的「已压过」记录（右键菜单用）：该游戏下次启动时重新压制一次</summary>
    public static void ResetRecord(Guid uuid)
    {
        string key = uuid.ToString();
        lock (Lock)
        {
            _done.Remove(key);
            _donePaths.Remove(key);
            Plugin.Data.VolumeNormalizedGames = new HashSet<string>(_done);
            Plugin.Data.VolumeNormalizedPaths = new Dictionary<string, string>(_donePaths);
        }
    }

    /// <summary>宿主 GalgameAddedEvent 回调：新入库游戏纳入属性观察</summary>
    public static void WatchNewGame(Galgame game)
    {
        try { lock (Lock) Watch(game); } catch { /* 事件回调里绝不抛 */ }
    }

    private static void RetryInitTick()
    {
        try
        {
            foreach (Galgame g in GetGamesSafe()) lock (Lock) Watch(g);
        }
        catch
        {
            // 宿主服务未就绪，下轮再试
        }

        lock (Lock)
        {
            if (Watched.Count == 0 && --_retriesLeft > 0) return; // 库仍是空的，继续等
            try { _initRetry?.Dispose(); } catch { /* 已释放 */ }
            _initRetry = null;
        }
    }

    private static List<Galgame> GetGamesSafe()
    {
        try { return Plugin.HostApi.GetAllGames(); }
        catch { return []; /* 宿主集合服务尚不可用 */ }
    }

    private static void Watch(Galgame game)
    {
        if (!Watched.Add(game.Uuid)) return;
        game.GalPropertyChanged += OnGamePropertyChanged;
    }

    /// <summary>属性观察兜底：LastPlayTime 跳变视为一次启动</summary>
    private static void OnGamePropertyChanged(Galgame game, string property, object? value)
    {
        if (!string.Equals(property, nameof(Galgame.LastPlayTime), StringComparison.Ordinal)) return;
        try { OnLaunchObserved(game); } catch { /* 绝不外抛到宿主事件链 */ }
    }

    // ===== 宿主官方「开始游玩」消息钩子 =====
    private static void HookMessages()
    {
        if (_msgHooked) return;
        try
        {
            Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "GalgameManager");
            MethodInfo? getService = host?.GetType("GalgameManager.App")
                ?.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static);
            object? hostMessenger = getService?.MakeGenericMethod(typeof(IMessenger)).Invoke(null, null);
            if (!ReferenceEquals(hostMessenger, WeakReferenceMessenger.Default))
            {
                Log("音量规范化: 宿主与插件 Messenger 非同实例，主消息钩子不可用（仅属性观察兜底）");
                return;
            }
            WeakReferenceMessenger.Default.Register<GalgamePlayedMessage>(MsgRecipient,
                (_, m) => OnGamePlayed(m.Value));
            _msgHooked = true;
        }
        catch (Exception ex)
        {
            Log($"音量规范化: 消息钩子挂载失败: {ex.Message}");
        }
    }

    private static void OnGamePlayed(Galgame? game)
    {
        if (game is null) return;
        try { OnLaunchObserved(game); } catch { /* 不外抛到 Messenger 派发链 */ }
    }

    /// <summary>统一入口（消息/属性两条路径汇合）：校验、去重后转后台压制。
    /// 不用 now<=known 防回退（同秒并发两路会自相矛盾地误拦）。只信三个事实——
    /// ① 本轮启动（LastPlayTime 在最近 2 分钟内，宿主启动游戏时写 DateTime.Now；守卫还原/改历史都超时）；
    /// ② 是否已有记录：有路径记录 → 短窗比对路径（移动过则重压）；无路径记录（首次/旧版）→ 压并记录路径；
    /// ③ 未被另一路在跑。</summary>
    private static void OnLaunchObserved(Galgame game)
    {
        if (!_initialized || !Plugin.Data.VolumeNormalizeEnabled) return;
        DateTime now = game.LastPlayTime;
        // 宿主启动游戏时无条件 LastPlayTime = DateTime.Now。不是近期跳变 = 还原/改历史，一律忽略。
        if (DateTime.Now - now > TimeSpan.FromMinutes(2)) return;
        string uuid = game.Uuid.ToString();
        string? recordedPath = null;
        lock (Lock)
        {
            if (_done.Contains(uuid))
            {
                // 已被插件主动压过 → 走路径比对（移动检测：路径变了则重压，因为 Windows 会重置回 100%）
                if (!_donePaths.TryGetValue(uuid, out recordedPath) || string.IsNullOrEmpty(recordedPath))
                    return; // 旧版记录无路径 → 维持旧语义不再动（避免重压已玩过的游戏）
            }
            else
            {
                // 从未压过 → 只对「基本没玩过」的游戏自动压（用户定义 A：PlayCount==0 且累计时长<5 分钟）。
                // 已经玩过的游戏不自动压——尊重用户可能已在系统/游戏内调过的音量。
                if (game.PlayCount != 0 || game.TotalPlayTime >= FirstPlayTotalMinutes)
                    return;
                recordedPath = null; // 真正首次
            }
            if (!InFlight.Add(game.Uuid)) return; // 另一路已在跑
        }
        _ = Task.Run(() => ProcessAsync(game, recordedPath));
    }

    private static async Task ProcessAsync(Galgame game, string? recordedPath)
    {
        bool alreadyDone = recordedPath is not null; // 有路径记录 = 已压过（短窗比对即可）
        int maxAttempts = alreadyDone ? ShortPollAttempts : MaxAttempts;
        try
        {
            List<string> names = CandidateProcessNames(game);
            string? gameDir = SafeLocalPath(game);
            // 无进程名且无游戏目录：无任何匹配依据，跳过
            if (names.Count == 0 && string.IsNullOrWhiteSpace(gameDir))
            {
                Info($"音量规范化：《{game.Name.Value}》没有可识别的进程或目录，跳过");
                return;
            }

            bool qiFailed = false;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                lock (Lock) if (!_initialized) return;
                if (!Plugin.Data.VolumeNormalizeEnabled) return;

                (int nameMatched, int dirMatched, int set, int total, bool qif, string? pressedExe, bool samePath) =
                    TrySetVolumeForAny(names, gameDir, game.LastPlayTime, recordedPath);
                qiFailed |= qif;
                if (set > 0)
                {
                    lock (Lock)
                    {
                        _done.Add(game.Uuid.ToString());
                        // 记录实际压到的进程 exe 路径（拿不到就沿用旧记录/空，空=路径未知下次补压）
                        _donePaths[game.Uuid.ToString()] = string.IsNullOrEmpty(pressedExe)
                            ? (recordedPath ?? "")
                            : pressedExe;
                        Plugin.Data.VolumeNormalizedGames = new HashSet<string>(_done);
                        Plugin.Data.VolumeNormalizedPaths = new Dictionary<string, string>(_donePaths);
                    }
                    Info($"音量规范化：已把《{game.Name.Value}》应用音量压到 {LevelPercent:0}%" +
                         (alreadyDone ? "（检测到路径变化，重新压制）" : ""));
                    return;
                }
                if (samePath)
                {
                    // 找到与会话路径一致的记录 → 没移动，尊重用户手动调整，静默跳过
                    return;
                }
                if (nameMatched + dirMatched > 0 && set == 0)
                {
                    Log($"音量规范化: 命中《{game.Name.Value}》会话但设置失败 (QI 失败={qiFailed})");
                    break;
                }

                await Task.Delay(PollIntervalMs);
            }

            if (!alreadyDone)
                Info($"音量规范化：30 秒内未找到《{game.Name.Value}》的音频会话（候选进程: {string.Join(", ", names)}）");
            // 已压过的游戏短窗未确认到路径变化 → 静默视为未移动
        }
        catch (Exception ex)
        {
            Log($"音量规范化: 处理《{game.Name.Value}》异常: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (Lock) InFlight.Remove(game.Uuid);
        }
    }

    /// <summary>安全取游戏根目录（读不到返回 null，不抛）</summary>
    private static string? SafeLocalPath(Galgame game)
    {
        try { return game.LocalPath; } catch { return null; }
    }

    /// <summary>候选进程名：ProcessName 优先；否则取 exe 文件名（宿主 TryGetProcessFromName 同款）</summary>
    private static List<string> CandidateProcessNames(Galgame game)
    {
        var list = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(game.ProcessName))
                list.Add(game.ProcessName!);
            else if (!string.IsNullOrWhiteSpace(game.ExePath))
            {
                string? exe = Path.GetFileNameWithoutExtension(game.ExePath);
                if (!string.IsNullOrWhiteSpace(exe)) list.Add(exe!);
            }
        }
        catch
        {
            // 读不到就当没有
        }
        return list;
    }

    private static float TargetVolume => Math.Clamp(Plugin.Data.VolumeNormalizeLevel, 0f, 1f);
    private static float LevelPercent => TargetVolume * 100f;

    /// <summary>枚举所有音频会话：先按进程名匹配；无命中且给了游戏目录时，按「exe 路径在游戏目录内 + 进程启动时间晚于本次启动」兜底
    /// （覆盖 bat/启动器唤起真正游戏进程的场景）。返回 (名称匹配数, 目录兜底匹配数, 成功设置数, 总会话数, QI失败, 压到的exe路径, 路径一致标志)。
    /// recordedPath 非空时：命中会话的 exe 路径与记录一致 → 视为没移动，跳过不压（尊重手动调整），并置 samePath=true。
    /// RCW 纪律：out 参数直接声明为接口类型避免双 RCW；释放统一用 ReleaseSafe 置空、包 try-catch，
    /// 绝不二次 release 已释放对象（那会抛 InvalidComObjectException 逃过 catch 冒泡到调用方）。</summary>
    private static (int nameMatched, int dirMatched, int set, int total, bool qiFailed, string? pressedExe, bool samePath)
        TrySetVolumeForAny(IReadOnlyCollection<string> processNames, string? gameDir, DateTime launchTime, string? recordedPath)
    {
        int nameMatched = 0, dirMatched = 0, set = 0, total = 0;
        bool qiFailed = false, samePath = false;
        string? pressedExe = null;
        var rcw = new List<object>();
        var deviceEnumerator = new MMDeviceEnumeratorClass() as IMMDeviceEnumerator;
        if (deviceEnumerator is null) return (0, 0, 0, 0, false, null, false);
        rcw.Add(deviceEnumerator);
        IMMDevice? device = null;
        IAudioSessionManager2? mgr = null;
        IAudioSessionEnumerator? enumerator = null;
        try
        {
            deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device);
            if (device is null) return (0, 0, 0, 0, false, null, false);
            rcw.Add(device);
            Guid actIid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref actIid, 0, IntPtr.Zero, out mgr);
            if (mgr is null) return (0, 0, 0, 0, false, null, false);
            rcw.Add(mgr);
            mgr.GetSessionEnumerator(out enumerator);
            if (enumerator is null) return (0, 0, 0, 0, false, null, false);
            rcw.Add(enumerator);
            enumerator.GetCount(out int count);
            total = count;
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2? ctl = null;
                try
                {
                    enumerator.GetSession(i, out ctl);
                    if (ctl is null) continue;

                    ctl.GetProcessId(out uint pid);
                    string? pname = pid != 0 ? GetProcessNameSafe(pid) : null;
                    (string? exePath, DateTime? start) = GetProcessInfoSafe(pid);
                    bool nameHit = pname is not null &&
                                   processNames.Any(n => string.Equals(n, pname, StringComparison.OrdinalIgnoreCase));

                    // 名称没命中且给了游戏目录：按「exe 在游戏目录内 + 启动晚于本次启动」兜底（bat/启动器唤起真游戏进程）
                    bool dirHit = !nameHit && !string.IsNullOrWhiteSpace(gameDir) &&
                                  exePath is not null && start.HasValue &&
                                  IsPathInside(exePath!, gameDir!) &&
                                  start.Value >= launchTime.AddSeconds(-30); // 30s 容差，防时钟/亚秒偏差

                    if (nameHit) nameMatched++;
                    else if (dirHit) dirMatched++;
                    else continue;

                    // 路径比对：已有路径记录且命中会话的 exe 与记录一致 → 没移动，跳过不压（尊重用户手动调整）
                    if (recordedPath is { Length: > 0 } && exePath is not null &&
                        string.Equals(exePath, recordedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        samePath = true;
                        continue;
                    }

                    if (ctl is not ISimpleAudioVolume vol)
                    {
                        qiFailed = true;
                        continue;
                    }
                    Guid evt = Guid.Empty;
                    if (vol.SetMasterVolume(TargetVolume, ref evt) == 0)
                    {
                        set++;
                        pressedExe = exePath; // 记录实际压到的进程 exe 路径（供后续移动检测）
                    }
                }
                finally
                {
                    ReleaseSafe(ref ctl);
                }
            }
        }
        catch
        {
            // 枚举/设置失败静默（会话未建立 / 无默认设备 / API 不可用）
        }
        finally
        {
            foreach (object o in rcw) { try { Marshal.ReleaseComObject(o); } catch { /* 已释放 */ } }
        }
        return (nameMatched, dirMatched, set, total, qiFailed, pressedExe, samePath);
    }

    /// <summary>把进程 ID 转成进程名（找不到/已退出/无权限返回 null）</summary>
    private static string? GetProcessNameSafe(uint pid)
    {
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>安全取进程的 exe 路径与启动时间（权限不足/已退出返回 null；MainModule 对某些进程会抛）</summary>
    private static (string? exePath, DateTime? startTime) GetProcessInfoSafe(uint pid)
    {
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* 无权限/已退出 */ }
            DateTime? start = null;
            try { start = p.StartTime; } catch { /* 无权限 */ }
            return (path, start);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>判断 child 路径是否位于 dir 目录内（大小写不敏感，含子目录）</summary>
    private static bool IsPathInside(string child, string dir)
    {
        try
        {
            string d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string c = Path.GetFullPath(child);
            return c.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>COM 对象安全释放：置空并吞掉已释放/分离异常（防止 finally 里二次 release 抛 InvalidComObjectException）</summary>
    private static void ReleaseSafe<T>(ref T? com) where T : class
    {
        if (com is not null)
        {
            try { Marshal.ReleaseComObject(com); } catch { /* 已释放或已分离 */ }
            com = null;
        }
    }

    private static void Info(string msg)
    {
        try { Plugin.HostApi.Info(InfoBarSeverity.Informational, msg: msg); } catch { /* 提示失败不影响功能 */ }
    }

    private static void Log(string msg)
    {
        try { Plugin.HostApi.Log(InfoBarSeverity.Informational, msg); } catch { /* 日志失败不影响功能 */ }
    }
}

/// <summary>CLSID_MMDeviceEnumerator 的 ComImport coclass（与宿主 AudioHelper 一致）</summary>
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorClass
{
}

// ===== Windows Core Audio COM 接口定义（与宿主 GameMuteTask/AudioHelper 逐一对齐） =====
// 注意：IID 必须与 Windows SDK audiopolicy.h 完全一致——此前 IAudioSessionEnumerator 用了
// 错误 IID 导致 GetSessionEnumerator 出参 QI 恒失败，是本功能"不生效"的根因。

internal enum DataFlow { Render = 0, Capture = 1, All = 2 }
internal enum Role { Console = 0, Multimedia = 1, Communications = 2 }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    // 前 4 槽（EnumAudioEndpoints / GetDefaultAudioEndpoint / GetDevice / Register...）本功能不用，
    // 但 vtable 位置必须保留：GetDefaultAudioEndpoint 是第 2 槽（宿主 AudioHelper 用 NotImpl1 占住第 1 槽）。
    [PreserveSig]
    int NotImpl1();

    [PreserveSig]
    int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice ppDevice);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    /// <summary>out 直接声明为 IAudioSessionManager2：列集器按该 IID 返回接口 RCW，避免 object→cast 产生第二 RCW</summary>
    [PreserveSig]
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IAudioSessionManager2 ppInterface);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // 继承自 IAudioSessionManager 的两个方法（GetAudioSessionControl / GetSimpleAudioVolume）占住前 2 槽，
    // GetSessionEnumerator 是第 3 槽（宿主 AudioHelper 用 NotImpl1/NotImpl2 占位）。
    [PreserveSig]
    int NotImpl1();

    [PreserveSig]
    int NotImpl2();

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int SessionCount);

    [PreserveSig]
    int GetSession(int SessionCount, out IAudioSessionControl2 Session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl 的 9 个方法在前，然后才是 IAudioSessionControl2 自身的方法。
    [PreserveSig]
    int GetState(out int pRetVal);
    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig]
    int GetGroupingParam(out Guid pRetVal);
    [PreserveSig]
    int SetGroupingParam([MarshalAs(UnmanagedType.LPStruct)] Guid Override, [MarshalAs(UnmanagedType.LPStruct)] Guid EventContext);
    [PreserveSig]
    int RegisterAudioSessionNotification(object NewNotifications);
    [PreserveSig]
    int UnregisterAudioSessionNotification(object NewNotifications);
    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    [PreserveSig]
    int GetProcessId(out uint pRetVal);
    [PreserveSig]
    int IsSystemSoundsSession();
    [PreserveSig]
    int SetDuckingPreference(bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float fLevel, ref Guid EventContext);
    [PreserveSig]
    int GetMasterVolume(out float pfLevel);
    [PreserveSig]
    int SetMute(bool bMute, ref Guid EventContext);
    [PreserveSig]
    int GetMute(out bool pbMute);
}
