using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Contracts.Phrase;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Helper.Kungal;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase
{
    public partial class Plugin : IPlugin, IParserProvider, IGalgamePageRightPanel
    {
        public static IPotatoVnApi HostApi { get; private set; } = null!;

        /// <summary>插件数据（SortPage 等无插件实例上下文的位置读取用）</summary>
        internal static PluginData Data { get; private set; } = new();

        /// <summary>插件元信息（静态副本，供 SidebarSelectionHelper 等无实例上下文的位置使用）</summary>
        internal static PluginInfo StaticInfo { get; } = new()
        {
            Id = new Guid("70ee3f8a-361a-450a-acff-5371e85808b4"),
            Name = "游戏库增强",
            Description = "为游戏库提供更多排序（预计时长/游玩时间/游玩次数/我的评分）、过滤与统计数据，不影响原生页面。",
        };

        private PluginData _data = new();
        private IPotatoVnApi _hostApi = null!;

        /// <summary>kungal 搜刮器静态实例（SortPage 等无插件实例上下文的位置使用；与宿主注册的是同一实例）</summary>
        internal static KungalPhraser StaticPhraser { get; } = new();

        // ===== 批量搜刮全局状态（页面销毁重建后恢复锁定与进度用） =====

        /// <summary>批量搜刮是否进行中（全局共享——页面导航销毁后新页面据此恢复锁定 UI）</summary>
        internal static bool IsBatchScraping;

        /// <summary>批量搜刮进度文本（"搜刮中 3/50：游戏名"），新页面恢复显示用</summary>
        internal static string BatchStatus = "";

        /// <summary>批量状态变化事件（页面订阅以实时更新进度；页面销毁后新页面构造时读取当前值恢复）。
        /// 用委托字段而非 event 关键字：SortPage 需从外部 Invoke（event 只能声明类内触发）</summary>
        internal static Action? BatchStatusChanged;

        /// <summary>kungal 搜刮器实例（插件页「更多搜刮」直接复用，配置改此实例）</summary>
        public KungalPhraser KungalPhraser => StaticPhraser;

        // ===== IParserProvider：向宿主注册 kungal 数据源 =====
        public IGalInfoPhraser GetPhraser() => StaticPhraser;

        public string ParserName => "Kungal";

        public PluginInfo Info { get; } = new()
        {
            Id = new Guid("70ee3f8a-361a-450a-acff-5371e85808b4"),
            Name = "游戏库增强",
            Description = "为游戏库提供更多排序（预计时长/游玩时间/游玩次数/我的评分）、过滤与统计数据，不影响原生页面。",
        };

        public async Task InitializeAsync(IPotatoVnApi hostApi)
        {
            _hostApi = hostApi;
            HostApi = hostApi;
            XamlResourceLocatorFactory.PackagePath = hostApi.GetPluginPath();
            ResourceLoader.Initialize(); //加载XAML样式资源（SortPage / GalgamePrefab 使用）

            var dataJson = await hostApi.GetDataAsync();
            if (!string.IsNullOrWhiteSpace(dataJson))
            {
                try
                {
                    _data = System.Text.Json.JsonSerializer.Deserialize<PluginData>(dataJson) ?? new PluginData();
                }
                catch
                {
                    _data = new PluginData();
                }
            }
            Data = _data;
            _data.PropertyChanged += (_, _) => SaveData(); // Observable属性变化时自动保存

            // 清除宿主「上次运行崩溃」历史残留（lastError 只累加从不清理，会持续弹旧崩溃提示）
            HostServices.ClearLastError();
            InitUi();
            KungalOpenHelper.Initialize();
        }

        public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
        {
            if (cts.IsCancellationRequested) return Task.FromCanceled(cts);
            KungalOpenHelper.Uninitialize();
            ResourceLoader.Unload(); // 卸载XAML资源字典
            return Task.CompletedTask;
        }

        private void SaveData()
        {
            var dataJson = System.Text.Json.JsonSerializer.Serialize(_data);
            _ = HostApi.SaveDataAsync(dataJson);
        }

        protected Guid Id => Info.Id;
    }
}
