using System.IO;
using LLCL.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace LLCL.Pages;

/// <summary>关于页：左侧图标 + 右侧产品名与发行方。</summary>
public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
    }

    private void Populate()
    {
        ProductTitle.Text = LauncherInfo.ProductName;
        StudioText.Text = $"Made by {LauncherInfo.Company}";

        VersionLine.Text = $"版本 {LauncherInfo.Version}";
        StageLine.Text = $"{LauncherInfo.StageDisplay} · 构建于 {LauncherInfo.BuildTime:yyyy-MM-dd}";
        PathLine.Text = $"游戏目录：{Paths.MinecraftDir}\n数据目录：{Paths.DataRoot}";

        _ = LoadLogoAsync();
    }

    /// <summary>优先从程序集资源加载图标，失败时回退到安装目录下的 PNG 文件。</summary>
    private async Task LoadLogoAsync()
    {
        try
        {
            // 1) ms-appx 资源（打包 / 有 Assets 目录时可用）
            var uri = new Uri("ms-appx:///Assets/llcl.png");
            var bitmap = new BitmapImage();
            bitmap.UriSource = uri;
            LogoSide.Source = bitmap;
            return;
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载内置图标失败: {ex.Message}");
        }

        // 2) 回退：读取 exe 同级 Assets\llcl.png
        try
        {
            var path = Path.Combine(Paths.LauncherDir, "Assets", "llcl.png");
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                LogoSide.Source = bitmap;
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"从文件加载图标失败: {ex.Message}");
        }

        // 3) 再回退：程序目录（非打包时的 AppContext.BaseDirectory）
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "llcl.png");
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                LogoSide.Source = bitmap;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"图标加载全部失败: {ex.Message}");
        }
    }
}

