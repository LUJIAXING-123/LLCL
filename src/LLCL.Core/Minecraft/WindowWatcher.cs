using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LLCL.Core.Minecraft;

/// <summary>
/// 等待游戏主窗口出现。
/// <para>
/// Java 进程启动后要先初始化 JVM、加载资源、创建 GL 上下文，
/// 真正弹出窗口往往需要数秒。仅凭进程存在无法判断"游戏已启动"，
/// 因此这里轮询窗口句柄，出现后再认为启动完成。
/// </para>
/// </summary>
public static class WindowWatcher
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// 等待进程出现可见的、尺寸足够大的窗口。
    /// 过滤掉启动早期的小型辅助窗口（如 JVM 的控制台占位），
    /// 避免误判为"游戏已启动"。
    /// </summary>
    /// <returns>成功时返回窗口句柄，超时返回 <see cref="IntPtr.Zero"/>。</returns>
    public static async Task<IntPtr> WaitForGameWindowAsync(Process process,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.Now + timeout;

        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited) return IntPtr.Zero;

            var handle = FindGameWindow((uint)process.Id);
            if (handle != IntPtr.Zero) return handle;

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    /// <summary>在指定进程的所有可见窗口中，找尺寸最大的那个（即游戏主窗口）。</summary>
    private static IntPtr FindGameWindow(uint processId)
    {
        IntPtr best = IntPtr.Zero;
        var bestArea = 0;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != processId) return true;

                if (!IsWindowVisible(hWnd)) return true;

                // 必须有标题，排除隐藏的辅助窗口
                if (GetWindowTextLength(hWnd) <= 0) return true;

                RECT rect;
                if (!GetWindowRect(hWnd, out rect)) return true;

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;

                // 游戏窗口至少要有一定尺寸，过滤掉启动早期的小窗口
                if (width < 200 || height < 150) return true;

                var area = width * height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = hWnd;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Debug($"枚举窗口失败: {ex.Message}");
        }

        return best;
    }

    /// <summary>读取窗口标题，用于日志与错误提示。</summary>
    public static string GetTitle(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLength(hWnd);
            if (length <= 0) return string.Empty;

            var sb = new System.Text.StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
