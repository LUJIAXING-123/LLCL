namespace LLCL.Pages;

/// <summary>UI 列表项模型（供 x:Bind 使用，必须为公开类型）。</summary>
public sealed class VersionCard
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public bool IsBroken { get; init; }
    public string? BrokenReason { get; init; }
    public string LoaderBadge { get; init; } = "原版";
}

public sealed class ResourceCard : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? IconUrl { get; init; }

    /// <summary>列表缩略图。由 IconUrl 构造，加载是异步的，不阻塞列表。</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Icon { get; init; }

    private string? _translated;

    /// <summary>
    /// 中文简介。取到之前为 null，界面据此决定是否显示这一行。
    /// </summary>
    public string? Translated
    {
        get => _translated;
        set
        {
            if (_translated == value) return;
            _translated = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Translated)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TranslatedText)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasTranslated)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DescriptionText)));
        }
    }

    /// <summary>
    /// 列表里显示的中文简介。未取到译文时是空字符串，
    /// 空文本不占高度，因此不需要再控制可见性。
    /// </summary>
    public string TranslatedText => _translated ?? string.Empty;

    /// <summary>
    /// 列表里实际显示的那段介绍：有中文就只显示中文，没有才回退到原文。
    /// 两者同时显示会让每一行都长一倍，也没必要。
    /// </summary>
    public string DescriptionText => string.IsNullOrWhiteSpace(_translated) ? Summary : _translated!;

    /// <summary>是否有中文简介。</summary>
    public bool HasTranslated => !string.IsNullOrWhiteSpace(_translated);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class VersionItem
{
    public string Id { get; init; } = string.Empty;
    public string TypeDisplay { get; init; } = string.Empty;
    public string ReleaseDate { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed class PeerItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; init; } = string.Empty;

    private string _info = string.Empty;

    /// <summary>
    /// 延迟等会频繁变化，做成可通知的属性，
    /// 这样只更新这一行而不用重建整张成员列表。
    /// </summary>
    public string Info
    {
        get => _info;
        set
        {
            if (_info == value) return;
            _info = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Info)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class HistoryItem
{
    public string Code { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
}

/// <summary>二级页面里的单个可下载版本。</summary>
public sealed class ResourceFileItem
{
    public string Name { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public LLCL.Core.Resources.ResourceFile File { get; init; } = new();
}

/// <summary>按游戏版本分组的资源版本集合。</summary>
public sealed class ResourceVersionGroup
{
    public string GameVersion { get; init; } = string.Empty;
    public string CountText { get; init; } = string.Empty;
    public IReadOnlyList<ResourceFileItem> Items { get; init; } = [];
}

/// <summary>关于页的赞助者条目。</summary>
public sealed class SponsorItem
{
    public string Name { get; init; } = string.Empty;
    public string Amount { get; init; } = string.Empty;
}

/// <summary>可勾选的前置模组条目。</summary>
public sealed class DependencyItem : System.ComponentModel.INotifyPropertyChanged
{
    public string ProjectId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool IsRequired { get; init; }

    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LanItem
{
    public string Display { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public int Port { get; init; }
}

/// <summary>
/// 下载任务卡片模型。实现 INotifyPropertyChanged，
/// 配合 x:Bind 的 OneWay 模式，让进度与状态能实时刷新。
/// </summary>
public sealed class TaskCard : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    private string _detail = string.Empty;
    private string _state = string.Empty;
    private double _ratio;

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value, nameof(Detail));
    }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value, nameof(State));
    }

    public double Ratio
    {
        get => _ratio;
        set => SetField(ref _ratio, value, nameof(Ratio));
    }

    private string _actionLabel = "取消并清理";

    /// <summary>
    /// 右侧按钮文案。失败/完成的任务已经是终态，按钮应当变成「清理」，
    /// 否则用户看到「取消并清理」会以为任务还在跑。
    /// </summary>
    public string ActionLabel
    {
        get => _actionLabel;
        set => SetField(ref _actionLabel, value, nameof(ActionLabel));
    }

    private bool _canTogglePause = true;

    /// <summary>已结束的任务不能再暂停，按钮应置灰。</summary>
    public bool CanTogglePause
    {
        get => _canTogglePause;
        set => SetField(ref _canTogglePause, value, nameof(CanTogglePause));
    }

    private bool _isIndeterminate;

    /// <summary>安装器等无量化进度的阶段显示滚动条。</summary>
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => SetField(ref _isIndeterminate, value, nameof(IsIndeterminate));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}

