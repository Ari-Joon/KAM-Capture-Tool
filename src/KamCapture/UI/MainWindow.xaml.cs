using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KamCapture.Capture;
using KamCapture.Interop;
using KamCapture.Services;
using KamCapture.Settings;
using KamCapture.Setup;
using Stage = KamCapture.Setup.UpdateService.Stage;

namespace KamCapture.UI
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _cfg;
        private readonly double _baseHeight;

        // Updates: whether the user asked to see the bar (the chip was clicked,
        // so show it even after "Not now"), whether to let "Up to date" stand
        // after a check they asked for, and the version this copy replaced.
        private bool _barWanted;
        private bool _justChecked;
        private string? _updatedFrom;
        private bool _spinning;

        private sealed record Item(string Label, object Value)
        {
            public override string ToString() => Label;
        }

        public MainWindow(AppSettings cfg)
        {
            InitializeComponent();
            _cfg = cfg;
            _baseHeight = Height;

            WindowStyling.ApplyDarkChrome(this);
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/kam-capture.ico")); } catch { }

            CmbDelay.ItemsSource = new[]
            {
                new Item("No delay", 0), new Item("1 second", 1), new Item("3 seconds", 3),
                new Item("5 seconds", 5), new Item("10 seconds", 10)
            };

            LoadFromSettings();

            CaptureController.Notified += Say;
            RecordingController.Notified += Say;
            RecordingController.StateChanged += UpdateRecordButton;
            UpdateService.Changed += RenderUpdates;
            UpdateBar.SizeChanged += (_, _) => FitToBar();

            // Closed, not Closing: closing is usually cancelled (see OnClosing).
            Closed += (_, _) =>
            {
                CaptureController.Notified -= Say;
                RecordingController.Notified -= Say;
                RecordingController.StateChanged -= UpdateRecordButton;
                UpdateService.Changed -= RenderUpdates;
            };

            Loaded += (_, _) => LoadFromSettings();
            RenderUpdates();
        }

        /// <summary>
        /// Closing the home window puts it away. The tool keeps running in the
        /// tray, and a window that has really closed can never be shown again —
        /// the next "Open" from the tray would have failed.
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!App.IsQuitting)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        private void LoadFromSettings()
        {
            SetMode(_cfg.DefaultMode);

            foreach (var o in CmbDelay.Items)
                if (o is Item it && Equals(it.Value, _cfg.DelaySeconds)) { CmbDelay.SelectedItem = o; break; }
            if (CmbDelay.SelectedItem == null) CmbDelay.SelectedIndex = 0;

            var mons = Screens.All();
            var (_, _, vw, vh) = Screens.VirtualBounds();

            // The scale this window is really rendered at, which is the number
            // that decides whether a selection keeps its pixels.
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            string scaling = Math.Abs(scale - 1.0) > 0.01 ? $" @ {scale * 100:0}% scaling" : "";

            LblSubtitle.Text = mons.Count == 1
                ? $"{mons[0].Describe()}{scaling} — captured at full device resolution"
                : $"{mons.Count} displays, {vw} × {vh} together{scaling} — captured at full device resolution";

            UpdateHotkeyHint();
            UpdateRecordButton();
        }

        private void UpdateHotkeyHint()
        {
            // One hint, not three: the full list is in Settings.
            LblHotkeys.Text = _cfg.HotkeysEnabled
                ? _cfg.HotkeyRegion + " anywhere in Windows"
                : "Global shortcuts are off";
        }

        private void UpdateRecordButton()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BtnRecord.Content = RecordingController.IsRecording ? "Recording…" : "Record screen";
                BtnRecord.IsEnabled = !RecordingController.IsRecording;
            }));
        }

        private void Say(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LblStatus.Text = message;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                t.Tick += (_, _) => { t.Stop(); if (LblStatus.Text == message) LblStatus.Text = ""; };
                t.Start();
            }));
        }

        private void SetMode(SnipMode mode)
        {
            foreach (var tb in new[] { ModeRegion, ModeWindow, ModeMonitor })
                tb.IsChecked = (tb.Tag as string) == mode.ToString();
        }

        private SnipMode CurrentMode()
        {
            foreach (var tb in new[] { ModeRegion, ModeWindow, ModeMonitor })
                if (tb.IsChecked == true && Enum.TryParse<SnipMode>(tb.Tag as string, out var m)) return m;
            return SnipMode.Region;
        }

        private void OnModeClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb) return;
            if (Enum.TryParse<SnipMode>(tb.Tag as string, out var m))
            {
                SetMode(m);
                _cfg.DefaultMode = m;
                _cfg.Save();
            }
        }


        private async void OnCapture(object sender, RoutedEventArgs e)
        {
            if (CmbDelay.SelectedItem is Item { Value: int d })
            {
                _cfg.DelaySeconds = d;
                _cfg.Save();
            }
            await CaptureController.RunAsync(CurrentMode(), _cfg);
        }

        private async void OnRecord(object sender, RoutedEventArgs e)
        {
            await RecordingController.StartAsync(_cfg);
        }

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_cfg) { Owner = this };
            if (w.ShowDialog() == true)
            {
                LoadFromSettings();
                App.ReapplyHotkeys();
            }
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = _cfg.EnsureLastOutputFolder();
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) { Say("Could not open the folder: " + ex.Message); }
        }

        // ---------------- updates ----------------

        /// <summary>From the tray or its notice: show the waiting update, even after "Not now".</summary>
        public void ShowUpdateBar()
        {
            _barWanted = true;
            RenderUpdates();
        }

        /// <summary>After an update, say so once in the bar.</summary>
        public void ShowUpdated(string from)
        {
            _updatedFrom = from;
            RenderUpdates();
        }

        /// <summary>Draw the bar and the chip as they look with an update waiting, for the README.</summary>
        internal void PrepareForDocShot(Updater.Release release)
        {
            Render(Stage.Available, release, 0, null, skipped: false);
            BtnBarPrimary.Content = "Update now";   // as an installed copy shows it
        }

        private void RenderUpdates()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RenderUpdates)); return; }
            Render(UpdateService.Now, UpdateService.Release, UpdateService.Progress,
                   UpdateService.Problem, UpdateService.IsSkipped);
        }

        private void Render(Stage stage, Updater.Release? release, double progress, string? problem, bool skipped)
        {
            bool offering = release != null &&
                stage is Stage.Available or Stage.Downloading or Stage.Installing or Stage.Failed;

            // ---- the chip: always there, gold when there is a version to get ----
            IconCheck.Visibility = offering ? Visibility.Collapsed : Visibility.Visible;
            IconDownload.Visibility = offering ? Visibility.Visible : Visibility.Collapsed;
            Spin(stage == Stage.Checking);

            string? label = stage switch
            {
                Stage.Checking => "Checking…",
                Stage.UpToDate when _justChecked => "Up to date",
                Stage.Failed when release == null && _justChecked => "Couldn't check",
                Stage.Downloading => $"Downloading {progress:0%}",
                Stage.Installing => "Restarting…",
                _ when offering => $"Update to {release!.Tag}",
                _ => null
            };
            LblUpdates.Text = label ?? "";
            LblUpdates.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
            BtnUpdates.Foreground = (Brush)FindResource(offering ? "Accent" : "FgDim");
            BtnUpdates.BorderBrush = offering ? (Brush)FindResource("AccentDeep") : Brushes.Transparent;
            BtnUpdates.ToolTip = offering
                ? $"KAM Capture Tool {release!.Tag} is available"
                : $"Check for updates. You have {Updater.Current.ToString(3)}.";

            // ---- the bar: only when there is something to act on ----
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateProgress.IsIndeterminate = false;
            BtnBarSecondary.Visibility = Visibility.Visible;
            BtnBarPrimary.Visibility = Visibility.Visible;
            BtnBarPrimary.IsEnabled = true;
            UpdateDot.Fill = (Brush)FindResource("Accent");

            if (!offering)
            {
                if (_updatedFrom != null)
                {
                    RunUpdate.Text = $"KAM Capture Tool is now {Updater.Current.ToString(3)}, updated from {_updatedFrom}. ";
                    BtnBarSecondary.Content = "Dismiss";
                    BtnBarPrimary.Visibility = Visibility.Collapsed;
                    UpdateBar.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdateBar.Visibility = Visibility.Collapsed;
                }
                return;
            }

            _updatedFrom = null;
            var tag = release!.Tag;
            switch (stage)
            {
                case Stage.Available:
                    var summary = release.Summary;
                    RunUpdate.Text = $"KAM Capture Tool {tag} is available" +
                                     (summary.Length > 0 ? " — " + summary : "") + ". It is free, as always. ";
                    BtnBarSecondary.Content = "Not now";
                    BtnBarPrimary.Content = Installer.IsRunningInstalled ? "Update now" : "Get the update";
                    break;

                case Stage.Downloading:
                    RunUpdate.Text = $"Downloading KAM Capture Tool {tag}… {progress:0%} ";
                    BtnBarSecondary.Content = "Cancel";
                    BtnBarPrimary.Visibility = Visibility.Collapsed;
                    UpdateProgress.Visibility = Visibility.Visible;
                    UpdateProgress.Value = progress;
                    break;

                case Stage.Installing:
                    RunUpdate.Text = $"Installing {tag}. KAM Capture Tool will close and reopen in a moment. ";
                    BtnBarSecondary.Visibility = Visibility.Collapsed;
                    BtnBarPrimary.Visibility = Visibility.Collapsed;
                    UpdateProgress.Visibility = Visibility.Visible;
                    UpdateProgress.IsIndeterminate = true;
                    break;

                case Stage.Failed:
                    UpdateDot.Fill = (Brush)FindResource("Danger");
                    RunUpdate.Text = (problem ?? "The update did not finish.") + " ";
                    BtnBarSecondary.Content = "Not now";
                    BtnBarPrimary.Content = "Try again";
                    break;
            }

            bool show = stage != Stage.Available || _barWanted || !skipped;
            UpdateBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnUpdatesClick(object sender, RoutedEventArgs e)
        {
            // A version is waiting: show its bar, even after "Not now".
            if (UpdateService.Release != null && UpdateService.Now != Stage.Checking)
            {
                _barWanted = true;
                RenderUpdates();
                return;
            }

            await CheckForUpdatesAsync();
        }

        /// <summary>A check the user asked for, from here or the tray: it answers either way.</summary>
        public async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            _justChecked = true;
            await UpdateService.CheckAsync(manual: true);
            if (UpdateService.Release != null) _barWanted = true;
            else if (UpdateService.Now == Stage.UpToDate) Say($"You're on the latest version, {Updater.Current.ToString(3)}.");
            else if (UpdateService.Problem != null) Say(UpdateService.Problem);
            RenderUpdates();

            // Let the answer stand for a moment, then go back to the quiet icon.
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) => { t.Stop(); _justChecked = false; RenderUpdates(); };
            t.Start();
        }

        private void OnBarPrimary(object sender, RoutedEventArgs e)
        {
            var release = UpdateService.Release;
            if (release == null || UpdateService.Now is not (Stage.Available or Stage.Failed)) return;

            // A copy that was never installed has nowhere to install over.
            if (!Installer.IsRunningInstalled) { OpenPage(release.Page); return; }
            if (!ConfirmClosingEditors()) return;

            _ = UpdateService.InstallAsync(hidden: !IsVisible, tray: App.HasTray);
        }

        private void OnBarSecondary(object sender, RoutedEventArgs e)
        {
            if (UpdateService.Now == Stage.Downloading)
            {
                UpdateService.Cancel();
                return;
            }

            if (UpdateService.Release == null)
            {
                _updatedFrom = null;      // "Dismiss" on the updated note
            }
            else
            {
                _barWanted = false;       // "Not now"
                UpdateService.Skip();
            }
            RenderUpdates();
        }

        private void OnWhatsNew(object sender, RoutedEventArgs e) =>
            OpenPage(UpdateService.Release?.Page ?? Updater.ReleasesPage);

        private void OpenPage(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Say("Could not open the browser: " + ex.Message); }
        }

        /// <summary>An update restarts the tool, so an open annotator would be lost. Ask first.</summary>
        private bool ConfirmClosingEditors()
        {
            int open = Application.Current.Windows.OfType<EditorWindow>().Count(w => w.IsVisible);
            if (open == 0) return true;

            var which = open == 1 ? "the annotator that is open" : $"the {open} annotators that are open";
            var answer = MessageBox.Show(this,
                $"Updating closes KAM Capture Tool, including {which}. Anything you have not saved or copied will be lost.\n\nUpdate now?",
                "KAM Capture Tool", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            return answer == MessageBoxResult.OK;
        }

        /// <summary>The bar adds its own height rather than squeezing the controls below it.</summary>
        private void FitToBar()
        {
            double extra = UpdateBar.Visibility == Visibility.Visible ? UpdateBar.ActualHeight : 0;
            Height = _baseHeight + extra;
        }

        private void Spin(bool on)
        {
            if (on == _spinning) return;
            _spinning = on;
            if (on)
            {
                SpinCheck.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                SpinCheck.BeginAnimation(RotateTransform.AngleProperty, null);
                SpinCheck.Angle = 0;
            }
        }
    }
}
