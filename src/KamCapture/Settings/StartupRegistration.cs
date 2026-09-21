using System;
using Microsoft.Win32;

namespace KamCapture.Settings
{
    /// <summary>Per-user Run key. No service, no scheduled task, no elevation.</summary>
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "KAM Capture Tool";

        public static void Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key == null) return;

                if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }

                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            catch { /* locked-down profile: silently leave it alone */ }
        }

        public static bool IsSet()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }
    }
}
