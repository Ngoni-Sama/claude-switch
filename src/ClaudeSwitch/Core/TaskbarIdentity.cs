// Experimental (Windows): a taskbar button of its own per profile.
//
// Every Claude window shares one taskbar button and one icon, so you can't
// tell Work from Personal there. Windows decides grouping from a window's
// AppUserModelID; giving each profile's windows their own ID (plus a relaunch
// icon and name) splits them into separate buttons wearing the profile colour.
// These are ordinary window properties: nothing in Claude's files changes, and
// clearing them (or restarting the window) puts things back.

using System.Runtime.InteropServices;

namespace ClaudeSwitch.Core;

public static class TaskbarIdentity
{
    public static bool Supported => Platform.IsWindows;

    static readonly Guid FmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static PropertyKey Key(uint pid) => new() { fmtid = FmtId, pid = pid };
    const uint RelaunchCommand = 2, RelaunchIcon = 3, RelaunchName = 4, AppId = 5;

    public static string IdFor(ProfileEntry p) => "ClaudeSwitch.Profile." + p.Slug;

    /// <summary>Gives every top-level window of the profile's Claude its own taskbar identity. Returns how many windows changed.</summary>
    public static int Apply(ProfileEntry p, int pid, string iconPath)
    {
        if (!Supported || pid == 0) return 0;
        int n = 0;
        foreach (IntPtr hwnd in WindowsOf(pid))
        {
            if (!Set(hwnd, AppId, IdFor(p))) continue;
            if (iconPath != null) Set(hwnd, RelaunchIcon, iconPath + ",0");
            Set(hwnd, RelaunchName, Shortcuts.Label(p.Name));
            Set(hwnd, RelaunchCommand, "\"" + Platform.ExePath + "\" --launch \"" + p.Name + "\"");
            n++;
        }
        return n;
    }

    /// <summary>Puts the windows back on Claude's own taskbar button.</summary>
    public static int Clear(int pid)
    {
        if (!Supported || pid == 0) return 0;
        int n = 0;
        foreach (IntPtr hwnd in WindowsOf(pid))
        {
            foreach (uint k in new[] { RelaunchCommand, RelaunchIcon, RelaunchName }) Set(hwnd, k, null);
            if (Set(hwnd, AppId, null)) n++;
        }
        return n;
    }

    /// <summary>The ID a window carries now (for checking it took), or null.</summary>
    public static string Read(int pid)
    {
        foreach (IntPtr hwnd in WindowsOf(pid))
        {
            if (SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out IPropertyStore store) != 0 || store == null) continue;
            try
            {
                PropertyKey k = Key(AppId);
                if (store.GetValue(ref k, out PropVariant v) != 0) continue;
                try { if (v.vt == VT_LPWSTR && v.ptr != IntPtr.Zero) return Marshal.PtrToStringUni(v.ptr); }
                finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        return null;
    }

    static bool Set(IntPtr hwnd, uint keyId, string value)
    {
        if (SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out IPropertyStore store) != 0 || store == null) return false;
        var v = new PropVariant();
        try
        {
            if (value != null) { v.vt = VT_LPWSTR; v.ptr = Marshal.StringToCoTaskMemUni(value); }
            PropertyKey k = Key(keyId);
            return store.SetValue(ref k, ref v) == 0 && store.Commit() == 0;
        }
        catch (Exception ex) { ErrorLog.Write(ex); return false; }
        finally
        {
            if (v.ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(v.ptr);
            Marshal.ReleaseComObject(store);
        }
    }

    // Visible, unowned top-level windows of a process: the ones on the taskbar.
    static List<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)pid && IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---- interop --------------------------------------------------------------------

    const ushort VT_LPWSTR = 31;
    static Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid fmtid; public uint pid; }

    // PROPVARIANT: 16 bytes on 32-bit, 24 on 64-bit; the value union starts at 8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
}
