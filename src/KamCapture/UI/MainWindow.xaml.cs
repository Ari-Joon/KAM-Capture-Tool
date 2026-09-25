using System;
using System.ComponentModel;
using System.Diagnostics;
using Activity = KamCapture.Settings.Activity;
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
using KamCapture.Recording;
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

        private Activity _activity = Activity.Screenshot;
        private bool _loading;

        // The status line: a message while it is fresh, otherwise the hint.
        private string? _message;
        private string _hint = "";

        // Lets the layout check draw the window mid-recording without recording.
        private bool _pretendRecording;

        private sealed record DeviceItem(string Label, string Id)
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

            LoadDevices();
            LoadFromSettings();

            CaptureController.Notified += Say;
            RecordingController.Notified += Say;
            RecordingController.StateChanged += OnRecordingStateChanged;
            UpdateService.Changed += RenderUpdates;
            UpdateBar.SizeChanged += (_, _) => FitToBar();

            // Closed, not Closing: closing is usually cancelled (see OnClosing).
            Closed += (_, _) =>
            {
                CaptureController.Notified -= Say;
                RecordingController.Notified -= Say;
                RecordingController.StateChanged -= OnRecordingStateChanged;
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
            _loading = true;

            foreach (var o in CmbDelay.Items)
                if (o is Item it && Equals(it.Value, _cfg.DelaySeconds)) { CmbDelay.SelectedItem = o; break; }
            if (CmbDelay.SelectedItem == null) CmbDelay.SelectedIndex = 0;

            ChkSystem.IsChecked = _cfg.RecordSystemAudio;
            ChkMic.IsChecked = _cfg.RecordMicrophone;
            SelectDevice(CmbOutput, _cfg.SystemAudioDeviceId);
            SelectDevice(CmbMic, _cfg.MicrophoneDeviceId);

            var mons = Screens.All();
            var (_, _, vw, vh) = Screens.VirtualBounds();

            // The scale this window is really rendered at, which is the number
            // that decides whether a selection keeps its pixels.
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            string scaling = Math.Abs(scale - 1.0) > 0.01 ? $" @ {scale * 100:0}% scaling" : "";

            LblSubtitle.Text = mons.Count == 1
                ? $"{mons[0].Describe()}{scaling} — captured at full device resolution"
                : $"{mons.Count} displays, {vw} × {vh} together{scaling} — captured at full device resolution";

            BtnOpenShots.ToolTip = _cfg.SaveFolder;
            BtnOpenVideo.ToolTip = _cfg.RecordFolder;
            BtnOpenAudio.ToolTip = _cfg.AudioFolder;

            _loading = false;
            ShowActivity(_cfg.LastActivity, animate: false, save: false);
        }

        // ---------------- the three activities ----------------

        /// <summary>Bring the window up on one activity. Used when something needs choosing first.</summary>
        public void ShowActivity(Activity activity)
        {
            ShowActivity(activity, animate: IsVisible, save: true);
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>Render one activity for the documentation screenshots.</summary>
        internal void PrepareActivityForDocShot(Activity activity) =>
            ShowActivity(activity, animate: false, save: false);

        /// <summary>Put the window in a state for the layout check to measure.</summary>
        internal void PrepareForLayoutCheck(Activity activity, bool recording, string? message, bool longDeviceNames = false)
        {
            if (longDeviceNames)
            {
                // Real device names run long: "Headset Earphone (Jabra Evolve2 65 with a very long suffix)".
                FillDevices(CmbOutput, "Headset Earphone (Some Very Long Bluetooth Hands-Free AG Audio Device Name)",
                    new System.Collections.Generic.List<AudioDevice>(), "");
                FillDevices(CmbMic, "Microphone Array (Intel® Smart Sound Technology for Digital Microphones)",
                    new System.Collections.Generic.List<AudioDevice>(), "");
            }
            _pretendRecording = recording;
            ShowActivity(activity, animate: false, save: false);
            _message = message;
            RenderStatus();
        }

        /// <summary>
        /// Pick what you are doing; everything else follows from it. Only the
        /// options that apply are shown, and the button says exactly what will
        /// happen when it is pressed.
        /// </summary>
        private void ShowActivity(Activity activity, bool animate, bool save)
        {
            _activity = activity;
            ActScreenshot.IsChecked = activity == Activity.Screenshot;
            ActVideo.IsChecked = activity == Activity.Video;
            ActAudio.IsChecked = activity == Activity.Audio;

            bool shot = activity == Activity.Screenshot;
            bool video = activity == Activity.Video;
            bool audio = activity == Activity.Audio;

            SetVisible(RowWhat, shot || video);
            SetVisible(RowDelay, shot);
            SetVisible(RowSystem, video || audio);
            SetVisible(RowMic, video || audio);
            SetVisible(LblNote, audio);

            SetWhat(shot ? _cfg.DefaultMode : _cfg.VideoMode);

            if (save && _cfg.LastActivity != activity)
            {
                _cfg.LastActivity = activity;
                _cfg.Save();
            }

            UpdatePrimary();
            UpdateHint();
            if (animate) FadeIn(Options);
        }

        private static void SetVisible(UIElement element, bool visible) =>
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>A 140ms fade: the motion vocabulary's state change, opacity only.</summary>
        private static void FadeIn(UIElement element)
        {
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        }

        private void OnActivityClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<Activity>(tag, out var activity))
                ShowActivity(activity, animate: activity != _activity, save: true);
        }

        private void SetWhat(SnipMode mode)
        {
            // Older settings may hold modes the home window no longer offers.
            mode = mode switch
            {
                SnipMode.FullScreen => SnipMode.Monitor,
                SnipMode.Freeform => SnipMode.Region,
                _ => mode
            };
            foreach (var tb in new[] { WhatRegion, WhatWindow, WhatScreen })
                tb.IsChecked = (tb.Tag as string) == mode.ToString();
        }

        private SnipMode CurrentWhat()
        {
            foreach (var tb in new[] { WhatRegion, WhatWindow, WhatScreen })
                if (tb.IsChecked == true && Enum.TryParse<SnipMode>(tb.Tag as string, out var m)) return m;
            return SnipMode.Region;
        }

        private void OnWhatClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse<SnipMode>(tag, out var mode)) return;
            SetWhat(mode);

            // Screenshots and videos each remember their own choice.
            if (_activity == Activity.Video) _cfg.VideoMode = mode;
            else _cfg.DefaultMode = mode;
            _cfg.Save();
        }

        // ---------------- sound ----------------

        private void LoadDevices()
        {
            FillDevices(CmbOutput, AudioDevices.EveryOutputLabel, AudioDevices.Outputs(), _cfg.SystemAudioDeviceId);
            FillDevices(CmbMic, AudioDevices.DefaultMicrophoneLabel(), AudioDevices.Inputs(), _cfg.MicrophoneDeviceId);
        }

        private void FillDevices(ComboBox box, string defaultLabel, System.Collections.Generic.List<AudioDevice> devices, string selectedId)
        {
            bool wasLoading = _loading;
            _loading = true;

            var items = new System.Collections.Generic.List<DeviceItem> { new(defaultLabel, "") };
            foreach (var d in devices) items.Add(new DeviceItem(d.Name, d.Id));
            box.ItemsSource = items;
            SelectDevice(box, selectedId);

            _loading = wasLoading;
        }

        private static void SelectDevice(ComboBox box, string id)
        {
            if (box.ItemsSource is not System.Collections.Generic.IEnumerable<DeviceItem> items) return;
            box.SelectedItem = items.FirstOrDefault(i => i.Id == id) ?? items.FirstOrDefault();
        }

        /// <summary>Headsets come and go; list what is plugged in at the moment the list opens.</summary>
        private void OnDevicesOpened(object? sender, EventArgs e)
        {
            if (sender == CmbOutput)
                FillDevices(CmbOutput, AudioDevices.EveryOutputLabel, AudioDevices.Outputs(), _cfg.SystemAudioDeviceId);
            else if (sender == CmbMic)
                FillDevices(CmbMic, AudioDevices.DefaultMicrophoneLabel(), AudioDevices.Inputs(), _cfg.MicrophoneDeviceId);
        }

        private void OnSoundToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _cfg.RecordSystemAudio = ChkSystem.IsChecked == true;
            _cfg.RecordMicrophone = ChkMic.IsChecked == true;
            _cfg.Save();
            UpdatePrimary();
        }

        private void OnOutputChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CmbOutput.SelectedItem is not DeviceItem item) return;
            _cfg.SystemAudioDeviceId = item.Id;
            _cfg.Save();
        }

        private void OnMicChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CmbMic.SelectedItem is not DeviceItem item) return;
            _cfg.MicrophoneDeviceId = item.Id;
            _cfg.Save();
        }

        // ---------------- the one button ----------------

        private void OnRecordingStateChanged() =>
            Dispatcher.BeginInvoke(new Action(UpdatePrimary));

        private void UpdatePrimary()
        {
            bool recording = _pretendRecording || RecordingController.IsRecording;
            bool anySource = ChkSystem.IsChecked == true || ChkMic.IsChecked == true;

            (string label, bool enabled) = _activity switch
            {
                Activity.Screenshot => ("Take screenshot", true),
                Activity.Video => (recording ? "Save recording" : "Start recording", true),
                _ => (recording ? "Save recording" : "Start recording audio", recording || anySource)
            };
            BtnPrimary.Content = label;
            BtnPrimary.IsEnabled = enabled;

            // Once it is running, sources are switched on the recording bar.
            RowSystem.IsEnabled = !recording;
            RowMic.IsEnabled = !recording;

            var format = (_cfg.AudioFormat ?? "mp3").ToUpperInvariant();
            LblNote.Text = recording
                ? "Recording. Retake, Save as… and Stop are on the bar at the bottom."
                : anySource
                    ? $"Saved as {format}. Either source can be switched on or off while it records."
                    : "Choose system audio, the microphone, or both.";
        }

        private void UpdateHint()
        {
            _hint = !_cfg.HotkeysEnabled
                ? ""
                : _activity switch
                {
                    Activity.Screenshot => _cfg.HotkeyRegion + " takes a screenshot from anywhere in Windows",
                    Activity.Video => _cfg.HotkeyRecord + " starts a video from anywhere, and saves it",
                    _ => ""
                };
            RenderStatus();
        }

        private void Say(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _message = message;
                RenderStatus();
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    if (_message != message) return;
                    _message = null;
                    RenderStatus();
                };
                t.Start();
            }));
        }

        private void RenderStatus()
        {
            var text = _message ?? _hint;
            LblStatus.Text = text;
            LblStatus.Foreground = (Brush)FindResource(_message != null ? "Fg" : "FgDim");
            LblStatus.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        }

        private async void OnPrimary(object sender, RoutedEventArgs e)
        {
            switch (_activity)
            {
                case Activity.Screenshot:
                    if (CmbDelay.SelectedItem is Item { Value: int d } && d != _cfg.DelaySeconds)
                    {
                        _cfg.DelaySeconds = d;
                        _cfg.Save();
                    }
                    await CaptureController.RunAsync(CurrentWhat(), _cfg);
                    break;

                case Activity.Video:
                    await RecordingController.StartVideoAsync(_cfg);
                    break;

                case Activity.Audio:
                    await RecordingController.StartAudioAsync(_cfg);
                    break;
            }
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

        private void OnOpenScreenshots(object sender, RoutedEventArgs e) => OpenFolder(_cfg.EnsureSaveFolder);
        private void OnOpenVideo(object sender, RoutedEventArgs e) => OpenFolder(_cfg.EnsureRecordFolder);
        private void OnOpenAudio(object sender, RoutedEventArgs e) => OpenFolder(_cfg.EnsureAudioFolder);

        /// <summary>Created if it is not there yet, so the button always goes somewhere.</summary>
        private void OpenFolder(Func<string> ensure)
        {
            try
            {
                var folder = ensure();
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
            BtnUpdates.ToolTip = stage switch
            {
                Stage.Downloading => $"Downloading {release?.Tag}. Click to cancel.",
                Stage.Installing => "Installing. KAM Capture Tool will close and reopen in a moment.",
                _ when offering => $"KAM Capture Tool {release!.Tag} is available",
                _ => $"Check for updates. You have {Updater.Current.ToString(3)}."
            };

            // ---- the bar: only when there is a decision to make ----
            // Downloading and installing are shown on the chip alone, with the
            // percentage; a second progress bar said the same thing twice.
            if (stage is Stage.Downloading or Stage.Installing)
            {
                UpdateBar.Visibility = Visibility.Collapsed;
                return;
            }

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
            // The chip is the only place a download shows, so it is where it stops.
            if (UpdateService.Now == Stage.Downloading)
            {
                UpdateService.Cancel();
                return;
            }

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
