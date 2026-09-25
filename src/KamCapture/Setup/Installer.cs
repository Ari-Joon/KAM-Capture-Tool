using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KamCapture.Setup
{
    /// <summary>
    /// KAM Capture Tool installs itself. The download is one self-contained
    /// executable; run it from anywhere and it offers to put itself somewhere
    /// permanent, or to keep running where it stands. No second payload to
    /// carry and no bundled runtime to install.
    /// </summary>
    public static class Installer
    {
        public const string ProductName = "KAM Capture Tool";
        public const string ExeName = "KamCapture.exe";

        /// <summary>
        /// Everywhere an install writes. The install check swaps in a scratch
        /// copy, so it runs the real code without touching the real registry,
        /// desktop, Start menu or install folder.
        /// </summary>
        internal sealed record Footprint(
            string RegRoot, string UninstallRoot, string RunKey,
            string DefaultTarget, string DesktopFolder, string StartMenuFolder)
        {
            public static readonly Footprint Real = new(
                @"Software\KAM\Capture Tool",
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KAMCaptureTool",
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", ProductName),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs));

            /// <summary>Everything under one folder and one registry key, for the checks.</summary>
            public static Footprint In(string folder, string regRoot) => new(
                regRoot + @"\App", regRoot + @"\Uninstall", regRoot + @"\Run",
                Path.Combine(folder, "Programs", ProductName),
                Path.Combine(folder, "Desktop"), Path.Combine(folder, "Start Menu"));
        }

        internal static Footprint Where { get; set; } = Services.Sandbox.Active
            ? Footprint.In(Services.Sandbox.Root!, $@"Software\KAM\Capture Tool (sandbox {Services.Sandbox.Id})")
            : Footprint.Real;

        private static string RegRoot => Where.RegRoot;
        private static string UninstallRoot => Where.UninstallRoot;

        public static string Version =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        public static string DefaultTarget => Where.DefaultTarget;

        public static string CurrentExe => Environment.ProcessPath ?? "";

        /// <summary>
        /// True for the complete program — the single self-contained file that
        /// is released. False for a development build, whose .exe is a small
        /// launcher for the KamCapture.dll beside it. Only the complete program
        /// may install itself: copied on its own, the launcher cannot start, and
        /// an installed copy that cannot start is a desktop icon that does
        /// nothing. That happened once, from a test build; it cannot again.
        /// </summary>
        public static bool IsCompleteProgram =>
            Services.Sandbox.Active ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(CurrentExe) ?? "", "KamCapture.dll"));
        public static string CurrentDir => Path.GetDirectoryName(CurrentExe) ?? "";

        public static string? InstalledDir
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegRoot);
                    return key?.GetValue("InstallPath") as string;
                }
                catch { return null; }
            }
        }

        /// <summary>The version the installed copy registered, if there is one.</summary>
        public static string? InstalledVersion
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegRoot);
                    return key?.GetValue("Version") as string;
                }
                catch { return null; }
            }
        }

        /// <summary>True when this executable is the installed copy.</summary>
        public static bool IsRunningInstalled
        {
            get
            {
                var dir = InstalledDir;
                if (string.IsNullOrEmpty(dir)) return false;
                try
                {
                    return string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(CurrentDir)),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        public static string DesktopShortcut => Path.Combine(Where.DesktopFolder, ProductName + ".lnk");

        public static string StartMenuShortcut => Path.Combine(Where.StartMenuFolder, ProductName + ".lnk");

        // ------------------------------------------------------------

        public sealed class Options
        {
            public string TargetDir { get; set; } = DefaultTarget;
            public bool DesktopShortcut { get; set; } = true;
            public bool StartMenuShortcut { get; set; } = true;
            public bool StartWithWindows { get; set; }
        }

        /// <summary>
        /// The choices for an install nobody is watching. Over an existing
        /// install that is an update: the same folder, only the shortcuts that
        /// are still there, and start-with-Windows left as it was. With nothing
        /// installed, the defaults.
        /// </summary>
        public static Options Unattended()
        {
            var existing = InstalledDir;
            if (string.IsNullOrWhiteSpace(existing)) return new Options();

            return new Options
            {
                TargetDir = existing,
                DesktopShortcut = File.Exists(DesktopShortcut),
                StartMenuShortcut = File.Exists(StartMenuShortcut),
                StartWithWindows = Settings.StartupRegistration.IsSet()
            };
        }

        /// <summary>
        /// Copy this executable into place and wire up the shortcuts.
        /// Returns the installed executable's path.
        /// </summary>
        public static string Install(Options options, Action<string>? progress = null)
        {
            var dir = options.TargetDir.Trim();
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultTarget;

            progress?.Invoke("Creating " + dir);
            Directory.CreateDirectory(dir);

            var targetExe = Path.Combine(dir, ExeName);
            var sourceExe = CurrentExe;

            if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(targetExe),
                    StringComparison.OrdinalIgnoreCase))
            {
                progress?.Invoke("Copying the application");
                ReplaceExe(sourceExe, targetExe);
            }

            progress?.Invoke("Registering");
            using (var key = Registry.CurrentUser.CreateSubKey(RegRoot))
            {
                key?.SetValue("InstallPath", dir);
                key?.SetValue("Version", Version);
                key?.SetValue("InstalledOn", DateTime.Now.ToString("yyyy-MM-dd"));
            }

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallRoot))
            {
                if (key != null)
                {
                    key.SetValue("DisplayName", ProductName);
                    key.SetValue("DisplayVersion", Version);
                    key.SetValue("Publisher", "KAM");
                    key.SetValue("DisplayIcon", targetExe);
                    key.SetValue("InstallLocation", dir);
                    key.SetValue("UninstallString", $"\"{targetExe}\" --uninstall");
                    key.SetValue("QuietUninstallString", $"\"{targetExe}\" --uninstall --quiet");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try
                    {
                        key.SetValue("EstimatedSize",
                            (int)(new FileInfo(targetExe).Length / 1024), RegistryValueKind.DWord);
                    }
                    catch { }
                }
            }

            if (options.DesktopShortcut)
            {
                progress?.Invoke("Desktop shortcut");
                CreateShortcut(DesktopShortcut, targetExe, null, dir,
                    "Capture, annotate and record the screen");
            }

            if (options.StartMenuShortcut)
            {
                progress?.Invoke("Start menu shortcut");
                CreateShortcut(StartMenuShortcut, targetExe, null, dir,
                    "Capture, annotate and record the screen");
            }

            // The installed copy, not this one: the installer is usually running
            // from wherever it was downloaded to.
            progress?.Invoke("Start with Windows");
            Settings.StartupRegistration.Set(options.StartWithWindows, targetExe);

            progress?.Invoke("Done");
            return targetExe;
        }

        /// <summary>
        /// Put <paramref name="source"/> where <paramref name="target"/> is. A
        /// running executable cannot be overwritten but can be renamed, so the
        /// old one is moved aside first — and moved back if the copy fails, so
        /// a failed update never leaves the install folder without a program.
        /// </summary>
        internal static void ReplaceExe(string source, string target)
        {
            var stale = target + ".old";
            bool movedAside = false;
            if (File.Exists(target))
            {
                try { if (File.Exists(stale)) File.Delete(stale); } catch { }
                try { File.Move(target, stale); movedAside = true; } catch { }
            }

            try
            {
                File.Copy(source, target, overwrite: true);
            }
            catch
            {
                if (movedAside)
                {
                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    try { File.Move(stale, target); } catch { }
                }
                throw;
            }
        }

        /// <summary>
        /// Delete the copy displaced by the last update. It cannot be removed
        /// during the install because it may still be running, so it is cleared
        /// the next time the new copy starts.
        /// </summary>
        public static void CleanUpPreviousVersion()
        {
            try
            {
                var dir = CurrentDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                foreach (var stale in Directory.GetFiles(dir, "*.old"))
                {
                    try { File.Delete(stale); }
                    catch { /* still locked; it will go on a later run */ }
                }
            }
            catch { }
        }

        public static void Uninstall()
        {
            var dir = InstalledDir ?? CurrentDir;

            foreach (var lnk in new[] { DesktopShortcut, StartMenuShortcut })
            {
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            }

            Settings.StartupRegistration.Set(false);

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRoot, throwOnMissingSubKey: false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(RegRoot, throwOnMissingSubKey: false); } catch { }

            // The executable cannot delete itself while it is running, so hand
            // the job to a detached shell that waits for this process to exit.
            try
            {
                if (Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        $"/c timeout /t 3 /nobreak >nul & rd /s /q \"{dir}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetTempPath()
                    });
                }
            }
            catch { }
        }

        /// <summary>Write a .lnk through the shell, which needs nothing extra installed.</summary>
        public static void CreateShortcut(string linkPath, string target, string? arguments,
                                          string workingDirectory, string description)
        {
            try
            {
                var dir = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return;

                dynamic? shell = Activator.CreateInstance(type);
                if (shell == null) return;

                dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = target;
                link.Arguments = arguments ?? "";
                link.WorkingDirectory = workingDirectory;
                link.IconLocation = target + ",0";
                link.Description = description;
                link.Save();
            }
            catch { /* a missing shortcut is not worth failing an install over */ }
        }
    }
}
