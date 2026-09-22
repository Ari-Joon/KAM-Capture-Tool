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
        }

        private static bool IsSetupCommand(string[] args) =>
            args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--install-silent", StringComparison.OrdinalIgnoreCase));

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
                    var exe = Installer.Install(new Installer.Options());
                    Log.Info("Silent install to " + exe);
                    Console.WriteLine(exe);
                }
                catch (Exception ex) { Log.Error("Silent install", ex); }
                Shutdown();
                return true;
            }

            return false;
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

            ShowMain();
        }

        // ---------------- tray ----------------

        private void BuildTray()
        {
            try
            {
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("New capture", null, (_, _) => Start(SnipMode.Region));
                menu.Items.Add("Capture a window", null, (_, _) => Start(SnipMode.Window));
                menu.Items.Add("Capture everything", null, (_, _) => Start(SnipMode.FullScreen));
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Record screen…", null, (_, _) => _ = RecordingController.StartAsync(_cfg));
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Open KAM Capture Tool", null, (_, _) => ShowMain());
                menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
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

        private static void OpenSettings()
        {
            ShowMain();
            var w = new SettingsWindow(_cfg) { Owner = _main };
            if (w.ShowDialog() == true) ReapplyHotkeys();
        }

        private static void ShowSetup()
        {
            var setup = new SetupWindow();
            if (setup.ShowDialog() == true && !setup.RunPortable) Current.Shutdown();
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
