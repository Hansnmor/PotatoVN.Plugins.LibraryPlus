using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;

//todo: 请修改PotatoVN.App.PluginBase/PotatoVN.App.PluginBase.csproj中的AssemblyName
namespace PotatoVN.App.PluginBase
{
    public partial class Plugin : IPlugin, IPluginSetting
    {
        public static IPotatoVnApi HostApi { get; private set; } = null!;
        private IPotatoVnApi _hostApi = null!;
        private PluginData _data = new ();
        
        public PluginInfo Info { get; } = new()
        {
            //todo: 请务必随机生成一个新的Guid，切勿使用这个示例Guid，否则可能会和其他使用了同一Guid的插件发生冲突
            Id = new Guid("78f4ca27-7ffb-43b2-a5a5-111111db096d"), 
            Name = "插件示例",
            Description = "这是一个示范插件！\n这是第二行描述",
        };

        public async Task InitializeAsync(IPotatoVnApi hostApi)
        {
            _hostApi = hostApi;
            HostApi = hostApi;
            XamlResourceLocatorFactory.PackagePath = _hostApi.GetPluginPath();
            PluginLocalization.Initialize(hostApi); //初始化插件多国语言支持，如果你的插件不需要支持多语言，可以不调用这个方法，直接在代码里写死字符串即可。
            ResourceLoader.Initialize(); //初始化XAML字典加载器，资源用法请参考ResourceLoader类的注释
            var dataJson = await _hostApi.GetDataAsync();
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
            _data.PropertyChanged += (_, _) => SaveData(); // 当Observable属性变化时自动保存数据，对于普通属性请手动调用SaveData
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
            _ = _hostApi.SaveDataAsync(dataJson);
        }

        protected Guid Id => Info.Id;
    }
}
