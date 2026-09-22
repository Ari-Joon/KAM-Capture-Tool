using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using KamCapture.Services;
using KamCapture.Setup;
using KamCapture.Settings;
using KamCapture.UI;
using Forms = System.Windows.Forms;

namespace KamCapture
{
    public partial class App : Application
    {
        private static HotkeyService? _hotkeys;
        private static Forms.NotifyIcon? _tray;
        private static AppSettings _cfg = new();
        private static MainWindow? _main;
        private static Forms.ToolStripMenuItem? _trayUpdate;
        private static bool _balloonIsUpdate;

        /// <summary>Set once the application is really exiting; until then the home window only hides.</summary>
        public static bool IsQuitting { get; private set; }

        public static bool HasTray => _tray != null;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Clear the copy displaced by the last update, whatever mode we run in.
            Installer.CleanUpPreviousVersion();

            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error("Unhandled on the UI thread", args.Exception);
                MessageBox.Show("Something went wrong.\n\n" + args.Exception.Message +
                                "\n\nDetails were written to:\n" + Log.Path,
                    "KAM Capture Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex) Log.Error("Unhandled on a background thread", ex);
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error("Unobserved task", args.Exception);
                args.SetObserved();
            };

            // Diagnostics are headless and run alongside a copy that is already
            // in the tray. They never touch the single-instance lock: if they did,
            // a running copy would swallow them and they would exit reporting
            // success without having run at all.
            // Headless smoke test: renders every annotation kind to a PNG and exits.
            var selfTest = e.Args.FirstOrDefault(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase));
            if (selfTest != null)
            {
                var target = selfTest.Contains('=')
                    ? selfTest[(selfTest.IndexOf('=') + 1)..]
                    : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kam-selftest.png");
                Environment.ExitCode = SelfTest.Run(target);
                Shutdown();
                return;
            }

