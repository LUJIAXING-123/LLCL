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

public sealed class ResourceCard
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? IconUrl { get; init; }
}

public sealed class VersionItem
{
    public string Id { get; init; } = string.Empty;
    public string TypeDisplay { get; init; } = string.Empty;
    public string ReleaseDate { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed class PeerItem
{
    public string Name { get; init; } = string.Empty;
    public string Info { get; init; } = string.Empty;
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

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}

