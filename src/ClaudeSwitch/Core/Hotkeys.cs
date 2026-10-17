// Global keyboard shortcuts (Windows):
//   Ctrl+Alt+1 ... Ctrl+Alt+9   open or bring forward the profile in that position
//   Ctrl+Alt+0                  show Claude Switch
//
// Registered on a thread of their own with a plain Win32 message loop, so they
// work while every window is hidden. Off by default. macOS and Linux would need
// their own global-hotkey APIs, which aren't wired up yet.

using System.Runtime.InteropServices;

namespace ClaudeSwitch.Core;

public static class Hotkeys
{
    public static bool Supported => Platform.IsWindows;

    const int WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    static Thread thread;
    static uint threadId;

    /// <summary>Keys that another app already holds, so they couldn't be registered.</summary>
    public static List<string> Taken { get; } = new();

    // index: 0 = Claude Switch itself, 1..9 = profile in that position.
    public static void Start(Action<int> pressed)
    {
        if (!Supported || thread != null) return;
        var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            Taken.Clear();
            for (int i = 0; i <= 9; i++)
                if (!RegisterHotKey(IntPtr.Zero, i + 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)(0x30 + i)))
                    Taken.Add("Ctrl+Alt+" + i);
            ready.Set();
            while (GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0)
                if (m.message == WM_HOTKEY)
                {
                    int index = (int)m.wParam.ToUInt32() - 1;
                    try { pressed(index); } catch (Exception ex) { ErrorLog.Write(ex); }
                }
            for (int i = 0; i <= 9; i++) UnregisterHotKey(IntPtr.Zero, i + 1);
        }) { IsBackground = true, Name = "hotkeys" };
        thread.Start();
        ready.Wait(2000);
    }

    public static void Stop()
    {
        if (thread == null) return;
        PostThreadMessage(threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
        thread = null;
    }
}
