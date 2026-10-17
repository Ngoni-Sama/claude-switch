// Directory links, the mechanism behind unify mode: two profiles can point at
// one real folder, so memory and transcripts are genuinely shared rather than
// copied back and forth.
//
//   Windows        a junction (mklink /J). Works without admin rights and
//                  without Developer Mode, unlike a symlink.
//   macOS, Linux   a symlink.
//
// Deleting a link never touches the folder it points at: Delete(linkPath) is
// always safe, and the shared folder survives.

using System.Diagnostics;

namespace ClaudeSwitch.Core;

public static class FsLink
{
    public static bool IsLink(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception) { return false; }
    }

    // The folder a link points at, or null if it isn't a link.
    public static string Target(string path)
    {
        try
        {
            if (!IsLink(path)) return null;
            string target = new DirectoryInfo(path).LinkTarget;
            if (string.IsNullOrEmpty(target)) return null;
            return Platform.Normal(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(path)!, target));
        }
        catch (Exception) { return null; }
    }

    public static bool PointsAt(string path, string target) =>
        Target(path) is string t && Platform.SamePath(t, Platform.Normal(target));

    public static void Create(string linkPath, string targetDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Directory.CreateDirectory(targetDir);
        if (Platform.IsWindows)
        {
            // cmd's mklink, because .NET's CreateSymbolicLink makes a symlink,
            // which needs admin or Developer Mode. A junction needs neither.
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(linkPath);
            psi.ArgumentList.Add(targetDir);
            using Process p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (!IsLink(linkPath)) throw new IOException("Couldn't link " + linkPath + ": " + err.Trim());
            return;
        }
        Directory.CreateSymbolicLink(linkPath, targetDir);
        if (!IsLink(linkPath)) throw new IOException("Couldn't link " + linkPath + ".");
    }

    // Removes the link itself. The folder it points at is left alone.
    public static void Remove(string linkPath)
    {
        if (!IsLink(linkPath)) return;
        // Directory.Delete on a reparse point removes the link, not the target.
        Directory.Delete(linkPath);
    }

    // Copies a tree without overwriting anything that's already there.
    public static int MergeInto(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return 0;
        Directory.CreateDirectory(targetDir);
        int copied = 0;
        foreach (string src in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, src));
            if (File.Exists(dst)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst);
                copied++;
            }
            catch (IOException ex) { ErrorLog.Write(ex); }
            catch (UnauthorizedAccessException ex) { ErrorLog.Write(ex); }
        }
        return copied;
    }

    public static void CopyTree(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, dir)));
        foreach (string src in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(src, Path.Combine(targetDir, Path.GetRelativePath(sourceDir, src)), overwrite: true);
    }
}
