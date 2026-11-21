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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

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

    /// <summary>
    /// 在指定进程的窗口里找出游戏主窗口。
    /// <para>
    /// 按<b>窗口类名</b>识别，与 PCL 的思路一致，但要同时覆盖两代渲染后端：
    /// 旧版用 GLFW/LWJGL，类名是 <c>GLFW30</c>、<c>LWJGL</c>；
    /// 新版（实测 26.3）改用 SDL，类名是 <c>SDL_app</c>。
    /// Mac 风格的 AWT 窗口类名是 <c>SunAwtFrame</c>。
    /// </para>
    /// <para>
    /// 必须同时要求窗口可见：SDL 会为同一个进程额外创建两个隐藏窗口
    /// （<c>Minecraft - RenderPearl OpenGL Hidden Utility Window</c> 以及一个空标题窗口），
    /// 枚举顺序在它们之前，只按类名取第一个会拿到隐藏窗口。
    /// </para>
    /// <para>
    /// 之前用的是「找该进程最大且带标题的可见窗口」这种启发式做法，
    /// 换一台机器就可能失效——部分环境下窗口标题要等很久才设置，
    /// 或者窗口尺寸小于阈值，于是永远匹配不到，表现为一直卡在「等待游戏窗口」。
    /// 类名在窗口创建时就确定，不受标题、尺寸、分辨率影响。
    /// </para>
    /// </summary>
    private static IntPtr FindGameWindow(uint processId)
    {
        var found = IntPtr.Zero;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                // 先按类名筛，绝大多数无关窗口在这一步就被排除
                var classBuffer = new System.Text.StringBuilder(256);
                GetClassName(hWnd, classBuffer, classBuffer.Capacity);
                var className = classBuffer.ToString();

                if (className is not ("GLFW30" or "LWJGL" or "SunAwtFrame" or "SDL_app"))
                    return true;

                // 隐藏窗口一律跳过：SDL 的辅助窗口类名同样是 SDL_app
                if (!IsWindowVisible(hWnd)) return true;

                var titleBuffer = new System.Text.StringBuilder(512);
                GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);
                var title = titleBuffer.ToString();

                // 排除 Forge 加载窗口、GLFW 消息窗口等辅助窗口
                if (title.StartsWith("FML", StringComparison.Ordinal)) return true;
                if (title.StartsWith("GLFW", StringComparison.Ordinal)) return true;
                if (title == "PopupMessageWindow") return true;
                if (title.Contains("Hidden Utility Window", StringComparison.OrdinalIgnoreCase))
                    return true;

                // 类名与可见性都通过，再确认是否属于目标进程
                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid != processId) return true;

                found = hWnd;
                return false; // 找到即停止枚举
            }, IntPtr.Zero);

            if (found != IntPtr.Zero) return found;

            // 类名没匹配上时退回原来的启发式，保证兼容性
            return FindLargestVisibleWindow(processId);
        }
        catch (Exception ex)
        {
            Logger.Debug($"枚举窗口失败: {ex.Message}");
            return found;
        }
    }

    /// <summary>
    /// 兜底：找该进程尺寸最大的可见窗口。
    /// 只在按类名匹配不到时使用，放宽了标题要求，
    /// 避免因为标题尚未设置而漏判。
    /// </summary>
    private static IntPtr FindLargestVisibleWindow(uint processId)
    {
        IntPtr best = IntPtr.Zero;
        var bestArea = 0;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != processId) return true;

            if (!IsWindowVisible(hWnd)) return true;

            RECT rect;
            if (!GetWindowRect(hWnd, out rect)) return true;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            // 过滤明显不是游戏的小窗口
            if (width < 160 || height < 120) return true;

            var area = width * height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }

            return true;
        }, IntPtr.Zero);

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
