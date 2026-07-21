using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZLauncher.Helpers;

/// <summary>
/// Поиск и принудительный показ окна Minecraft (LWJGL / GLFW),
/// когда звук есть, а окна «нет» (HWND=0, за экраном, свёрнуто).
/// </summary>
public static class GameWindowHelper
{
    private const int SW_HIDE = 0;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int SW_SHOWDEFAULT = 10;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotopmost = new(-2);

    /// <summary>
    /// Несколько попыток найти окно процесса и вытащить его на главный монитор.
    /// </summary>
    public static async Task BringGameWindowAsync(
        Process process,
        int width = 1280,
        int height = 720,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // LWJGL 1.12 создаёт Display через 5–30+ сек после старта Java
        for (var attempt = 0; attempt < 60 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                if (process.HasExited)
                    return;

                process.Refresh();
                var hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    hwnd = FindBestWindowForPid(process.Id);

                if (hwnd != IntPtr.Zero)
                {
                    ForceShowOnPrimaryMonitor(hwnd, width, height);
                    // Повторно через секунду — Forge splash → main menu
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    if (!process.HasExited)
                    {
                        process.Refresh();
                        var hwnd2 = process.MainWindowHandle;
                        if (hwnd2 == IntPtr.Zero)
                            hwnd2 = FindBestWindowForPid(process.Id);
                        if (hwnd2 != IntPtr.Zero)
                            ForceShowOnPrimaryMonitor(hwnd2, width, height);
                    }

                    return;
                }
            }
            catch
            {
                // ignore
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    public static void ForceShowOnPrimaryMonitor(IntPtr hwnd, int width, int height)
    {
        if (hwnd == IntPtr.Zero)
            return;

        width = Math.Clamp(width, 854, 3840);
        height = Math.Clamp(height, 480, 2160);

        // Снять topmost у других, показать, восстановить
        ShowWindow(hwnd, SW_SHOW);
        ShowWindow(hwnd, SW_RESTORE);
        ShowWindow(hwnd, SW_SHOWNORMAL);

        // Центр primary work area
        var (x, y) = CenterOnPrimary(width, height);

        // Сначала topmost (гарантированно поверх), потом снять topmost
        SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        SetWindowPos(hwnd, HwndNotopmost, x, y, width, height, SWP_SHOWWINDOW);

        AllowSetForegroundWindow(ASFW_ANY);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        SetActiveWindow(hwnd);

        // Если окно было 0×0 / off-screen — ещё раз
        if (GetWindowRect(hwnd, out var rc))
        {
            var w = rc.Right - rc.Left;
            var h = rc.Bottom - rc.Top;
            if (w < 200 || h < 150 || rc.Right < 50 || rc.Bottom < 50 ||
                rc.Left > 10000 || rc.Top > 10000 || rc.Left < -5000 || rc.Top < -5000)
            {
                SetWindowPos(hwnd, HwndTop, x, y, width, height, SWP_SHOWWINDOW);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
    }

    /// <summary>
    /// Лучшее окно PID: видимое с площадью, иначе любое top-level (в т.ч. «невидимое»).
    /// </summary>
    public static IntPtr FindBestWindowForPid(int pid)
    {
        var candidates = new List<(IntPtr Hwnd, int Area, bool Visible, string Title, string Class)>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != (uint)pid)
                return true;

            // Пропускаем tool-окна без клиента, если есть style
            var style = GetWindowLong(hWnd, GWL_STYLE);
            // WS_CHILD
            if ((style & 0x40000000) != 0)
                return true;

            GetWindowRect(hWnd, out var rc);
            var w = Math.Max(0, rc.Right - rc.Left);
            var h = Math.Max(0, rc.Bottom - rc.Top);
            var area = w * h;
            var visible = IsWindowVisible(hWnd);

            var title = GetWindowText(hWnd);
            var cls = GetClassName(hWnd);

            // Игнор совсем крошечных (курсоры/тултипы)
            if (area > 0 && area < 100 && !cls.Contains("LWJGL", StringComparison.OrdinalIgnoreCase) &&
                !cls.Contains("GLFW", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase))
                return true;

            candidates.Add((hWnd, area, visible, title, cls));
            return true;
        }, IntPtr.Zero);

        if (candidates.Count == 0)
            return IntPtr.Zero;

        // Приоритет: Minecraft/LWJGL/GLFW → visible+largest → largest
        candidates.Sort((a, b) =>
        {
            int Score((IntPtr Hwnd, int Area, bool Visible, string Title, string Class) c)
            {
                var s = c.Area;
                if (c.Visible) s += 10_000_000;
                if (c.Title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) s += 5_000_000;
                if (c.Class.Contains("LWJGL", StringComparison.OrdinalIgnoreCase)) s += 4_000_000;
                if (c.Class.Contains("GLFW", StringComparison.OrdinalIgnoreCase)) s += 4_000_000;
                if (c.Class.Contains("SunAwt", StringComparison.OrdinalIgnoreCase)) s += 1_000_000;
                return s;
            }

            return Score(b).CompareTo(Score(a));
        });

        return candidates[0].Hwnd;
    }

    private static (int X, int Y) CenterOnPrimary(int width, int height)
    {
        try
        {
            var cx = GetSystemMetrics(SM_CXSCREEN);
            var cy = GetSystemMetrics(SM_CYSCREEN);
            if (cx > 0 && cy > 0)
            {
                var x = Math.Max(0, (cx - width) / 2);
                var y = Math.Max(0, (cy - height) / 2);
                return (x, y);
            }
        }
        catch
        {
            // ignore
        }

        return (80, 80);
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int GWL_STYLE = -16;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint ASFW_ANY = 0xFFFFFFFF;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    private static int GetWindowLong(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8)
            return (int)GetWindowLongPtr64(hWnd, nIndex);
        return GetWindowLong32(hWnd, nIndex);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
