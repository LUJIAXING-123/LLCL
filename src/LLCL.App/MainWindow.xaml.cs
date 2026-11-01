using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using LLCL.Core;
using LLCL.Pages;
using LLCL.Services;

namespace LLCL;

public sealed partial class MainWindow : Window
{
    private readonly nint _hwwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicBackdrop? _acrylic;
    private MicaBackdrop? _mica;

    private static MainWindow? _instance;

    /// <summary>当前主窗口（避免与 Window.Current 同名冲突）。</summary>
    public static MainWindow? Instance => _instance;

    public MainWindow()
    {
        InitializeComponent();
        _instance = this;
        _hwwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _SetUpTitleBar();
        _TryApplyBackdrop();

        // 启动时初始化配置与目录：
        // 1. 确定可写的数据根（不可写时回退到 AppData）
        // 2. 读取配置（配置文件位于 LLCL 文件夹内）
        // 3. 应用自定义游戏目录后，再创建所有目录
        Paths.ResolveWritableRoot();
        LauncherConfig.Load();
        if (!string.IsNullOrWhiteSpace(LauncherConfig.Current.CustomMinecraftDir))
            Paths.SetMinecraftDir(LauncherConfig.Current.CustomMinecraftDir);
        Paths.EnsureAll();

        Title = LauncherInfo.DisplayVersion;
        RootGrid.Loaded += (_, _) =>
        {
            // 仅导航一次；不设置 SelectedItem，否则 SelectionChanged 会重复导航
            Navigate("home");
        };

        // 启动后台准备：EasyTier 内核检查 / 更新
        _ = AppServices.WarmupAsync(this);

        AppWindow.Closing += AppWindow_Closing;

        // 尺寸必须在窗口激活后设置，否则会被 XAML 初始尺寸覆盖
        Activated += MainWindow_Activated;
    }

    private void _SetInitialSize()
    {
        try
        {
            var work = _GetWorkArea();
            var workWidthPx = Math.Max(1, work.Right - work.Left);
            var workHeightPx = Math.Max(1, work.Bottom - work.Top);

            // 直接按物理像素取工作区的 94%（无需 DPI 换算，避免缩放失真）
            var widthPx = (int)(workWidthPx * 0.94);
            var heightPx = (int)(workHeightPx * 0.94);

            _appWindow.Resize(new Windows.Graphics.SizeInt32(widthPx, heightPx));
            _appWindow.Move(new Windows.Graphics.PointInt32(
                work.Left + (workWidthPx - widthPx) / 2,
                work.Top + (workHeightPx - heightPx) / 2));
        }
        catch (Exception ex)
        {
            Logger.Warn($"设置窗口尺寸失败: {ex.Message}");
        }
    }

    private static uint _GetMonitorDpi()
    {
        // 主显示器 DPI；失败时回退到系统 DPI
        try
        {
            var monitor = MonitorFromPoint(new _Point { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0)
                return dpiX;
        }
        catch
        {
        }

        try { return GetDpiForWindow(GetDesktopWindow()); } catch { return 96; }
    }

    private struct _Point { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(_Point point, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    private bool _sized;

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_sized) return;
        _sized = true;
        _SetInitialSize();
    }

    private struct _Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, out _Rect rect, uint winIni);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>工作区（不含任务栏），物理像素。</summary>
    private static _Rect _GetWorkArea()
    {
        var rect = new _Rect();
        // SPI_GETWORKAREA = 0x0030
        SystemParametersInfo(0x0030, 0, out rect, 0);
        return rect;
    }

    private void _SetUpTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        try
        {
            SetTitleBar(TitleBar);
        }
        catch
        {
        }

        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonForegroundColor = null;
        _appWindow.TitleBar.ButtonHoverBackgroundColor = null;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
    }

    private void _TryApplyBackdrop()
    {
        if (!LauncherConfig.Current.UseMica) return;

        try
        {
            if (!MicaController.IsSupported()) return;
            _mica = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            SystemBackdrop = _mica;
        }
        catch
        {
            try
            {
                _acrylic = new DesktopAcrylicBackdrop();
                SystemBackdrop = _acrylic;
            }
            catch { }
        }
    }

    /// <summary>
    /// 页面缓存：切换标签时复用已有页面实例。
    /// 这样下载进度、列表滚动位置等界面状态不会丢，也避免重复订阅全局事件造成泄漏。
    /// </summary>
    private readonly Dictionary<string, Page> _pageCache = new();

    public void Navigate(string tag)
    {
        if (!_pageCache.TryGetValue(tag, out var page) || page is null)
        {
            page = tag switch
            {
                "home" => new HomePage(),
                "download" => new DownloadPage(),
                "link" => new LinkPage(),
                "settings" => new SettingsPage(),
                "about" => new AboutPage(),
                _ => new HomePage()
            };
            _pageCache[tag] = page;
        }

        // 同一页面重复导航时直接返回，避免不必要的重建
        if (ReferenceEquals(ContentFrame.Content, page))
        {
            UpdateNavSelection(tag);
            return;
        }

        ContentFrame.Content = page;
        UpdateNavSelection(tag);
    }

    /// <summary>打开指定版本的设置页。</summary>
    public void ShowVersionSettings(string versionId)
    {
        ContentFrame.Content = new VersionSettingsPage(versionId);
    }

    private void UpdateNavSelection(string tag)
    {
        // NavigationView 的父项与子项都可能有相同 Tag，做一次规范化
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            if (item.Tag as string == tag) { Nav.SelectedItem = item; return; }

        foreach (var item in Nav.FooterMenuItems.OfType<NavigationViewItem>())
            if (item.Tag as string == tag) { Nav.SelectedItem = item; return; }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }

    public void SetStatus(string text)
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(() => TitleBarStatus.Text = text);
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            await LLCL.Core.Link.LinkService.Instance.LeaveAsync();
        }
        catch { }
    }
}









