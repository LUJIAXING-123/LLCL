using LLCL.Core;
using Microsoft.UI.Xaml.Controls;

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
    }
}
