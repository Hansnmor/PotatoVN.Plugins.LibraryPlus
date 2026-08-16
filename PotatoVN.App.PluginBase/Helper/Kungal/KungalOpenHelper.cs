using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GalgameManager.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace PotatoVN.App.PluginBase.Helper.Kungal
{
    /// <summary>
    /// 向宿主游戏详情页右上角「···」菜单注入「在Kungal中打开」。
    /// 仅当游戏已经单次/批量用 kungal 搜刮过（<see cref="Galgame.IdForPlugins"/> 中存有
    /// <see cref="KungalPhraser.ParserId"/> 对应的 gid）时才注入，行为与原生「在BGM中打开」等一致：
    /// 「在外部网站中打开」AppBarButton（v1.10.2.0，Flyout 为 MenuFlyout，各数据源是子菜单项）的
    /// 子菜单末尾追加「在Kungal中打开」。
    /// 若游戏只有 kungal id、没有任何原生外部网站 id，宿主的「在外部网站中打开」按钮会因
    /// CanOpenInExternalWebsite=false 被折叠隐藏——此时强制把该按钮设为可见（不新增独立按钮），
    /// 保证「在Kungal中打开」入口可用。
    /// </summary>
    internal static class KungalOpenHelper
    {
        private const string HostAssemblyName = "GalgameManager";
        private const string NavigationServiceTypeName = "GalgameManager.Contracts.Services.INavigationService";
        private const string HomeDetailPageTypeName = "GalgameManager.Views.HomeDetailPage";
        private const string ButtonTag = "PotatoVN.App.PluginBase.OpenInKungal";
        private const string MenuLabel = "在Kungal中打开";
        private const string KungalPageUrlFormat = "https://www.kungal.com/galgame/{0}";

        /// <summary>原生「在外部网站中打开」对应的 Galgame.Ids 索引（Vndb/Bgm/Ymgal/Cngal/Steam/Hikarinagi）。</summary>
        private static readonly int[] BuiltInExternalIdIndexes = { 0, 1, 5, 6, 7, 8 };

        private static readonly List<InjectedItem> InjectedItems = [];
        private static object? _navigationService;
        private static EventInfo? _navigatedEvent;
        private static Delegate? _navigatedHandler;
        private static bool _initialized;

        /// <summary>插件初始化时调用：订阅宿主导航事件，并处理“插件加载时已经停在详情页”的情况。</summary>
        public static void Initialize()
        {
            if (_initialized) return;
            try
            {
                object? navigationService = ResolveNavigationService();
                if (navigationService is null) return;

                EventInfo? navigatedEvent = navigationService.GetType().GetEvent("Navigated");
                MethodInfo? handlerMethod = typeof(KungalOpenHelper).GetMethod(nameof(OnNavigated),
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (navigatedEvent is null || navigatedEvent.EventHandlerType is not { } handlerType ||
                    handlerMethod is null) return;

                Delegate handler = Delegate.CreateDelegate(handlerType, handlerMethod);
                navigatedEvent.AddEventHandler(navigationService, handler);

                _navigationService = navigationService;
                _navigatedEvent = navigatedEvent;
                _navigatedHandler = handler;
                _initialized = true;

                Plugin.HostApi.InvokeOnMainThread(InjectIntoCurrentPage);
            }
            catch (Exception ex)
            {
                LogWarning($"初始化详情页 Kungal 打开入口失败: {ex.Message}");
            }
        }

        /// <summary>插件卸载时调用：退订导航事件并移除已经注入的菜单项。</summary>
        public static void Uninitialize()
        {
            try
            {
                if (_navigatedEvent is not null && _navigationService is not null && _navigatedHandler is not null)
                    _navigatedEvent.RemoveEventHandler(_navigationService, _navigatedHandler);
            }
            catch
            {
                // 忽略：卸载路径只做清理
            }

            _navigationService = null;
            _navigatedEvent = null;
            _navigatedHandler = null;
            _initialized = false;

            try
            {
                Plugin.HostApi.InvokeOnMainThread(CleanupAllInjectedItems);
            }
            catch
            {
                // 忽略：窗口可能已经不可用
            }
        }

        /// <summary>宿主 NavigationService.Navigated 事件（sender 为宿主 Frame，与 Frame.Navigated 同签名）。</summary>
        private static void OnNavigated(object sender, NavigationEventArgs e)
        {
            try
            {
                if (sender is not Frame { Content: Page page }) return;
                if (page.GetType().FullName != HomeDetailPageTypeName) return;

                // NavigationService.Navigated 在 OnNavigatedTo 之后触发，但 OnNavigatedTo 是 async void，
                // 事件触发时 ViewModel.Item 可能尚未赋值。导航参数是同步可用的，优先从这里拿游戏。
                Galgame? game = GetGameFromParameter(e.Parameter) ?? GetGameFromViewModel(page);
                TryInject(page, game);
            }
            catch (Exception ex)
            {
                LogWarning($"注入 Kungal 打开入口失败: {ex.Message}");
            }
        }

        /// <summary>处理“插件加载时已经停在详情页”的情况（导航事件已过，直接注入当前页）。</summary>
        private static void InjectIntoCurrentPage()
        {
            try
            {
                if (_navigationService is null) return;
                object? frame = _navigationService.GetType().GetProperty("Frame")?.GetValue(_navigationService);
                if (frame is not Frame { Content: Page page }) return;
                if (page.GetType().FullName != HomeDetailPageTypeName) return;
                TryInject(page, GetGameFromViewModel(page));
            }
            catch (Exception ex)
            {
                LogWarning($"处理当前详情页 Kungal 打开入口失败: {ex.Message}");
            }
        }

        private static void TryInject(Page page, Galgame? game)
        {
            if (game is null || !TryGetGid(game, out int gid)) return;

            CommandBar? commandBar = FindCommandBar(page);
            if (commandBar is null)
            {
                // 导航事件早于页面 Loaded，视觉树可能尚未完整构建——延迟到 Loaded 后再试一次。
                page.Loaded += (_, _) =>
                {
                    try
                    {
                        TryInject(page, GetGameFromViewModel(page) ?? game);
                    }
                    catch
                    {
                        // 忽略：页面销毁等竞态
                    }
                };
                return;
            }

            AppBarButton? externalButton = FindExternalWebsiteButton(commandBar);
            if (externalButton?.Flyout is not MenuFlyout externalFlyout)
            {
                LogWarning("未找到「在外部网站中打开」按钮的 MenuFlyout，跳过 Kungal 打开入口注入");
                return;
            }

            // 游戏只有 kungal id、没有任何原生外部网站 id 时，宿主按钮会因 CanOpenInExternalWebsite=false
            // 被折叠隐藏——强制显示它，保证「在Kungal中打开」入口可见（不新增独立按钮）。
            if (!HasAnyBuiltInExternalId(game))
                externalButton.Visibility = Visibility.Visible;

            TryAddToExternalFlyout(externalFlyout, gid);
        }

        private static bool TryAddToExternalFlyout(MenuFlyout flyout, int gid)
        {
            if (HasInjectedItem(flyout.Items)) return true;

            MenuFlyoutItem item = CreateMenuFlyoutItem(gid);
            try
            {
                flyout.Items.Add(item);
            }
            catch (Exception ex)
            {
                LogWarning($"向「在外部网站中打开」子菜单添加 Kungal 失败: {ex.Message}");
                return false;
            }

            TrackInjectedItem(flyout.Items, item);
            Plugin.HostApi.Log(InfoBarSeverity.Informational, $"详情页已在「在外部网站中打开」添加「{item.Text}」: gid={gid}");
            return true;
        }

        private static MenuFlyoutItem CreateMenuFlyoutItem(int gid)
        {
            var item = new MenuFlyoutItem
            {
                Text = MenuLabel,
                Tag = ButtonTag,
                Icon = new FontIcon { Glyph = "\uE774" }, // Globe：与「在外部网站中打开」子项同款语义图标
            };
            item.Click += (_, _) => OpenKungal(gid);
            return item;
        }

        private static void OpenKungal(int gid)
        {
            try
            {
                _ = Launcher.LaunchUriAsync(new Uri(string.Format(KungalPageUrlFormat, gid)));
            }
            catch (Exception ex)
            {
                LogWarning($"打开 Kungal 页面失败: {ex.Message}");
            }
        }

        /// <summary>游戏是否已存 kungal gid（单次/批量搜刮后由 KungalPhraser 写入 IdForPlugins 并随游戏持久化）。</summary>
        private static bool TryGetGid(Galgame game, out int gid)
        {
            gid = 0;
            if (game.IdForPlugins is null) return false;
            if (!game.IdForPlugins.TryGetValue(KungalPhraser.ParserId, out string? value) ||
                string.IsNullOrWhiteSpace(value)) return false;
            return int.TryParse(value, out gid) && gid > 0;
        }

        /// <summary>游戏是否有任一原生外部网站 id（决定宿主「在外部网站中打开」按钮是否折叠）。</summary>
        private static bool HasAnyBuiltInExternalId(Galgame game)
        {
            string?[]? ids = game.Ids;
            if (ids is null) return false;
            foreach (int index in BuiltInExternalIdIndexes)
            {
                if (index >= 0 && index < ids.Length && !string.IsNullOrEmpty(ids[index]))
                    return true;
            }
            return false;
        }

        /// <summary>从导航参数（GalgamePageParameter.Galgame 公开字段）取游戏。</summary>
        private static Galgame? GetGameFromParameter(object? parameter)
        {
            if (parameter is null) return null;
            try
            {
                FieldInfo? field = parameter.GetType().GetField("Galgame");
                return field?.GetValue(parameter) as Galgame;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从详情页 ViewModel.Item 取游戏（导航后 ViewModel 可能尚未完成初始化，作参数兜底）。</summary>
        private static Galgame? GetGameFromViewModel(Page page)
        {
            try
            {
                PropertyInfo? viewModelProperty = page.GetType().GetProperty("ViewModel");
                object? viewModel = viewModelProperty?.GetValue(page);
                if (viewModel is null) return null;
                PropertyInfo? itemProperty = viewModel.GetType().GetProperty("Item");
                return itemProperty?.GetValue(viewModel) as Galgame;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>在页面视觉树中查找详情页 CommandBar（主命令 + SecondaryCommands 同属一个）。</summary>
        private static CommandBar? FindCommandBar(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is CommandBar commandBar) return commandBar;
                if (FindCommandBar(child) is { } found) return found;
            }
            return null;
        }

        /// <summary>在 SecondaryCommands 中定位「在外部网站中打开」按钮（其 Flyout 为 MenuFlyout，v1.10.2.0 结构）。</summary>
        private static AppBarButton? FindExternalWebsiteButton(CommandBar commandBar)
        {
            foreach (ICommandBarElement element in commandBar.SecondaryCommands)
            {
                if (element is not AppBarButton button) continue;
                if (button.Flyout is MenuFlyout) return button;
                if (button.ContextFlyout is MenuFlyout) return button;
            }
            return null;
        }

        private static bool HasInjectedItem(IEnumerable<object> items)
        {
            foreach (object item in items)
            {
                if (item is FrameworkElement { Tag: string tag } && tag == ButtonTag)
                    return true;
            }
            return false;
        }

        private static void TrackInjectedItem(object owner, FrameworkElement element)
        {
            InjectedItems.Add(new InjectedItem(owner, element));
        }

        /// <summary>插件卸载时移除所有已注入的菜单项/按钮（owner 为 MenuFlyout.Items 或 SecondaryCommands，反射 Remove）。</summary>
        private static void CleanupAllInjectedItems()
        {
            foreach (InjectedItem injected in InjectedItems)
            {
                try
                {
                    injected.Owner.GetType().GetMethod("Remove")?.Invoke(injected.Owner, new[] { injected.Element });
                }
                catch
                {
                    // 忽略：页面可能已销毁，集合不可用
                }
            }
            InjectedItems.Clear();
        }

        private static object? ResolveNavigationService()
        {
            Assembly? host = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HostAssemblyName);
            if (host is null) return null;
            Type? appType = host.GetType("GalgameManager.App");
            Type? navigationServiceType = host.GetType(NavigationServiceTypeName);
            if (appType is null || navigationServiceType is null) return null;

            // 注意：App.GetService&lt;T&gt;() 的泛型参数必须是 DI 容器注册的类型（接口，非具体类）。
            MethodInfo? getService = appType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Static);
            return getService?.MakeGenericMethod(navigationServiceType).Invoke(null, null);
        }

        private static void LogWarning(string message)
        {
            try
            {
                Plugin.HostApi.Log(InfoBarSeverity.Warning, message);
            }
            catch
            {
                // 日志失败不影响功能
            }
        }

        /// <summary>已注入的菜单项/按钮记录（卸载时统一移除）。</summary>
        private sealed class InjectedItem(object owner, FrameworkElement element)
        {
            public object Owner { get; } = owner;

            public FrameworkElement Element { get; } = element;
        }
    }
}
