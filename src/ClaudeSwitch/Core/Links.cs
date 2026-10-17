namespace ClaudeSwitch.Core;

public static class Links
{
    public const string Repo = "https://github.com/Ngoni-Sama/claude-switch";
    public const string Site = "https://ngoni-sama.github.io/claude-switch/";
    // The donation page is its own small site (Ngoni-Sama/buy-me-a-coffee,
    // hosted on Vercel). Update this if its address changes.
    public const string Support = "https://support-ngonie.vercel.app";
    public const string Issues = Repo + "/issues";
    public const string ClaudeDownload = "https://claude.ai/download";
    public const string ClaudeLinux = "https://code.claude.com/docs/en/desktop-linux";

    public static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception)
        {
            try
            {
                if (Platform.IsMac) System.Diagnostics.Process.Start("open", url);
                else if (Platform.IsLinux) System.Diagnostics.Process.Start("xdg-open", url);
            }
            catch (Exception ex) { ErrorLog.Write(ex); }
        }
    }
}
