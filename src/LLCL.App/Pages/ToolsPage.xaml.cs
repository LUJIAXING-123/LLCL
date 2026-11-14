using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using LLCL.Core;
using LLCL.Core.Account;
using LLCL.Core.Minecraft;
using LLCL.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Pages;

/// <summary>
/// 工具页：放一些不常用但偶尔需要的小功能。
/// 条目对齐 PCL-CE 的百宝箱（PageToolsTest）。
/// </summary>
public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnLoaded();
    }

    private void OnLoaded()
    {
        // 默认保存到启动器数据目录下的 MyDownload
        if (string.IsNullOrWhiteSpace(DownloadFolderBox.Text))
            DownloadFolderBox.Text = Path.Combine(Paths.DataRoot, "MyDownload");
    }

    #region 百宝箱

    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            long freed = 0;
            var removed = 0;

            // 清理崩溃报告与旧日志：这两类最容易堆积且删掉无副作用
            var targets = new List<string>();

            foreach (var versionDir in Directory.Exists(Paths.VersionsDir)
                         ? Directory.GetDirectories(Paths.VersionsDir)
                         : [])
            {
                var crashDir = Path.Combine(versionDir, "crash-reports");
                if (Directory.Exists(crashDir)) targets.Add(crashDir);
            }

            var logsDir = Path.Combine(Paths.MinecraftDir, "logs");
            if (Directory.Exists(logsDir))
                targets.AddRange(Directory.GetFiles(logsDir, "*.log.gz"));

            foreach (var target in targets)
            {
                try
                {
                    if (File.Exists(target))
                    {
                        var size = new FileInfo(target).Length;
                        File.Delete(target);
                        freed += size;
                        removed++;
                    }
                    else if (Directory.Exists(target))
                    {
                        foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
                            freed += new FileInfo(file).Length;

                        Directory.Delete(target, true);
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"清理 {target} 失败: {ex.Message}");
                }
            }

            await ShowAsync("清理完成", removed > 0
                ? $"已清理 {removed} 处，释放 {FormatSize(freed)}。\n（崩溃报告与旧日志）"
                : "没有需要清理的内容。");
        }
        catch (Exception ex)
        {
            await ShowAsync("清理失败", FriendlyErrorText(ex));
        }
    }

    private async void Luck_Click(object sender, RoutedEventArgs e)
    {
        // 用日期与离线用户名派生，同一天内结果稳定
        var seed = $"{DateTime.Now:yyyyMMdd}|{LauncherConfig.Current.OfflineUserName}";
        var hash = seed.Aggregate(17, (current, c) => current * 31 + c);
        var luck = Math.Abs(hash % 100);

        var comment = luck switch
        {
            >= 90 => "今天运气爆棚，适合挑战极限模式。",
            >= 70 => "手气不错，可以试试抽奖。",
            >= 50 => "普普通通，稳扎稳打就好。",
            >= 30 => "稍微有点背，别碰岩浆。",
            _ => "建议今天只挖矿，不探险。"
        };

        await ShowAsync("今日人品", $"人品值：{luck} / 100\n\n{comment}");
    }

    private async void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Path.Combine(Paths.LauncherDir, "LLCL.exe");
            if (!File.Exists(exe))
            {
                await ShowAsync("找不到程序", "无法定位启动器本体，快捷方式创建失败。");
                return;
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var link = Path.Combine(desktop, "LL Craft Launcher.lnk");

            // 用 PowerShell 创建 .lnk，避免引入 COM 互操作依赖
            var script =
                "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + link + "');" +
                "$s.TargetPath='" + exe + "';" +
                "$s.WorkingDirectory='" + Paths.LauncherDir + "';" +
                "$s.IconLocation='" + exe + ",0';" +
                "$s.Save()";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is not null) await process.WaitForExitAsync();

            await ShowAsync("创建成功", File.Exists(link)
                ? $"已在桌面创建快捷方式：\n{link}"
                : "快捷方式创建失败，请检查桌面目录是否可写。");
        }
        catch (Exception ex)
        {
            await ShowAsync("创建失败", FriendlyErrorText(ex));
        }
    }

    private async void LaunchCount_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 通过遍历日志统计，不额外维护计数器
            var count = 0;
            var latest = DateTime.MinValue;

            if (Directory.Exists(Paths.VersionsDir))
            {
                foreach (var versionDir in Directory.GetDirectories(Paths.VersionsDir))
                {
                    var logs = Path.Combine(versionDir, "logs");
                    if (!Directory.Exists(logs)) continue;

                    foreach (var file in Directory.GetFiles(logs, "*.log*"))
                    {
                        count++;
                        var time = File.GetLastWriteTime(file);
                        if (time > latest) latest = time;
                    }
                }
            }

            var lines = new List<string>
            {
                $"日志文件数：{count}",
                $"最近活动：{(latest == DateTime.MinValue ? "无记录" : latest.ToString("yyyy-MM-dd HH:mm"))}",
                $"数据目录：{Paths.DataRoot}"
            };

            await ShowAsync("启动统计", string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            await ShowAsync("读取失败", FriendlyErrorText(ex));
        }
    }

    #endregion

    #region 自定义下载

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        // WinUI 3 的文件夹选择器需要窗口句柄，这里退化为让用户直接输入路径
        var suggestion = Directory.Exists(DownloadFolderBox.Text)
            ? DownloadFolderBox.Text
            : Path.Combine(Paths.DataRoot, "MyDownload");

        Directory.CreateDirectory(suggestion);
        DownloadFolderBox.Text = suggestion;
        ShowStatus(DownloadStatusText, $"已使用默认目录：{suggestion}");
    }

    private async void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        var url = DownloadUrlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowStatus(DownloadStatusText, "请填写有效的下载地址", isError: true);
            return;
        }

        var folder = DownloadFolderBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder))
            folder = Path.Combine(Paths.DataRoot, "MyDownload");

        var name = DownloadNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name))
            name = $"download-{DateTime.Now:yyyyMMddHHmmss}.bin";

        StartDownloadButton.IsEnabled = false;
        ShowStatus(DownloadStatusText, "正在下载…");

        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, name);

            await DownloadQueue.RunAsync($"下载 {name}", DownloadTaskKind.Other,
                async (downloader, token) =>
                {
                    // 复用下载队列的引擎，从而享有断点续传与速度统计
                    await downloader.DownloadAllAsync(
                        [new DownloadItem { Url = uri.ToString(), TargetPath = target }],
                        token);
                });

            ShowStatus(DownloadStatusText, $"已保存到：{target}");
        }
        catch (Exception ex)
        {
            ShowStatus(DownloadStatusText, "下载失败：" + FriendlyErrorText(ex), isError: true);
        }
        finally
        {
            StartDownloadButton.IsEnabled = true;
        }
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = DownloadFolderBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(Paths.DataRoot, "MyDownload");

            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(DownloadStatusText, "打开目录失败：" + FriendlyErrorText(ex), isError: true);
        }
    }

    #endregion

    #region 皮肤

    private async void SaveSkin_Click(object sender, RoutedEventArgs e)
    {
        var name = SkinNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowStatus(SkinStatusText, "请填写玩家名", isError: true);
            return;
        }

        ShowStatus(SkinStatusText, "正在查询玩家…");

        try
        {
            // 先按玩家名查 UUID
            var uuid = await QueryUuidAsync(name);
            if (uuid is null)
            {
                ShowStatus(SkinStatusText, $"找不到玩家「{name}」", isError: true);
                return;
            }

            ShowStatus(SkinStatusText, "正在下载皮肤…");

            var skin = await MinecraftSkin.GetSkinAsync(uuid);
            if (skin is null)
            {
                ShowStatus(SkinStatusText, "该玩家没有设置皮肤", isError: true);
                return;
            }

            var folder = Path.Combine(Paths.DataRoot, "Skins");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"{name}.png");
            await File.WriteAllBytesAsync(target, skin);

            ShowStatus(SkinStatusText, $"已保存到：{target}");
        }
        catch (Exception ex)
        {
            ShowStatus(SkinStatusText, "下载失败：" + FriendlyErrorText(ex), isError: true);
        }
    }

    /// <summary>按玩家名查询正版 UUID。找不到返回 null。</summary>
    private static async Task<string?> QueryUuidAsync(string name)
    {
        try
        {
            var url = $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}";
            var json = await Http.GetStringAsync(url);
            if (JsonNode.Parse(json) is not JsonObject obj) return null;
            return obj["id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region 服务器查询

    private async void QueryServer_Click(object sender, RoutedEventArgs e)
    {
        var address = ServerBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            ShowStatus(ServerStatusText, "请填写服务器地址", isError: true);
            return;
        }

        var host = address;
        var port = 25565;
        var colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var parsed))
        {
            port = parsed;
            host = address[..colon];
        }

        ShowStatus(ServerStatusText, "正在查询…");

        try
        {
            var (online, players, max, version) = await QueryJavaServerAsync(host, port);
            ShowStatus(ServerStatusText, online
                ? $"{host}:{port} 在线\n玩家：{players}/{max}\n版本：{version}"
                : $"{host}:{port} 无法连接\n可能未开放或已离线。",
                isError: !online);
        }
        catch (Exception ex)
        {
            ShowStatus(ServerStatusText, "查询失败：" + FriendlyErrorText(ex), isError: true);
        }
    }

    /// <summary>
    /// 极简的 Server List Ping。只取在线状态与人数，不解析图标等其它字段。
    /// </summary>
    private static async Task<(bool Online, int Players, int Max, string Version)> QueryJavaServerAsync(
        string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, cts.Token);

            var stream = client.GetStream();

            // 握手包
            using var body = new MemoryStream();
            WriteVarInt(body, 0x00);
            WriteVarInt(body, 767);
            WriteString(body, host);
            body.WriteByte((byte)(port >> 8));
            body.WriteByte((byte)(port & 0xFF));
            WriteVarInt(body, 1);

            using var frame = new MemoryStream();
            WriteVarInt(frame, (int)body.Length);
            var head = frame.ToArray();
            var payload = body.ToArray();
            await stream.WriteAsync(head, cts.Token);
            await stream.WriteAsync(payload, cts.Token);

            // 状态请求
            await stream.WriteAsync(new byte[] { 0x01, 0x00 }, cts.Token);

            var length = await ReadVarIntAsync(stream, cts.Token);
            if (length <= 0) return (false, 0, 0, "未知");

            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cts.Token);
                if (read == 0) break;
                offset += read;
            }

            var index = 0;
            ReadVarInt(buffer, ref index);
            var json = ReadString(buffer, ref index);

            if (JsonNode.Parse(json) is not JsonObject obj) return (false, 0, 0, "未知");

            var players = obj["players"];
            return (true,
                players?["online"]?.GetValue<int>() ?? 0,
                players?["max"]?.GetValue<int>() ?? 0,
                obj["version"]?["name"]?.GetValue<string>() ?? "未知");
        }
        catch
        {
            return (false, 0, 0, "未知");
        }
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            stream.WriteByte(b);
        } while (v != 0);
    }

    private static void WriteString(Stream stream, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];

        for (var i = 0; i < 5; i++)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return -1;

            var b = buffer[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }

        return -1;
    }

    private static int ReadVarInt(byte[] data, ref int index)
    {
        var result = 0;
        var shift = 0;

        for (var i = 0; i < 5; i++)
        {
            if (index >= data.Length) return -1;
            var b = data[index++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }

        return -1;
    }

    private static string ReadString(byte[] data, ref int index)
    {
        var length = ReadVarInt(data, ref index);
        if (length <= 0 || index + length > data.Length) return string.Empty;

        var text = System.Text.Encoding.UTF8.GetString(data, index, length);
        index += length;
        return text;
    }

    #endregion

    #region 成就

    private async void PreviewAchievement_Click(object sender, RoutedEventArgs e)
    {
        var title = AchievementTitleBox.Text?.Trim();
        var line1 = AchievementLine1Box.Text?.Trim();
        var line2 = AchievementLine2Box.Text?.Trim();

        if (string.IsNullOrWhiteSpace(line1))
        {
            ShowStatus(AchievementStatusText, "第一行不能为空", isError: true);
            return;
        }

        var preview = string.Join("\n\n", new[]
        {
            string.IsNullOrWhiteSpace(title) ? "获得成就！" : title,
            line1,
            string.IsNullOrWhiteSpace(line2) ? null : line2
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        await ShowAsync("成就预览", preview);
    }

    private async void SaveAchievement_Click(object sender, RoutedEventArgs e)
    {
        // 生成图片需要位图渲染，这里先把内容存成文本，
        // 后续接入渲染后再改为输出 PNG。
        var title = AchievementTitleBox.Text?.Trim();
        var line1 = AchievementLine1Box.Text?.Trim();
        var line2 = AchievementLine2Box.Text?.Trim();

        if (string.IsNullOrWhiteSpace(line1))
        {
            ShowStatus(AchievementStatusText, "第一行不能为空", isError: true);
            return;
        }

        try
        {
            var folder = Path.Combine(Paths.DataRoot, "Achievements");
            Directory.CreateDirectory(folder);

            var target = Path.Combine(folder, $"achievement-{DateTime.Now:yyyyMMddHHmmss}.txt");
            await File.WriteAllTextAsync(target,
                $"{title}\n{line1}\n{line2}".Trim());

            ShowStatus(AchievementStatusText,
                $"已保存到：{target}\n（当前先输出文本，图片渲染尚未接入）");
        }
        catch (Exception ex)
        {
            ShowStatus(AchievementStatusText, "保存失败：" + FriendlyErrorText(ex), isError: true);
        }
    }

    #endregion

    #region 通用

    private static void ShowStatus(TextBlock block, string text, bool isError = false)
    {
        block.Text = text;
        block.Visibility = Visibility.Visible;
        block.Foreground = isError
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private static string FriendlyErrorText(Exception ex) =>
        LLCL.Core.Link.FriendlyError.ToText(ex);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F1} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    private async Task ShowAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "确定",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    #endregion
}
