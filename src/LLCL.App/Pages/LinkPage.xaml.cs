using System.Collections.ObjectModel;
using LLCL.Core;
using LLCL.Core.Link;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace LLCL.Pages;

/// <summary>联机页：创建房间、房间码加入、局域网扫描、房间状态面板。</summary>
public sealed partial class LinkPage : Page
{
    private readonly ObservableCollection<PeerItem> _peers = new();
    private readonly ObservableCollection<LanItem> _lan = new();
    private List<LanWorld> _worlds = [];
    private bool _loaded;

    private static LinkService Link => LinkService.Instance;

    public LinkPage()
    {
        InitializeComponent();

        PeerList.ItemsSource = _peers;

        Link.OnStateChanged += OnLinkStateChanged;
        Link.OnPeersUpdated += OnPeersUpdated;
        Link.OnStatusUpdated += OnStatusUpdated;
        Link.OnWorldFound += OnWorldFound;

        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            Init();
        };
    }

    #region 初始化

    private async void Init()
    {
        var name = LauncherConfig.Current.LobbyUserName;
        if (string.IsNullOrWhiteSpace(name)) name = LauncherConfig.Current.OfflineUserName;
        HostNameBox.Text = name;

        UpdateStatusPanel(Link.Status);

        try
        {
            var versions = EasyTierCore.GetInstalledVersions();
            KernelText.Text = versions.Count == 0
                ? "EasyTier 内核未安装，首次联机时会自动下载"
                : $"EasyTier 内核已就绪：{versions.OrderByDescending(v => v).First()}";
        }
        catch (Exception ex)
        {
            KernelText.Text = "内核状态未知：" + FriendlyError.ToText(ex);
        }

        await ScanAsync();
    }

    #endregion

    #region 状态面板

    private void OnLinkStateChanged(LinkEventArgs args)
    {
        Dispatch(() =>
        {
            BusyRing.IsActive = args.State is LinkState.Creating or LinkState.Joining
                or LinkState.Scanning or LinkState.Initializing or LinkState.Leaving;

            if (args.FriendlyMessage is not null)
                ShowStatus(args.FriendlyMessage, args.State == LinkState.Error
                    ? InfoBarSeverity.Error
                    : InfoBarSeverity.Informational);

            UpdateStatusPanel(Link.Status);
        });
    }

    private void OnStatusUpdated(LinkStatus status) => Dispatch(() => UpdateStatusPanel(status));

    private void OnPeersUpdated(List<PeerInfo> peers)
    {
        Dispatch(() =>
        {
            // 自己和房主都不进成员列表：
            // 自己出现在里面很怪，房主已经在上面的状态卡里单独展示。
            var selfIp = Link.Status.LocalVirtualIp;

            var wanted = peers
                .Where(p => !p.IsHost)
                .Where(p => string.IsNullOrWhiteSpace(selfIp) ||
                            !string.Equals(p.Ipv4, selfIp, StringComparison.OrdinalIgnoreCase))
                .Select(p => new PeerItem
                {
                    Name = p.DisplayName,
                    Info = $"{p.Ping}ms"
                })
                .ToList();

            // 增量更新而不是 Clear 后重建。
            // 队列每两秒刷新一次，清空重建会让 ListView 反复重绘，
            // 观感上就是成员列表一直在闪。
            SyncPeers(wanted);

            PeerSection.Visibility = _peers.Count > 0 && Link.State == LinkState.Connected
                ? Visibility.Visible
                : Visibility.Collapsed;
        });
    }

    /// <summary>按名称做增删改，避免整表重建。</summary>
    private void SyncPeers(List<PeerItem> wanted)
    {
        // 删除已消失的成员
        for (var i = _peers.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => w.Name == _peers[i].Name))
                _peers.RemoveAt(i);
        }

        // 追加新成员并更新已有成员的信息
        foreach (var item in wanted)
        {
            var existing = _peers.FirstOrDefault(p => p.Name == item.Name);
            if (existing is null)
            {
                _peers.Add(item);
            }
            else if (existing.Info != item.Info)
            {
                existing.Info = item.Info;
            }
        }
    }

    private void UpdateStatusPanel(LinkStatus status)
    {
        var connected = status.State == LinkState.Connected && status.RoomCode is not null;

        StateBadgeText.Text = status.StateText;

        // 徽标使用「实心底色 + 白字」的组合，保证在 Mica 材质上依然清晰可读
        (StateBadge.Background, StateBadgeText.Foreground) = status.State switch
        {
            LinkState.Connected => (Brush("SystemFillColorSuccessBrush"),
                                    Brush("TextOnAccentFillColorPrimaryBrush")),
            LinkState.Error => (Brush("SystemFillColorCriticalBrush"),
                                Brush("TextOnAccentFillColorPrimaryBrush")),
            LinkState.Creating or LinkState.Joining or LinkState.Initializing
                              => (Brush("SystemFillColorCautionBrush"),
                                  Brush("TextOnAccentFillColorPrimaryBrush")),
            _ => (Brush("SystemFillColorNeutralBrush"),
                  Brush("TextOnAccentFillColorPrimaryBrush"))
        };

        RoomCodeRow.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        MetricsGrid.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        PeerSection.Visibility = connected && _peers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        // 连接地址仅对加入者有意义：端口转发模式下本机会开一个本地端口
        var address = status.JoinerAddress;
        ConnectAddressRow.Visibility = connected && address is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConnectAddressText.Text = address ?? "—";

        CreateCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        JoinCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

        if (!connected) return;

        RoomCodeText.Text = status.RoomCode;
        VirtualIpText.Text = status.VirtualIp ?? "—";
        RelayText.Text = status.RelayText;
        PingText.Text = status.PingText;
        OnlineText.Text = $"{status.OnlineCount} 人";

        RoomHintText.Visibility = Visibility.Visible;
        if (status.IsHost)
        {
            RoomHintText.Text = "你是房主。把房间码发给好友，好友加入后会用「直接连接」进你的世界。";
        }
        else if (status.TunnelReachable == false)
        {
            // 自检失败时给出明确原因，避免用户只看到游戏里的 Connection reset
            RoomHintText.Text =
                "已加入房间，但房主的游戏端口暂时连不上。" +
                "请让房主确认游戏里已「对局域网开放」、且没有关闭世界，然后重新加入房间。";
        }
        else
        {
            RoomHintText.Text = "已加入房间。在 Minecraft 里选「直接连接」并填入上方的连接地址。";
        }
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key)
        => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private void RefreshStatus_Click(object sender, RoutedEventArgs e) => _ = Link.RefreshStatusAsync();

    #endregion

    #region 创建房间

    private async Task ScanAsync()
    {
        Dispatch(() => BusyRing.IsActive = true);
        try
        {
            _lan.Clear();
            _worlds = await Link.ScanLanAsync(TimeSpan.FromSeconds(6));

            PortHintText.Text = _worlds.Count > 0
                ? $"已发现 {_worlds.Count} 个局域网世界，可直接创建房间。"
                : "未发现局域网世界。请先在 Minecraft 里「对局域网开放」，再回到这里创建房间。";
        }
        catch (Exception ex)
        {
            PortHintText.Text = FriendlyError.ToText(ex);
        }
        finally
        {
            Dispatch(() => BusyRing.IsActive = false);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        // 端口不再让用户手填：先重新扫描，取当前真正开放的那个世界。
        // 每次「对局域网开放」游戏都会换端口，让用户自己抄反而更容易出错。
        Dispatch(() => BusyRing.IsActive = true);
        try
        {
            _worlds = await Link.ScanLanAsync(TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            Dispatch(() => BusyRing.IsActive = false);
            ShowStatus(FriendlyError.ToText(ex), InfoBarSeverity.Error);
            return;
        }
        Dispatch(() => BusyRing.IsActive = false);

        if (_worlds.Count == 0)
        {
            ShowStatus(
                "没有检测到已开放的局域网世界。\n" +
                "请先在游戏里 ESC →「对局域网开放」，看到聊天栏提示端口后，再回来点创建房间。",
                InfoBarSeverity.Warning);
            return;
        }

        // 多个世界时取第一个
        var port = _worlds[0].Port;

        Dispatch(() => BusyRing.IsActive = true);
        var isMinecraft = await MinecraftPing.PingAsync(port, TimeSpan.FromSeconds(4), default);
        Dispatch(() => BusyRing.IsActive = false);

        if (!isMinecraft)
        {
            ShowStatus(
                $"端口 {port} 可以连通，但没有响应 Minecraft 的状态查询，多半不是游戏端口。",
                InfoBarSeverity.Error);
            return;
        }

        var name = HostNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = LauncherConfig.Current.OfflineUserName;

        Dispatch(() => BusyRing.IsActive = true);
        var ok = await Link.CreateRoomAsync(name, port);
        Dispatch(() => BusyRing.IsActive = false);

        if (ok)
        {
            PortHintText.Text =
                $"房间码已包含端口 {port}。如果重新对局域网开放，端口会变化，需要重新创建房间。";
            UpdateStatusPanel(Link.Status);
        }
    }

    #endregion

    #region 加入房间

    private void RoomCodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var input = RoomCodeBox.Text?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            JoinHintText.Text = string.Empty;
            return;
        }

        var parsed = RoomCode.Parse(input, out var error);
        JoinHintText.Text = parsed is null
            ? error ?? string.Empty
            : $"✓ 房间码有效（端口 {parsed.Port}）";
        JoinHintText.Foreground = parsed is null
            ? Brush("SystemFillColorCriticalBrush")
            : Brush("TextFillColorSecondaryBrush");
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        var code = RoomCodeBox.Text?.Trim() ?? string.Empty;

        var parsed = RoomCode.Parse(code, out var error);
        if (parsed is null)
        {
            JoinHintText.Text = error ?? "房间码无效";
            return;
        }

        var name = LauncherConfig.Current.LobbyUserName;
        if (string.IsNullOrWhiteSpace(name)) name = LauncherConfig.Current.OfflineUserName;

        // 加入期间禁用按钮，避免连点启动两套内核
        Dispatch(() =>
        {
            BusyRing.IsActive = true;
            JoinButton.IsEnabled = false;
        });

        var ok = await Link.JoinRoomAsync(code, name);

        Dispatch(() =>
        {
            BusyRing.IsActive = false;
            JoinButton.IsEnabled = true;
        });

        if (ok)
        {
            RoomCodeBox.Text = string.Empty;
            JoinHintText.Text = string.Empty;
            UpdateStatusPanel(Link.Status);
        }
    }

    #endregion

    #region 房间操作

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        await Link.LeaveAsync();
        _peers.Clear();
        UpdateStatusPanel(Link.Status);
        ShowStatus("已离开房间。", InfoBarSeverity.Informational);
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        var code = Link.CurrentRoomCode ?? RoomCodeText.Text;
        if (string.IsNullOrEmpty(code)) return;

        try
        {
            var package = new DataPackage();
            package.SetText(code);
            Clipboard.SetContent(package);

            ShowStatus($"房间码已复制：{code}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus("复制失败：" + FriendlyError.ToText(ex), InfoBarSeverity.Warning);
        }
    }

    /// <summary>复制加入者应使用的连接地址。</summary>
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = Link.Status.JoinerAddress;
        if (string.IsNullOrEmpty(address)) return;

        try
        {
            var package = new DataPackage();
            package.SetText(address);
            Clipboard.SetContent(package);

            ShowStatus($"连接地址已复制：{address}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus("复制失败：" + FriendlyError.ToText(ex), InfoBarSeverity.Warning);
        }
    }

    private void OnWorldFound(LanWorld world) => Dispatch(() =>
    {
        if (_lan.Any(l => l.Port == world.Port)) return;
        _lan.Add(new LanItem
        {
            Display = world.Display,
            Source = world.Source == "broadcast" ? "广播" : "扫描",
            Port = world.Port
        });
    });

    #endregion

    #region 工具

    private void Dispatch(Action action)
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(() => action());
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    #endregion
}


