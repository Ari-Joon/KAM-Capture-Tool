using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using KamCapture.Services;
using KamCapture.Settings;

namespace KamCapture.Setup
{
    /// <summary>
    /// When to ask, and what the answer was. Asks once per start, a few seconds
    /// in, as every KAM tool does; holds the result for the home window, the
    /// tray and Settings; and runs the download and hand-over when the user
    /// says yes.
    /// </summary>
    public static class UpdateService
    {
        public enum Stage { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

        public static Stage Now { get; private set; } = Stage.Idle;

        /// <summary>The newer release, once one has been found.</summary>
        public static Updater.Release? Release { get; private set; }

        public static double Progress { get; private set; }

        /// <summary>What went wrong, worded for the reader, when <see cref="Now"/> is Failed.</summary>
        public static string? Problem { get; private set; }

        /// <summary>Raised on the UI thread whenever any of the above changes.</summary>
        public static event Action? Changed;

        private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(5);

        /// <summary>How long the old copy waits to be closed by the new one before saying so.</summary>
        private static readonly TimeSpan HandOverLimit = TimeSpan.FromSeconds(60);

        private static AppSettings? _cfg;
        private static CancellationTokenSource? _download;

        /// <summary>
        /// One automatic check per start, and never again until the next one: an
        /// update matters, but not enough for a tray program to keep going back to
        /// the network all day. It often starts with Windows, before the network
        /// is up, so a check that finds no connection waits for one to arrive and
        /// then asks - once. The update button still asks whenever it is clicked.
        /// </summary>
        public static void Start(AppSettings cfg)
        {
            _cfg = cfg;
            if (!cfg.CheckForUpdates) return;

            var first = new DispatcherTimer { Interval = FirstCheck };
            first.Tick += async (_, _) =>
            {
                first.Stop();
                if (!await CheckAsync(manual: false) && !NetworkInterface.GetIsNetworkAvailable())
                    AskWhenConnected();
            };
            first.Start();
        }

        private static void AskWhenConnected()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            NetworkAvailabilityChangedEventHandler? handler = null;
            handler = (_, e) =>
            {
                if (!e.IsAvailable) return;
                NetworkChange.NetworkAvailabilityChanged -= handler;
                dispatcher.BeginInvoke(new Action(() => _ = CheckAsync(manual: false)));
            };
            NetworkChange.NetworkAvailabilityChanged += handler;
        }

        /// <summary>
        /// Ask GitHub. The automatic check fails quietly; a check the user
        /// asked for says what happened either way.
        /// Returns true when GitHub answered, whatever it said.
        /// </summary>
        public static async Task<bool> CheckAsync(bool manual)
        {
            if (Now is Stage.Checking or Stage.Downloading or Stage.Installing) return true;
            Set(Stage.Checking);
            try
            {
                var latest = await Updater.LatestAsync(CancellationToken.None);
                Log.Info($"Update check: latest is {latest.Tag}, this is {Updater.Current.ToString(3)}");
                Problem = null;
                Release = Updater.IsNewer(latest) ? latest : null;
                Set(Release != null ? Stage.Available : Stage.UpToDate);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("Update check did not complete: " + ex.Message);
                if (manual)
                {
                    Problem = "Could not check for updates. " + Describe(ex);
                    Set(Stage.Failed);
                }
                else
                {
                    Set(Release != null ? Stage.Available : Stage.Idle);
                }
                return false;
            }
        }

        /// <summary>
        /// Download, verify, and hand over to the new copy, which closes this
        /// one and installs itself. Returns once the new copy has started.
        /// </summary>
        public static async Task InstallAsync(bool hidden, bool tray)
        {
            var release = Release;
            if (release == null || Now is Stage.Downloading or Stage.Installing) return;

            if (RecordingController.IsRecording)
            {
                Problem = "Stop the recording first — updating closes KAM Capture Tool.";
                Set(Stage.Failed);
                return;
            }

            _download = new CancellationTokenSource();
            Problem = null;
            Progress = 0;
            Set(Stage.Downloading);
            try
            {
                var progress = new Progress<double>(p => { Progress = p; Changed?.Invoke(); });
                var exe = await Updater.DownloadAsync(release, progress, _download.Token);

                Log.Info($"Update {release.Tag} downloaded and verified; handing over to {exe}");
                Set(Stage.Installing);
                Updater.Launch(exe, hidden, tray);

                // The new copy asks this one to close through the same --exit
                // every setup command uses. If that never comes, say so.
                var watchdog = new DispatcherTimer { Interval = HandOverLimit };
                watchdog.Tick += (_, _) =>
                {
                    watchdog.Stop();
                    if (Now != Stage.Installing) return;
                    Problem = "The new version did not start. Try again, or download it from the release page.";
                    Set(Stage.Failed);
                };
                watchdog.Start();
            }
            catch (OperationCanceledException)
            {
                Set(Stage.Available);
            }
            catch (Exception ex)
            {
                Log.Error("Update", ex);
                Problem = Describe(ex);
                Set(Stage.Failed);
            }
            finally
            {
                _download?.Dispose();
                _download = null;
            }
        }

        public static void Cancel() => _download?.Cancel();

        /// <summary>"Not now": leave this version alone until a newer one lands.</summary>
        public static void Skip()
        {
            if (Release == null || _cfg == null) return;
            _cfg.SkippedUpdate = Release.Tag;
            _cfg.Save();
            Changed?.Invoke();
        }

        public static bool IsSkipped =>
            Release != null && _cfg != null && _cfg.SkippedUpdate == Release.Tag;

        private static string Describe(Exception ex) => ex switch
        {
            UpdateException u => u.Message,
            HttpRequestException => "GitHub could not be reached — check the connection and try again.",
            TaskCanceledException => "GitHub did not answer in time.",
            _ => ex.Message
        };

        private static void Set(Stage stage)
        {
            Now = stage;
            Changed?.Invoke();
        }
    }
}
