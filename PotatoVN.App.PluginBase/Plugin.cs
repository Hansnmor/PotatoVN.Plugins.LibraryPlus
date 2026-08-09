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
            Name = "更多排序条件",
            Description = "提供独立的游戏排序页面，支持按预计时长排序（升序/降序），不影响游戏库原生排序。",
        };

        private PluginData _data = new();
        private IPotatoVnApi _hostApi = null!;

        public PluginInfo Info { get; } = new()
        {
            Id = new Guid("70ee3f8a-361a-450a-acff-5371e85808b4"),
            Name = "更多排序条件",
            Description = "提供独立的游戏排序页面，支持按预计时长排序（升序/降序），不影响游戏库原生排序。",
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
