using System.Runtime.InteropServices;

namespace PingCandidateFinder.WinUI;

internal static class ClipboardCopyService
{
    internal static async Task<bool> TryCopyAsync(string value, Action<string> write,
        Func<TimeSpan, Task> delay)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                write(value);
                return true;
            }
            catch (COMException)
            {
                if (attempt < 2) await delay(TimeSpan.FromMilliseconds(80));
            }
        }
        return false;
    }
}
