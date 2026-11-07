using System.Security.Cryptography;
using System.Text;
using LLCL.Core.Net;

namespace LLCL.Core.Account;

/// <summary>
/// 账号管理：持久化账号列表、维护当前选中账号、按需刷新令牌。
/// <para>
/// 账号信息保存在 <see cref="Paths.DataRoot"/> 下的 accounts.json，
/// 与启动器主配置分离，便于单独备份或清理。
/// </para>
/// </summary>
public static class AccountManager
{
    private static readonly object _lock = new();
    private static List<AccountEntry>? _accounts;

    /// <summary>账号文件路径。</summary>
    private static string FilePath => Path.Combine(Paths.DataRoot, "accounts.json");

    /// <summary>账号列表发生变化（增删改选）时触发。</summary>
    public static event Action? Changed;

    /// <summary>读取全部账号（首次调用时从磁盘加载）。</summary>
    public static IReadOnlyList<AccountEntry> Accounts
    {
        get
        {
            lock (_lock)
            {
                _accounts ??= _Load();
                return _accounts;
            }
        }
    }

    /// <summary>当前选中的账号 ID。</summary>
    public static string? SelectedId
    {
        get => LauncherConfig.Current.SelectedAccountId;
        set
        {
            LauncherConfig.Current.SelectedAccountId = value;
            LauncherConfig.Current.Save();
            Changed?.Invoke();
        }
    }

    /// <summary>当前选中账号；没有可用账号时返回 null。</summary>
    public static AccountEntry? Selected
    {
        get
        {
            var id = SelectedId;
            if (id is null) return null;
            return Accounts.FirstOrDefault(a => a.Id == id);
        }
    }

    /// <summary>添加账号并选中它。</summary>
    public static void Add(AccountEntry account)
    {
        lock (_lock)
        {
            _accounts ??= _Load();

            // 同类型同 Uuid 视为同一账号，覆盖而非重复添加
            var existing = _accounts.FirstOrDefault(a =>
                a.Kind == account.Kind &&
                a.Uuid.Equals(account.Uuid, StringComparison.OrdinalIgnoreCase));

            if (existing is not null) _accounts.Remove(existing);

            _accounts.Add(account);
            _Save(_accounts);
        }

        SelectedId = account.Id;
        Changed?.Invoke();
    }

    /// <summary>更新已有账号（例如令牌续期后写回）。</summary>
    public static void Update(AccountEntry account)
    {
        lock (_lock)
        {
            _accounts ??= _Load();
            var index = _accounts.FindIndex(a => a.Id == account.Id);
            if (index < 0) return;
            _accounts[index] = account;
            _Save(_accounts);
        }

        Changed?.Invoke();
    }

    /// <summary>删除账号；若删除的是当前账号则自动切换到下一个。</summary>
    public static void Remove(string id)
    {
        lock (_lock)
        {
            _accounts ??= _Load();
            _accounts.RemoveAll(a => a.Id == id);
            _Save(_accounts);
        }

        if (SelectedId == id)
            SelectedId = Accounts.FirstOrDefault()?.Id;

        Changed?.Invoke();
    }

    /// <summary>
    /// 确保指定账号的令牌可用。过期或即将过期时自动续期，
    /// 续期失败则抛出 <see cref="AuthException"/> 让调用方引导重新登录。
    /// </summary>
    public static async Task<AccountEntry> EnsureValidAsync(AccountEntry account, CancellationToken ct = default)
    {
        if (account.Kind == AccountKind.Offline) return account;

        if (!account.IsExpired) return account;

        Logger.Info($"账号 {account.Name} 令牌已过期，尝试自动续期");

        var refreshed = account.Kind switch
        {
            AccountKind.Microsoft => await MicrosoftAuth.RefreshAsync(account, ct).ConfigureAwait(false),
            AccountKind.ThirdParty => await ThirdPartyAuth.RefreshAsync(account, ct).ConfigureAwait(false),
            _ => null
        };

        if (refreshed is null)
        {
            throw new AuthException(
                account.Kind == AccountKind.Microsoft
                    ? "微软账号登录已过期，请在账号页重新登录"
                    : "第三方账号登录已失效，请重新登录");
        }

        Update(refreshed);
        return refreshed;
    }

    /// <summary>离线账号的创建（UUID 由名字推导，与原版一致）。</summary>
    public static AccountEntry CreateOffline(string name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();

        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + clean));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        return new AccountEntry
        {
            Kind = AccountKind.Offline,
            Name = clean,
            Uuid = hex,
            AccessToken = "0"
        };
    }

    /// <summary>启动时自动续期所有可续期账号，失败不影响其它账号。</summary>
    public static async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var account in Accounts.ToList())
        {
            if (account.Kind == AccountKind.Offline) continue;
            if (!account.IsExpired) continue;

            try
            {
                await EnsureValidAsync(account, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"账号 {account.Name} 自动续期失败: {ex.Message}");
            }
        }
    }

    // ---------------- 持久化 ----------------

    private static List<AccountEntry> _Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];

            var json = File.ReadAllText(FilePath);
            return System.Text.Json.JsonSerializer.Deserialize<List<AccountEntry>>(json,
                       new System.Text.Json.JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })
                   ?? [];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "读取账号文件失败");
            return [];
        }
    }

    private static void _Save(List<AccountEntry> accounts)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            var json = System.Text.Json.JsonSerializer.Serialize(accounts,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "保存账号文件失败");
        }
    }
}

