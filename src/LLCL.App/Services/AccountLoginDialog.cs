using LLCL.Core;
using LLCL.Core.Account;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LLCL.Services;

/// <summary>
/// 账号登录对话框：微软设备码流程、第三方皮肤站密码登录。
/// </summary>
public static class AccountLoginDialog
{
    /// <summary>微软账号登录：展示设备码，用户在浏览器完成授权。</summary>
    public static async Task<bool> LoginMicrosoftAsync(XamlRoot? root)
    {
        if (root is null) return false;

        AccountEntry? result = null;

        var status = new TextBlock
        {
            Text = "正在获取验证码…",
            TextWrapping = TextWrapping.Wrap
        };

        var codeText = new TextBlock
        {
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 8, 0, 8)
        };

        var linkText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        panel.Children.Add(status);
        panel.Children.Add(codeText);
        panel.Children.Add(linkText);

        var copyButton = new Button
        {
            Content = "复制验证码",
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(copyButton);

        var dialog = new ContentDialog
        {
            Title = "微软账号登录",
            Content = panel,
            CloseButtonText = "取消",
            XamlRoot = root
        };

        using var cts = new CancellationTokenSource();

        var loginTask = Task.Run(async () =>
        {
            var session = await MicrosoftAuth.RequestDeviceCodeAsync(cts.Token);

            // 更新界面：显示验证码与链接
            dialog.DispatcherQueue.TryEnqueue(() =>
            {
                status.Text = "请在浏览器中打开下面的链接，并输入验证码：";
                codeText.Text = session.UserCode;
                linkText.Text = session.VerificationUriComplete;
                copyButton.Visibility = Visibility.Visible;
            });

            copyButton.Click += (_, _) =>
            {
                try
                {
                    var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    data.SetText(session.UserCode);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                }
                catch { }
            };

            // 自动打开浏览器
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = session.VerificationUriComplete,
                    UseShellExecute = true
                });
            }
            catch { }

            return await MicrosoftAuth.WaitForAuthorizationAsync(session, null, cts.Token);
        });

        // 用户关闭对话框时取消登录
        dialog.CloseButtonClick += (_, _) => cts.Cancel();

        _ = ShowAndCloseAsync(dialog, loginTask, cts);

        try
        {
            result = await loginTask;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (AuthException ex)
        {
            result = null;
            _ = ShowErrorAsync(root, ex.Message);
            return false;
        }

        if (result is not null)
        {
            AccountManager.Add(result);
            return true;
        }

        return false;
    }

    /// <summary>第三方皮肤站登录。</summary>
    public static async Task<bool> LoginThirdPartyAsync(XamlRoot? root)
    {
        if (root is null) return false;

        var serverBox = new TextBox
        {
            Header = "验证服务器",
            Text = ThirdPartyAuth.LittleSkinUrl,
            PlaceholderText = "https://littleskin.cn/api/yggdrasil",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var presetCombo = new ComboBox
        {
            Header = "常用站点",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var (name, url) in ThirdPartyAuth.Presets.Where(p => p.Url.Length > 0))
            presetCombo.Items.Add(name);
        presetCombo.SelectedIndex = 0;
        presetCombo.SelectionChanged += (_, _) =>
        {
            var index = presetCombo.SelectedIndex;
            var presets = ThirdPartyAuth.Presets.Where(p => p.Url.Length > 0).ToList();
            if (index >= 0 && index < presets.Count) serverBox.Text = presets[index].Url;
        };

        var emailBox = new TextBox
        {
            Header = "邮箱",
            PlaceholderText = "you@example.com",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var passwordBox = new PasswordBox
        {
            Header = "密码",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(presetCombo);
        panel.Children.Add(serverBox);
        panel.Children.Add(emailBox);
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title = "第三方账号登录",
            Content = panel,
            PrimaryButtonText = "登录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        var server = serverBox.Text.Trim();
        var email = emailBox.Text.Trim();
        var password = passwordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            await ShowErrorAsync(root, "请填写邮箱和密码");
            return false;
        }

        try
        {
            var account = await ThirdPartyAuth.LoginAsync(server, email, password);
            AccountManager.Add(account);
            return true;
        }
        catch (AuthException ex)
        {
            await ShowErrorAsync(root, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "第三方账号登录失败");
            await ShowErrorAsync(root, "登录失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>登录成功后自动关闭对话框。</summary>
    private static async Task ShowAndCloseAsync(ContentDialog dialog, Task<AccountEntry> loginTask,
        CancellationTokenSource cts)
    {
        var showTask = dialog.ShowAsync();

        try
        {
            await loginTask;
        }
        catch { /* 失败由主流程处理 */ }

        try { dialog.Hide(); } catch { }
        await showTask;
    }

    private static async Task ShowErrorAsync(XamlRoot root, string message)
    {
        var dialog = new ContentDialog
        {
            Title = "登录失败",
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = root
        };
        await dialog.ShowAsync();
    }
}