            if (e.Args.Any(a => a.Equals("--foldertest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = SelfTest.FolderTest();
                Shutdown();
                return;
            }

            if (e.Args.Any(a => a.Equals("--lifecycletest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = SelfTest.LifecycleTest();
                Shutdown();
                return;
            }

            if (e.Args.Any(a => a.Equals("--ghosttest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = SelfTest.GhostTest();
                Shutdown();
                return;
            }

            if (e.Args.Any(a => a.Equals("--savetest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = SelfTest.SaveTest();
                Shutdown();
                return;
            }

            if (e.Args.Any(a => a.Equals("--installtest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = SelfTest.InstallTest();
                Shutdown();
                return;
            }

            if (e.Args.Any(a => a.Equals("--updatetest", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = SelfTest.UpdateTest();
                Shutdown();
                return;
            }

            var recTest = e.Args.FirstOrDefault(a => a.StartsWith("--rectest=", StringComparison.OrdinalIgnoreCase));
            if (recTest != null)
            {
                var spec = recTest["--rectest=".Length..].Split(',');
                int secs = spec.Length > 1 && int.TryParse(spec[1], out var n) ? n : 4;
                Environment.ExitCode = SelfTest.RecordTest(spec[0], secs);
                Shutdown();
                return;
            }

            var docShots = e.Args.FirstOrDefault(a => a.StartsWith("--docshots", StringComparison.OrdinalIgnoreCase));
            if (docShots != null)
            {
                var dir = docShots.Contains('=') ? docShots[(docShots.IndexOf('=') + 1)..] : "docs/images";
                Environment.ExitCode = DocShots.Run(dir);
                Shutdown();
                return;
            }

            // Setup commands must act even when a copy is running, so ask it to
            // close rather than handing the command to it — handed over, an
            // uninstall would simply open the running copy's window.
            if (IsSetupCommand(e.Args))
            {
                if (!SingleInstance.AskRunningCopyToExit(TimeSpan.FromSeconds(10)))
                {
                    MessageBox.Show(
                        "KAM Capture Tool is still running. Close it from the notification area and try again.",
                        Installer.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
                if (HandleSetupArguments(e.Args)) return;
            }

            // A different copy opened plainly while one is running is almost
            // always a newer download. Handing it over would only bring the
            // running copy's window up, so offer to install over it instead;
            // the running copy is closed only if the user goes ahead.
            if (ShouldOfferUpdate(SingleInstance.IsAnotherCopyRunning(), Installer.InstalledDir,
                                  Installer.CurrentDir, e.Args))
            {
                Log.Info($"Offering {Installer.Version} over the running copy ({Installer.InstalledVersion})");
                new SetupWindow { ReplacesRunningCopy = true }.ShowDialog();
                Shutdown();
                return;
            }

            // One instance owns the global shortcuts. A second launch hands its
            // arguments over and exits quietly rather than showing a dialog.
            if (!SingleInstance.Claim())
            {
                SingleInstance.HandOver(e.Args);
                Shutdown();
                return;
            }
            SingleInstance.SecondInstance += OnSecondInstance;

            Log.Info("--- started (" + string.Join(" ", e.Args) + ")");
            _cfg = AppSettings.Load();

            if (!OfferInstall(e.Args)) return;

            if (!e.Args.Any(a => a.Equals("--no-tray", StringComparison.OrdinalIgnoreCase)))
                BuildTray();
            ReapplyHotkeys();

            bool startHidden = _cfg.StartMinimisedToTray ||
                               e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            _main = new MainWindow(_cfg);
            if (!startHidden) _main.Show();

            // A capture asked for straight from the command line.
            var mode = e.Args.FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase));
            if (mode != null && Enum.TryParse<SnipMode>(mode["--capture=".Length..], true, out var m))
                _ = CaptureController.RunAsync(m, _cfg);

            StartUpdates(e.Args);
        }

        private static bool IsSetupCommand(string[] args) =>
            args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--install-silent", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// True for the case that used to go wrong: a plain launch of a copy
        /// other than the installed one while the installed one is running.
        /// Anything with arguments is a command for the running copy and is
        /// still handed over.
        /// </summary>
        internal static bool ShouldOfferUpdate(bool anotherRunning, string? installedDir, string currentDir,
                                               string[] args)
        {
            if (!anotherRunning || string.IsNullOrWhiteSpace(installedDir) || args.Length > 0) return false;
            try
            {
                return !string.Equals(
                    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(installedDir)),
                    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(currentDir)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------------- updates ----------------

        private static void StartUpdates(string[] args)
        {
            Updater.CleanUpDownloads();
            UpdateService.Changed += OnUpdateChanged;
            UpdateService.Start(_cfg);

            var from = args.FirstOrDefault(a => a.StartsWith("--updated-from=", StringComparison.OrdinalIgnoreCase));
            if (from != null)
            {
                var previous = from["--updated-from=".Length..];
                Log.Info($"Now running {Updater.Current.ToString(3)}, updated from {previous}");
                _main?.ShowUpdated(previous);
                if (_main is { IsVisible: false })
                    Balloon($"KAM Capture Tool is now {Updater.Current.ToString(3)}", "Updated from " + previous + ".", update: false);
            }

            if (args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase)))
                _ = UpdateNowAsync();
        }

        /// <summary>
        /// --update on the command line: check, and install if there is
        /// something newer. Running the command is the yes.
        /// </summary>
        private static async System.Threading.Tasks.Task UpdateNowAsync()
        {
            if (!Installer.IsRunningInstalled)
            {
                Log.Info("--update ignored: this copy is not installed");
                return;
            }
            await UpdateService.CheckAsync(manual: true);
            if (UpdateService.Release != null)
                await UpdateService.InstallAsync(hidden: !(_main?.IsVisible ?? false), tray: HasTray);
        }

        private static void OnUpdateChanged()
        {
            var release = UpdateService.Release;
            bool offering = release != null && UpdateService.Now is UpdateService.Stage.Available
                or UpdateService.Stage.Downloading or UpdateService.Stage.Failed;

            if (_trayUpdate != null)
            {
                _trayUpdate.Visible = offering;
                if (release != null) _trayUpdate.Text = $"Update to {release.Tag}…";
            }

            // Announce each new version once, and only to someone not already
            // looking at the home window, where the bar says it anyway.
            if (UpdateService.Now == UpdateService.Stage.Available && release != null &&
                !UpdateService.IsSkipped && _cfg.AnnouncedUpdate != release.Tag &&
                !(_main?.IsVisible ?? false))
            {
                _cfg.AnnouncedUpdate = release.Tag;
                _cfg.Save();
                var summary = release.Summary;
                Balloon($"KAM Capture Tool {release.Tag} is available",
                    (summary.Length > 0 ? char.ToUpperInvariant(summary[0]) + summary[1..] + ". " : "") + "Click to update.",
                    update: true);
            }
        }

        private static void Balloon(string title, string text, bool update)
        {
            if (_tray == null) return;
            _balloonIsUpdate = update;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = text;
            _tray.ShowBalloonTip(8000);
        }

        /// <summary>Uninstall and silent-install run without any main window.</summary>
        private bool HandleSetupArguments(string[] args)
        {
            bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

            if (Has("--uninstall"))
            {
                if (!Has("--quiet"))
                {
                    var answer = MessageBox.Show(
                        "Remove KAM Capture Tool?\n\nYour captures and recordings are kept. " +
                        "Settings stay in your profile in case you reinstall.",
                        Installer.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.OK) { Shutdown(); return true; }
                }

                Log.Info("Uninstalling from " + Installer.CurrentDir);
                Installer.Uninstall();
                Shutdown();
                return true;
            }

            if (Has("--install-silent"))
            {
                try
                {
                    var exe = Installer.Install(Installer.Unattended());
                    Log.Info("Silent install to " + exe);
                    Console.WriteLine(exe);
                }
                catch (Exception ex) { Log.Error("Silent install", ex); }
                Shutdown();
                return true;
            }

            if (Has("--apply-update"))
            {
                // Started by the copy this one replaces, which has just closed.
                // Install over it, keeping every choice it had, then start the
                // new version the way the old one was showing.
                var from = Installer.InstalledVersion ?? "an earlier version";
                var show = (Has("--tray") ? " --tray" : "") + (Has("--no-tray") ? " --no-tray" : "");
                try
                {
                    var exe = Installer.Install(Installer.Unattended());
                    Log.Info($"Updated from {from} to {Installer.Version} at {exe}");
                    StartCopy(exe, "--updated-from=" + from + show);
                }
                catch (Exception ex)
                {
                    Log.Error("Applying the update", ex);
                    MessageBox.Show("The update could not be installed.\n\n" + ex.Message +
                                    "\n\nThe version you had is still in place.",
                        Installer.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);

                    // Put the old copy back on its feet rather than leave nothing running.
                    var dir = Installer.InstalledDir;
                    if (dir != null) StartCopy(System.IO.Path.Combine(dir, Installer.ExeName), show.Trim());
                }
                Shutdown();
                return true;
            }

            return false;
        }

        private static void StartCopy(string exe, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? ""
                });
            }
            catch (Exception ex) { Log.Error("Starting " + exe, ex); }
        }

        /// <summary>
        /// Run from a download folder, the application offers to install
        /// itself. Run from where it was installed, it just starts.
        /// Returns false when startup should stop here.
        /// </summary>
        private bool OfferInstall(string[] args)
        {
            bool portable = args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase));
            if (portable || _cfg.SkipSetupPrompt || Installer.IsRunningInstalled) return true;

            var setup = new SetupWindow();
            bool? result = setup.ShowDialog();

            if (result != true) { Shutdown(); return false; }

            if (!setup.RunPortable)
            {
                // The installed copy has been started; this one steps aside.
                Shutdown();
                return false;
            }

            _cfg.SkipSetupPrompt = true;
            _cfg.Save();
            return true;
        }

        /// <summary>Another copy was launched; do what it was asked to do.</summary>
        private static void OnSecondInstance(string[] args)
        {
            // An installer or uninstaller needs this copy out of the way.
            if (args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
            {
                Quit();
                return;
            }

            var mode = args.FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.OrdinalIgnoreCase));
            if (mode != null && Enum.TryParse<SnipMode>(mode["--capture=".Length..], true, out var m))
            {
                Start(m);
                return;
            }

            if (args.Any(a => a.Equals("--record", StringComparison.OrdinalIgnoreCase)))
            {
                _ = RecordingController.StartAsync(_cfg);
                return;
            }

            if (args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase)))
            {
                _ = UpdateNowAsync();
                return;
            }

            ShowMain();
        }

        // ---------------- tray ----------------

        private void BuildTray()
        {
            try
            {
                var menu = new Forms.ContextMenuStrip();

                // Only there while a new version is waiting.
                _trayUpdate = new Forms.ToolStripMenuItem("Update…", null, (_, _) => ShowUpdate()) { Visible = false };
                _trayUpdate.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
                menu.Items.Add(_trayUpdate);

                menu.Items.Add("New capture", null, (_, _) => Start(SnipMode.Region));
                menu.Items.Add("Capture a window", null, (_, _) => Start(SnipMode.Window));
                menu.Items.Add("Capture everything", null, (_, _) => Start(SnipMode.FullScreen));
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Record screen…", null, (_, _) => _ = RecordingController.StartAsync(_cfg));
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Open KAM Capture Tool", null, (_, _) => ShowMain());
                menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
                menu.Items.Add("Check for updates", null, (_, _) =>
                {
                    ShowMain();
                    if (_main != null) _ = _main.CheckForUpdatesAsync();
                });
                if (!Installer.IsRunningInstalled)
                    menu.Items.Add("Install KAM Capture Tool…", null, (_, _) => ShowSetup());
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Exit", null, (_, _) => Quit());

                _tray = new Forms.NotifyIcon
                {
                    Text = "KAM Capture Tool",
                    Visible = true,
                    ContextMenuStrip = menu,
                    Icon = LoadIcon()
                };
                _tray.DoubleClick += (_, _) => ShowMain();
                _tray.BalloonTipClicked += (_, _) => { if (_balloonIsUpdate) ShowUpdate(); };
            }
            catch
            {
                // No tray is survivable; the window still works.
            }
        }

        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                var info = GetResourceStream(new Uri("pack://application:,,,/Assets/kam-capture.ico"));
                if (info != null) return new System.Drawing.Icon(info.Stream);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private static void ShowMain()
        {
            if (_main == null) return;
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        }

        private static void ShowUpdate()
        {
            ShowMain();
            _main?.ShowUpdateBar();
        }

        private static void OpenSettings()
        {
            ShowMain();
            var w = new SettingsWindow(_cfg) { Owner = _main };
            if (w.ShowDialog() == true) ReapplyHotkeys();
        }

        private static void ShowSetup()
        {
            var setup = new SetupWindow();
            if (setup.ShowDialog() == true && !setup.RunPortable)
            {
                IsQuitting = true;
                Current.Shutdown();
            }
        }

        private static void Start(SnipMode mode) => _ = CaptureController.RunAsync(mode, _cfg);

        // ---------------- hotkeys ----------------

        public static void ReapplyHotkeys()
        {
            _hotkeys ??= new HotkeyService();
            _hotkeys.UnregisterAll();

            if (!_cfg.HotkeysEnabled) return;

            var failed = new System.Collections.Generic.List<string>();

            void Bind(string chord, Action action)
            {
                if (string.IsNullOrWhiteSpace(chord)) return;
                if (!_hotkeys!.Register(chord, action)) failed.Add(chord);
            }

            Bind(_cfg.HotkeyRegion, () => Start(SnipMode.Region));
            Bind(_cfg.HotkeyWindow, () => Start(SnipMode.Window));
            Bind(_cfg.HotkeyFullScreen, () => Start(SnipMode.FullScreen));
            Bind(_cfg.HotkeyRecord, () =>
            {
                if (RecordingController.IsRecording) RecordingController.StopActive();
                else _ = RecordingController.StartAsync(_cfg);
            });

            if (failed.Count > 0 && _tray != null)
            {
                _tray.BalloonTipTitle = "KAM Capture Tool";
                _tray.BalloonTipText = "Already in use by another program: " + string.Join(", ", failed);
                _tray.ShowBalloonTip(4000);
            }
        }

        private static void Quit()
        {
            if (RecordingController.IsRecording)
            {
                var answer = MessageBox.Show(
                    "A recording is still running. Stop it and exit?",
                    "KAM Capture Tool", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                RecordingController.StopActiveNow();
            }
            IsQuitting = true;
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _hotkeys?.Dispose(); } catch { }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            SingleInstance.Release();
            base.OnExit(e);
        }
    }
}
