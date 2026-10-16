using System.Diagnostics;

namespace Bobcat.Ui.Hosting;

/// <summary>Opens a URL in the default browser, best-effort: a failure is reported, never thrown.</summary>
public static class Browser
{
    public static bool Open(string url)
    {
        try
        {
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url) { UseShellExecute = false };

            using var _ = Process.Start(info);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
