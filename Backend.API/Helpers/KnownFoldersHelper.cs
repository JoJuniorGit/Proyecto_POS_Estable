using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Backend.API.Helpers;

public static class KnownFoldersHelper
{
    private static readonly Guid DownloadsFolderGuid = new Guid("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out string ppszPath);

    public static string GetDownloadsFolderPath()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int hr = SHGetKnownFolderPath(DownloadsFolderGuid, 0, IntPtr.Zero, out string path);
                if (hr == 0 && !string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }
        catch
        {
            // Fallback if P/Invoke fails or running in constrained environment
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            return Path.Combine(userProfile, "Downloads");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
