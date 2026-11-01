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
    private readonly ObservableCollection<HistoryItem> _history = new();
    private List<LanWorld> _worlds = [];
    private bool _loaded;

    private static LinkService Link => LinkService.Instance;

    public LinkPage()
    {
        InitializeComponent();

        PeerList.ItemsSource = _peers;
        LanList.ItemsSource = _lan;
        HistoryList.ItemsSource = _history;
        WorldComboBox.SelectionChanged += WorldComboBox_SelectionChanged;

        Link.OnStateChanged += OnLinkStateChanged;
        Link.OnPeersUpdated += OnPeersUpdated;
        Link.OnStatusUpdated += OnStatusUpdated;
        Link.OnWorldFound += OnWorldFound;
        RoomHistory.Changed += RefreshHistory;

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

        var suggested = FriendlyError.SuggestPort(25565);
        ManualPortBox.Value = suggested > 0 ? suggested : 25565;

        RefreshHistory();
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
            _peers.Clear();
            foreach (var peer in peers)
            {
                _peers.Add(new PeerItem
                {
                    Name = peer.DisplayName,
                    Info = $"{(peer.IsHost ? "房主" : "成员")} · {peer.Ping}ms"
                });
            }

            PeerSection.Visibility = _peers.Count > 0 && Link.State == LinkState.Connected
                ? Visibility.Visible
                : Visibility.Collapsed;
        });
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

        CreateCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        JoinCard.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

        if (!connected) return;

        RoomCodeText.Text = status.RoomCode;
        VirtualIpText.Text = status.VirtualIp ?? "—";
        RelayText.Text = status.RelayText;
        PingText.Text = status.PingText;
        OnlineText.Text = $"{status.OnlineCount} 人";

        RoomHintText.Visibility = Visibility.Visible;
        RoomHintText.Text = status.IsHost
            ? "你是房主。把房间码发给好友即可加入；离开房间会关闭虚拟网络。"
            : $"已加入房间。在 Minecraft 多人游戏中连接 127.0.0.1:{status.LocalPort} 即可进入游戏。";
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key)
        => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private void RefreshStatus_Click(object sender, RoutedEventArgs e) => _ = Link.RefreshStatusAsync();

    #endregion

    #region 创建房间

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        Dispatch(() => BusyRing.IsActive = true);
        try
        {
            _lan.Clear();
            _worlds = await Link.ScanLanAsync(TimeSpan.FromSeconds(6));

            WorldComboBox.Items.Clear();
            foreach (var world in _worlds)
            {
                WorldComboBox.Items.Add(world.Display);
                _lan.Add(new LanItem
                {
                    Display = world.Display,
                    Source = world.Source == "broadcast" ? "广播" : "扫描",
                    Port = world.Port
                });
            }

            if (_worlds.Count > 0)
            {
                WorldComboBox.SelectedIndex = 0;
                ManualPortBox.Value = _worlds[0].Port;
                PortHintText.Text = $"已发现 {_worlds.Count} 个世界，端口已自动填入。";
            }
            else
            {
                PortHintText.Text = "未发现局域网世界。可在 Minecraft 中开放后重试，或直接手动填写端口。";
            }
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
        var port = double.IsNaN(ManualPortBox.Value) ? 0 : (int)ManualPortBox.Value;

        if (port is <= 0 or > 65535)
        {
            ShowStatus("请填写 1 到 65535 之间的端口号。", InfoBarSeverity.Warning);
            return;
        }

        if (!FriendlyError.IsPortAvailable(port, out var portError))
        {
            ShowStatus(portError ?? "端口不可用", InfoBarSeverity.Error);
            return;
        }

        var name = HostNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = LauncherConfig.Current.OfflineUserName;

        Dispatch(() => BusyRing.IsActive = true);
        var ok = await Link.CreateRoomAsync(name, port);
        Dispatch(() => BusyRing.IsActive = false);

        if (ok) UpdateStatusPanel(Link.Status);
    }

    private void WorldComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorldComboBox.SelectedIndex >= 0 && WorldComboBox.SelectedIndex < _worlds.Count)
            ManualPortBox.Value = _worlds[WorldComboBox.SelectedIndex].Port;
    }

    private void Lan_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LanItem item) return;
        ManualPortBox.Value = item.Port;
        PortHintText.Text = $"已选择端口 {item.Port}，可点击“创建房间”。";
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

        Dispatch(() => BusyRing.IsActive = true);
        var ok = await Link.JoinRoomAsync(code, name);
        Dispatch(() => BusyRing.IsActive = false);

        if (ok)
        {
            RoomCodeBox.Text = string.Empty;
            JoinHintText.Text = string.Empty;
            UpdateStatusPanel(Link.Status);
        }
    }

    private void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not HistoryItem item) return;
        RoomCodeBox.Text = item.Code;
        RoomCodeBox.Focus(FocusState.Programmatic);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        RoomHistory.Clear();
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        Dispatch(() =>
        {
            _history.Clear();
            foreach (var entry in RoomHistory.Entries)
            {
                _history.Add(new HistoryItem
                {
                    Code = entry.Code,
                    Summary = entry.Summary,
                    Time = entry.TimeDisplay
                });
            }
        });
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

