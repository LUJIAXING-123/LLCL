using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using LLCL.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

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

        SetLogo(LoadLogoBytes());

        ThanksLine.Text = "特别感谢 冷刃风影 的赞助支持。";
    }

    /// <summary>缓存的收款码字节，点击按钮时才创建位图。</summary>
    private byte[]? _donateQrBytes;

    /// <summary>
    /// 查找微信收款码。依次尝试：
    /// 1) 内嵌资源
    /// 2) exe 同级目录与 Assets 子目录
    /// 3) 数据目录
    /// </summary>
    private byte[]? LoadDonateQrBytes()
    {
        if (_donateQrBytes is not null) return _donateQrBytes;

        const string fileName = "donate-qrcode.png";

        // 1) 内嵌资源
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("LLCL.Assets.donate-qrcode.png");
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                _donateQrBytes = ms.ToArray();
                return _donateQrBytes;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取内嵌收款码失败: {ex.Message}");
        }

        // 2) 文件系统
        foreach (var dir in new[] { Paths.LauncherDir, AppContext.BaseDirectory, Paths.DataRoot })
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, fileName),
                         Path.Combine(dir, "Assets", fileName)
                     })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;

                    var bytes = File.ReadAllBytes(candidate);
                    if (bytes.Length == 0) continue;

                    Logger.Info($"关于页收款码：使用文件 {candidate}");
                    _donateQrBytes = bytes;
                    return _donateQrBytes;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"读取收款码失败 {candidate}: {ex.Message}");
                }
            }
        }

        Logger.Warn($"未找到收款码 {fileName}");
        return null;
    }

    /// <summary>弹出微信收款码。</summary>
    private async void Donate_Click(object sender, RoutedEventArgs e)
    {
        var bytes = LoadDonateQrBytes();

        if (bytes is null)
        {
            await ShowAsync("收款码缺失",
                "没有找到收款码图片。如果你仍想支持开发，可以通过 GitHub 仓库联系作者。");
            return;
        }

        var image = new Image
        {
            Width = 240,
            Height = 240,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        };

        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            bitmap.SetSource(stream);
            image.Source = bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn($"收款码解码失败: {ex.Message}");
            await ShowAsync("收款码无法显示", "图片解码失败，请通过 GitHub 仓库联系作者。");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "微信扫码支持开发",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    image,
                    new TextBlock
                    {
                        Text = "赞助完全自愿，不影响任何功能的使用。",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private async Task ShowAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    /// <summary>打开项目仓库。</summary>
    private void Repo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/LUJIAXING-123/LLCL",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开仓库失败: {ex.Message}");
        }
    }

    /// <summary>按优先级尝试多种来源取得图标字节。</summary>
    private static byte[]? LoadLogoBytes()
    {
        // 1) 程序集内嵌资源（最可靠，打包与绿色版都适用）
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // 优先用原图（细节最好），其次用 256px 版本
            foreach (var name in new[] { "LLCL.Assets.llcl-logo.png", "LLCL.Assets.llcl.png", "Assets.llcl.png", "llcl.png" })
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is null) continue;

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                Logger.Info($"关于页图标：使用内嵌资源 {name}");
                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取内嵌图标失败: {ex.Message}");
        }

        // 2) exe 同级 Assets\llcl.png
        foreach (var dir in new[] { Paths.LauncherDir, AppContext.BaseDirectory })
        {
            try
            {
                var path = Path.Combine(dir, "Assets", "llcl-logo.png");
                if (!File.Exists(path)) path = Path.Combine(dir, "Assets", "llcl.png");
                if (!File.Exists(path)) continue;

                var bytes = File.ReadAllBytes(path);
                Logger.Info($"关于页图标：使用文件 {path}");
                return bytes;
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取图标文件失败: {ex.Message}");
            }
        }

        Logger.Warn("关于页图标：未找到可用的图片来源");
        return null;
    }

    private void SetLogo(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            // 全部失败时给一个可见的占位，至少让布局不塌陷
            LogoSide.Visibility = Visibility.Collapsed;
            LogoFallback.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            bitmap.SetSource(stream);
            LogoSide.Source = bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn($"图标解码失败: {ex.Message}");
            LogoSide.Visibility = Visibility.Collapsed;
            LogoFallback.Visibility = Visibility.Visible;
        }
    }
}

