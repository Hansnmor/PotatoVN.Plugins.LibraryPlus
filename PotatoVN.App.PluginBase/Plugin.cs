using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Models;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase
{
    public partial class Plugin : IPlugin
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

            InitUi();
        }

        public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
        {
            if (cts.IsCancellationRequested) return Task.FromCanceled(cts);
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
