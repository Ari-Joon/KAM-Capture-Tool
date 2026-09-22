using System;
using Microsoft.Win32;

namespace KamCapture.Settings
{
    /// <summary>Per-user Run key. No service, no scheduled task, no elevation.</summary>
    public static class StartupRegistration
    {
        private const string ValueName = "KAM Capture Tool";

        private static string RunKey => Setup.Installer.Where.RunKey;

        /// <summary>
        /// Start <paramref name="exe"/> at sign-in, or stop starting anything.
        /// Left out, the executable is this process — right for Settings, and
        /// wrong for the installer, which runs from wherever it was downloaded
        /// and so pointed sign-in at the download instead of the installed copy.
        /// </summary>
        public static void Set(bool enabled, string? exe = null)
        {
            try
            {
                if (!enabled)
                {
                    using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                    existing?.DeleteValue(ValueName, throwOnMissingValue: false);
                    return;
                }

                exe ??= Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                key?.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            catch { /* locked-down profile: silently leave it alone */ }
        }

        /// <summary>The command that runs at sign-in, or null when nothing does.</summary>
        public static string? Registered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) as string;
            }
            catch { return null; }
        }

        public static bool IsSet() => Registered() != null;
    }
}
