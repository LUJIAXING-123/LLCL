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

public sealed class LanItem
{
    public string Display { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public int Port { get; init; }
}


