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
    }

    /// <summary>按优先级尝试多种来源取得图标字节。</summary>
    private static byte[]? LoadLogoBytes()
    {
        // 1) 程序集内嵌资源（最可靠，打包与绿色版都适用）
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "LLCL.Assets.llcl.png", "Assets.llcl.png", "llcl.png" })
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
                var path = Path.Combine(dir, "Assets", "llcl.png");
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
