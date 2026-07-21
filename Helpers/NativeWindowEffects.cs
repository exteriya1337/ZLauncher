using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ZLauncher.Helpers;

/// <summary>
/// Системная DWM-анимация свернуть / развернуть для borderless-окна на Windows.
/// Хук WndProc — <b>на каждое окно отдельно</b> (не static один на всё),
/// иначе при splash→main CallWindowProc ломает процесс.
/// </summary>
public static class NativeWindowEffects
{
    private const int GWL_STYLE = -16;
    private const int GWLP_WNDPROC = -4;

    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_FRAMECHANGED = 0x0020;

    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_THICKFRAME = 0x00040000;

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_NCDESTROY = 0x0082;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_RESTORE = 0xF120;

    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_ENABLED = 2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly ConcurrentDictionary<IntPtr, IntPtr> OldWndProcs = new();
    // Keep delegate alive for process lifetime
    private static readonly WndProc SharedHook = HookedWndProc;

    public static void EnableSystemMinimizeAnimation(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero)
                return;

            ApplyDwmAndStyles(hwnd);
            InstallWndProcHook(hwnd);
        }
        catch
        {
            // ignore
        }
    }

    public static bool TryMinimizeAnimated(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero)
                return false;

            ApplyDwmAndStyles(hwnd);
            InstallWndProcHook(hwnd);
            return ShowWindow(hwnd, SW_MINIMIZE);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRestoreAnimated(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var hwnd = GetHwnd(window);
            if (hwnd == IntPtr.Zero)
                return false;

            ApplyDwmAndStyles(hwnd);
            var ok = ShowWindow(hwnd, SW_RESTORE);
            if (ok)
                SetForegroundWindow(hwnd);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyDwmAndStyles(IntPtr hwnd)
    {
        var disable = 0;
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disable, sizeof(int));

        var ncPolicy = DWMNCRP_ENABLED;
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(int));

        var margins = new MARGINS { cxLeftWidth = 1, cxRightWidth = 1, cyTopHeight = 1, cyBottomHeight = 1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style |= WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private static void InstallWndProcHook(IntPtr hwnd)
    {
        if (OldWndProcs.ContainsKey(hwnd))
            return;

        var newProc = Marshal.GetFunctionPointerForDelegate(SharedHook);
        var old = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newProc);
        if (old != IntPtr.Zero)
            OldWndProcs[hwnd] = old;
    }

    private static IntPtr HookedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_SYSCOMMAND)
        {
            var cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd == SC_MINIMIZE)
            {
                ShowWindow(hWnd, SW_MINIMIZE);
                return IntPtr.Zero;
            }

            if (cmd == SC_RESTORE)
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
                return IntPtr.Zero;
            }
        }

        if (msg == WM_NCDESTROY)
        {
            if (OldWndProcs.TryRemove(hWnd, out var old) && old != IntPtr.Zero)
            {
                // restore before destroy chain (best effort)
                try { SetWindowLongPtr(hWnd, GWLP_WNDPROC, old); } catch { /* ignore */ }
                return CallWindowProc(old, hWnd, msg, wParam, lParam);
            }
        }

        if (OldWndProcs.TryGetValue(hWnd, out var prev) && prev != IntPtr.Zero)
            return CallWindowProc(prev, hWnd, msg, wParam, lParam);

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr GetHwnd(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        return handle?.Handle ?? IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);
}
