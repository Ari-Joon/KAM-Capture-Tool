using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KamCapture.Editor;

namespace KamCapture.Services
{
    /// <summary>
    /// Renders one board containing every kind of annotation and writes it to a
    /// PNG. No windows, no screen capture — a smoke test for the drawing,
    /// grouping and export paths that can run anywhere, including CI.
    /// </summary>
    public static class SelfTest
    {
        public static int Run(string outputPath)
        {
            try
            {
                var doc = BuildSample();

                // Exercise grouping: bind three marks together, then move and
                // scale the group as one, which is what the editor does.
                var group = new GroupItem();
                group.Children.Add(new SymbolItem
                {
                    A = new Point(0, 0), B = new Point(70, 70),
                    Symbol = "circle", Filled = false, StrokeColor = "#2BB673", Thickness = 4
                });
                group.Children.Add(new LineItem
                {
                    A = new Point(70, 70), B = new Point(150, 120),
                    StrokeColor = "#2BB673", Thickness = 4, ArrowEnd = true
                });
                group.Children.Add(new TextItem
                {
                    Origin = new Point(150, 110), Text = "grouped", FontSize = 20,
                    StrokeColor = "#0B3D2A", BackgroundColor = "#C9F2DE"
                });

                var before = group.Bounds;
                group.Translate(560, 470);
                group.Scale(1.4, 1.4, new Point(560, 470));
                var after = group.Bounds;
                doc.Items.Add(group);

                double grew = after.Width / Math.Max(0.01, before.Width);
                if (Math.Abs(grew - 1.4) > 0.08)
                    return Fail($"group scaling wrong: expected ~1.40x, measured {grew:0.00}x");

                var png = doc.Export(2);
                if (png.PixelWidth != (int)Math.Ceiling(doc.BoardSize.Width * 2))
                    return Fail("export scale did not apply");

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                using (var fs = File.Create(outputPath))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(png));
                    enc.Save(fs);
                }

                Console.WriteLine($"self-test OK  ->  {outputPath}  ({png.PixelWidth} x {png.PixelHeight})");
                Console.WriteLine($"  items: {doc.Items.Count}, symbols in catalogue: {SymbolCatalog.All.Count}");
                return 0;
            }
            catch (Exception ex)
            {
                return Fail(ex.ToString());
            }
        }

        /// <summary>
        /// Record a small region for a few seconds with no interface at all, to
        /// prove the frame pump, the audio mixer and the ffmpeg pipeline
        /// actually produce a playable file.
        /// </summary>
        public static int RecordTest(string outputPath, int seconds, int pauseSeconds = 0)
        {
            try
            {
                var ffmpeg = Recording.FfmpegLocator.Resolve(null);
                if (ffmpeg == null) return Fail("ffmpeg not found");

                var cfg = Settings.AppSettings.Load();
                cfg.RecordFolder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
                cfg.FileNameTemplate = Path.GetFileNameWithoutExtension(outputPath);
                cfg.RecordFps = 30;
                cfg.RecordCursor = false;

                var target = Recording.RecordTarget.Region(new Int32Rect(0, 0, 640, 360));
                using var recorder = new Recording.ScreenRecorder(cfg, target, ffmpeg);

                string? failure = null;
                recorder.Failed += m => failure ??= m;

                Say($"recording {seconds}s of 640x360 to {recorder.OutputPath}" +
                    (pauseSeconds > 0 ? $", paused for {pauseSeconds}s in the middle" : ""));
                recorder.Start(systemAudio: true, micDeviceId: null);
                Say("  started");

                RunWithPause(seconds, pauseSeconds, recorder.Pause, recorder.Resume);

                Say("  stopping");
                string? path = null;
                var stopped = Task.Run(() => path = recorder.Stop());
                if (!stopped.Wait(TimeSpan.FromSeconds(30)))
                    return Fail("Stop() did not return within 30s");
                Say("  stopped");
                Say($"  frames written: {recorder.FramesWritten}, padded: {recorder.FramesDuplicated}");
                if (failure != null) Say("  reported: " + failure);

                if (path == null || !File.Exists(path)) return Fail("no output file");
                var bytes = new FileInfo(path).Length;
                Say($"  file: {path} ({bytes / 1024.0:0} KB)");
                if (bytes < 4096) return Fail("output file is suspiciously small");

                // A pause must leave its stretch out of the file, not fill it.
                double expected = (seconds - pauseSeconds) * 30.0;
                if (recorder.FramesWritten < expected * 0.9 || recorder.FramesWritten > expected * 1.1 + 2)
                    return Fail($"{recorder.FramesWritten} frames, expected about {expected:0}");

                Say("record-test OK");
                return 0;
            }
            catch (Exception ex) { return Fail(ex.ToString()); }
        }

        /// <summary>
        /// Record sound only, with no interface: switch system audio off and on
        /// part way through, pause in the middle, then check the track is exactly
        /// as long as the time actually recorded — no gap where the source was
        /// off, and no paused stretch.
        /// </summary>
        public static int AudioTest(string outputPath, int seconds, int pauseSeconds = 0)
        {
            var cfg = Settings.AppSettings.Load();
            var keepFolder = cfg.AudioFolder;
            var keepTemplate = cfg.FileNameTemplate;
            var keepFormat = cfg.AudioFormat;
            try
            {
                var ffmpeg = Recording.FfmpegLocator.Resolve(null);
                if (ffmpeg == null) return Fail("ffmpeg not found");

                cfg.AudioFolder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
                cfg.FileNameTemplate = Path.GetFileNameWithoutExtension(outputPath);
                cfg.AudioFormat = Path.GetExtension(outputPath).TrimStart('.');

                using var recorder = new Recording.ScreenRecorder(cfg, Recording.RecordTarget.AudioOnly(), ffmpeg);
                string? failure = null;
                recorder.Failed += m => failure ??= m;

                Say($"recording {seconds}s of audio to {recorder.OutputPath}" +
                    (pauseSeconds > 0 ? $", paused for {pauseSeconds}s" : ""));
                if (recorder.FormatNote != null) Say("  " + recorder.FormatNote);
                recorder.Start(systemAudio: true, micDeviceId: null);

                // Switch system audio off and back on, as the bar's toggle does.
                System.Threading.Thread.Sleep(700);
                recorder.Audio.Remove("system");
                Say("  system audio off");
                System.Threading.Thread.Sleep(500);
                recorder.Audio.AddSystemAudio("system", null, 1.0);
                Say("  system audio on");

                RunWithPause(seconds - 1.2, pauseSeconds, recorder.Pause, recorder.Resume);

                string? path = null;
                var stopped = Task.Run(() => path = recorder.Stop());
                if (!stopped.Wait(TimeSpan.FromSeconds(30)))
                    return Fail("Stop() did not return within 30s");
                if (failure != null) Say("  reported: " + failure);

                double recorded = recorder.Audio.FramesWritten / (double)Recording.AudioEngine.SampleRate;
                double expected = seconds - pauseSeconds;
                Say($"  track length: {recorded:0.00}s, expected {expected:0.00}s");

                if (path == null || !File.Exists(path)) return Fail("no output file");
                var bytes = new FileInfo(path).Length;
                Say($"  file: {path} ({bytes / 1024.0:0} KB)");
                if (bytes < 1024) return Fail("output file is suspiciously small");

                if (Math.Abs(recorded - expected) > 0.35)
                    return Fail($"track is {recorded:0.00}s, expected {expected:0.00}s");

                Say("audio-test OK");
                return 0;
            }
            catch (Exception ex) { return Fail(ex.ToString()); }
            finally
            {
                cfg.AudioFolder = keepFolder;
                cfg.FileNameTemplate = keepTemplate;
                cfg.AudioFormat = keepFormat;
            }
        }

        /// <summary>Wait out a recording, pausing for a stretch in the middle of it.</summary>
        private static void RunWithPause(double seconds, int pauseSeconds, Action pause, Action resume)
        {
            double running = Math.Max(0, seconds - pauseSeconds);
            Wait(running / 2);
            if (pauseSeconds > 0)
            {
                pause();
                Say("  paused");
                Wait(pauseSeconds);
                resume();
                Say("  resumed");
            }
            Wait(running / 2);
        }

        private static void Wait(double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end) System.Threading.Thread.Sleep(20);
        }

        /// <summary>Walk the exact path the Save button takes, and report where it breaks.</summary>
        public static int SaveTest()
        {
            try
            {
                var cfg = Settings.AppSettings.Load();
                Say("save folder : " + cfg.SaveFolder);
                Say("template    : " + cfg.FileNameTemplate);
                Say("export scale: " + cfg.ExportScale);

                var doc = BuildSample();
                Say("board       : " + doc.BoardSize.Width + " x " + doc.BoardSize.Height);

                var flat = doc.Export(cfg.ExportScale);
                Say("rendered    : " + flat.PixelWidth + " x " + flat.PixelHeight);

                Directory.CreateDirectory(cfg.SaveFolder);
                var name = cfg.BuildFileName(".png");
                Say("file name   : " + name);

                // The real folder, because that is what is being tested — but a
                // name no capture already has, and gone again afterwards, so
                // the sample never turns up among someone's screenshots.
                var path = UI.NameDialog.UniquePath(Path.Combine(cfg.SaveFolder, name));
                UI.EditorWindow.SaveTo(path, flat);

                if (!File.Exists(path)) return Fail("SaveTo returned but no file exists at " + path);
                Say("written     : " + path + "  (" + new FileInfo(path).Length / 1024 + " KB)");
                File.Delete(path);
                Say("removed     : the sample, now that it has been written");
                Say("save-test OK");
                return 0;
            }
            catch (Exception ex) { return Fail(ex.ToString()); }
        }

        /// <summary>
        /// The OneDrive rules: a folder Windows redirected into OneDrive is
        /// moved back to local disk, but one the user explicitly confirmed is
        /// honoured. Runs entirely in a scratch folder and never calls Save(),
        /// so the real settings file and captures folder are not touched.
        /// </summary>
        public static int FolderTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "kam-foldertest-" + Guid.NewGuid().ToString("N")[..8]);
            var failures = 0;
            void Expect(bool ok, string what)
            {
                Say((ok ? "  ok    " : "  FAIL  ") + what);
                if (!ok) failures++;
            }

            try
            {
                // "\OneDrive" anywhere in the path is enough for IsSynced.
                var synced = Path.Combine(root, "OneDrive", "Pictures", "KAM");
                var chosen = Path.Combine(root, "OneDrive", "Chosen");
                var local = Path.Combine(root, "Local", "Screenshots");
                Directory.CreateDirectory(synced);
                Directory.CreateDirectory(chosen);
                File.WriteAllText(Path.Combine(synced, "KAM-test.png"), "x");
                File.WriteAllText(Path.Combine(chosen, "KAM-keep.png"), "x");

                Expect(OutputFolder.IsSynced(synced), "a path inside OneDrive is recognised");
                Expect(!OutputFolder.IsSynced(local), "a local path is not");

                Say("redirected by Windows, never confirmed");
                var moved = Settings.AppSettings.Relocate(synced, local, ".png", _ => false);
                Expect(moved == local, "setting moves back to local disk");
                Expect(File.Exists(Path.Combine(local, "KAM-test.png")), "the tool's own capture comes with it");
                Expect(OutputFolder.Resolve(synced, OutputFolder.CapturesLeaf) != synced, "saving does not pick the synced folder");

                Say("chosen and confirmed in Settings");
                var cfg = new Settings.AppSettings();
                cfg.ConfirmedSyncedFolders.Add(chosen);
                Expect(cfg.IsChosen(chosen), "the confirmed folder is recognised");
                Expect(cfg.IsChosen(chosen + Path.DirectorySeparatorChar), "…with or without a trailing slash");
                Expect(!cfg.IsChosen(synced), "a different OneDrive folder is not covered by that yes");

                var kept = Settings.AppSettings.Relocate(chosen, local, ".png", cfg.IsChosen);
                Expect(kept == chosen, "setting is left alone at launch");
                Expect(File.Exists(Path.Combine(chosen, "KAM-keep.png")), "its files are not moved");
                Expect(OutputFolder.Resolve(chosen, OutputFolder.CapturesLeaf, honourPreferred: true) == chosen, "saving writes there");
            }
            catch (Exception ex) { return Fail(ex.ToString()); }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            if (failures > 0) return Fail(failures + " expectation(s) not met");
            Say("folder-test OK");
            return 0;
        }

        /// <summary>
        /// Install and update: the real code, pointed at a scratch registry key
        /// and scratch folders. The bugs this guards: ticking "Start with
        /// Windows" registered the downloaded file instead of the installed
        /// copy, and an unattended update moved the install to the default
        /// folder, put back a shortcut that had been deleted, and switched
        /// start-with-Windows off. Ends by checking the real install, its
        /// shortcuts and its sign-in entry are exactly as they were.
        /// </summary>
        public static int InstallTest()
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var root = Path.Combine(Path.GetTempPath(), "kam-installtest-" + id);
            var regRoot = $@"Software\KAM\Capture Tool (install check {id})";
            var real = Setup.Installer.Where;
            var before = Snapshot(real);

            var failures = 0;
            void Expect(bool ok, string what)
            {
                Say((ok ? "  ok    " : "  FAIL  ") + what);
                if (!ok) failures++;
            }

            try
            {
                Setup.Installer.Where = Setup.Installer.Footprint.In(root, regRoot);

                var chosen = Path.Combine(root, "Chosen", Setup.Installer.ProductName);
                string StartsAt() => Settings.StartupRegistration.Registered() ?? "nothing";

                Say("first install, run from the download, start with Windows ticked");
                var exe = Setup.Installer.Install(new Setup.Installer.Options { TargetDir = chosen, StartWithWindows = true });
                var wanted = $"\"{exe}\" --tray";
                Expect(!string.Equals(exe, Setup.Installer.CurrentExe, StringComparison.OrdinalIgnoreCase),
                    "the installed copy is a different file from the download");
                Expect(StartsAt() == wanted, "sign-in starts the installed copy" +
                    (StartsAt() == wanted ? "" : " — it starts " + StartsAt()));
                Expect(File.Exists(Setup.Installer.DesktopShortcut) && File.Exists(Setup.Installer.StartMenuShortcut),
                    "both shortcuts are made");

                Say("unattended update, after the desktop shortcut was deleted");
                File.Delete(Setup.Installer.DesktopShortcut);
                Setup.Installer.Install(Setup.Installer.Unattended());
                Expect(Setup.Installer.InstalledDir == chosen, "it updates the install where it is");
                Expect(!Directory.Exists(Setup.Installer.DefaultTarget), "nothing is put in the default folder");
                Expect(!File.Exists(Setup.Installer.DesktopShortcut), "the deleted shortcut stays deleted");
                Expect(File.Exists(Setup.Installer.StartMenuShortcut), "the Start menu entry is kept");
                Expect(StartsAt() == wanted, "start with Windows stays on");

                Say("unattended update, start with Windows off");
                Settings.StartupRegistration.Set(false);
                Setup.Installer.Install(Setup.Installer.Unattended());
                Expect(!Settings.StartupRegistration.IsSet(), "it stays off");

                Say("unattended install, nothing installed yet");
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Setup.Installer.Where.RegRoot, throwOnMissingSubKey: false);
                var fresh = Setup.Installer.Unattended();
                Expect(fresh.TargetDir == Setup.Installer.DefaultTarget && fresh.DesktopShortcut &&
                       fresh.StartMenuShortcut && !fresh.StartWithWindows,
                    "the defaults: default folder, both shortcuts, no sign-in entry");
            }
            catch (Exception ex) { Expect(false, ex.ToString()); }
            finally
            {
                Setup.Installer.Where = real;
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(regRoot, throwOnMissingSubKey: false); } catch { }
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            Expect(Snapshot(real) == before, "the real install, shortcuts and sign-in entry are untouched");

            if (failures > 0) return Fail(failures + " expectation(s) not met");
            Say("install-test OK");
            return 0;
        }

        /// <summary>
        /// The update path short of the network and the restart: versions are
        /// compared number by number, GitHub's reply is read correctly, a
        /// download that does not match its checksum is thrown away without a
        /// trace, a failed swap puts the old program back, and a download
        /// opened over a running copy is offered as an update instead of being
        /// handed over to it. The restart is scripts/test-update.ps1.
        /// </summary>
        public static int UpdateTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "kam-updatetest-" + Guid.NewGuid().ToString("N")[..8]);
            var failures = 0;
            void Expect(bool ok, string what)
            {
                Say((ok ? "  ok    " : "  FAIL  ") + what);
                if (!ok) failures++;
            }

            try
            {
                Directory.CreateDirectory(root);

                Say("versions");
                static Version? V(string s) => Setup.Updater.ParseVersion(s);
                Expect(V("v1.2.0") == new Version(1, 2, 0), "v1.2.0 reads as 1.2.0");
                Expect(V("1.10.0") > V("1.9.9"), "1.10.0 is newer than 1.9.9, not older");
                Expect(V("v2") == new Version(2, 0, 0), "v2 reads as 2.0.0");
                Expect(V("1.2.0-beta") == null && V("latest") == null && V("") == null,
                    "anything else is not a version");

                Say("GitHub's reply");
                var release = Setup.Updater.Parse(SampleRelease);
                Expect(release?.Version == new Version(1, 1, 2), "the version comes from the tag");
                Expect(release?.Download?.EndsWith("/v1.1.2/KamCapture.exe") == true,
                    "the download is the executable, not the checksum file");
                Expect(release?.Sha256 == "b98e5f950552f91be336946bb677bb737b5c9424d19ffd7a736fd49a1d91bccc",
                    "the checksum comes from GitHub's digest of the asset");
                Expect(release?.Summary == "half the memory", "the summary is the title without the name and number");
                Expect(Setup.Updater.Parse(SampleRelease.Replace("\"prerelease\": false", "\"prerelease\": true")) == null,
                    "a pre-release is not offered");
                Expect(Setup.Updater.Parse(SampleRelease.Replace("\"KamCapture.exe\"", "\"Other.exe\""))?.Download == null,
                    "a release without the executable has nothing to install");

                Say("download");
                var payload = Encoding.UTF8.GetBytes("stand-in for an executable");
                var good = Convert.ToHexString(SHA256.HashData(payload));
                var target = Path.Combine(root, "Updates", "KamCapture-9.9.9.exe");

                Run(() => Setup.Updater.SaveVerifiedAsync(new MemoryStream(payload), payload.Length, good,
                    target, null, CancellationToken.None));
                Expect(File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(payload),
                    "a download that matches its checksum is kept");

                File.Delete(target);
                Expect(Throws<Setup.UpdateException>(() => Setup.Updater.SaveVerifiedAsync(new MemoryStream(payload),
                        payload.Length, new string('0', 64), target, null, CancellationToken.None)),
                    "a download that does not match is refused");
                Expect(!File.Exists(target) && !File.Exists(target + ".partial"), "…and nothing of it is left on disk");

                Expect(Throws<Setup.UpdateException>(() => Setup.Updater.SaveVerifiedAsync(new MemoryStream(payload),
                        payload.Length, null, target, null, CancellationToken.None)) && !File.Exists(target),
                    "a download with no published checksum is refused");

                Say("swapping the program");
                var installed = Path.Combine(root, "Programs", "KamCapture.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
                File.WriteAllText(installed, "old version");
                Expect(Throws<Exception>(() =>
                    {
                        Setup.Installer.ReplaceExe(Path.Combine(root, "missing.exe"), installed);
                        return Task.CompletedTask;
                    }), "a swap from a file that is not there fails");
                Expect(File.Exists(installed) && File.ReadAllText(installed) == "old version",
                    "…and the old program is back in its place");

                var fresh = Path.Combine(root, "new.exe");
                File.WriteAllText(fresh, "new version");
                Setup.Installer.ReplaceExe(fresh, installed);
                Expect(File.ReadAllText(installed) == "new version", "a good swap puts the new program in place");

                Say("a download opened while a copy is running");
                var home = Path.GetDirectoryName(installed)!;
                var downloads = Path.Combine(root, "Downloads");
                var plain = Array.Empty<string>();
                Expect(App.ShouldOfferUpdate(true, home, downloads, plain), "it is offered as an update");
                Expect(!App.ShouldOfferUpdate(true, home, home, plain), "the installed copy opened again is handed over");
                Expect(!App.ShouldOfferUpdate(true, home, downloads, new[] { "--capture=Region" }),
                    "a command is still handed over");
                Expect(!App.ShouldOfferUpdate(false, home, downloads, plain), "with nothing running it starts as usual");
                Expect(!App.ShouldOfferUpdate(true, null, downloads, plain), "with nothing installed it is handed over");
            }
            catch (Exception ex) { Expect(false, ex.ToString()); }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            if (failures > 0) return Fail(failures + " expectation(s) not met");
            Say("update-test OK");
            return 0;
        }

        /// <summary>GitHub's reply for 1.1.2, trimmed to the fields the updater reads.</summary>
        private const string SampleRelease = """
            {
              "html_url": "https://github.com/Ari-Joon/KAM-Capture-Tool/releases/tag/v1.1.2",
              "tag_name": "v1.1.2",
              "name": "KAM Capture Tool 1.1.2 - half the memory",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "KamCapture.exe",
                  "size": 178351621,
                  "digest": "sha256:b98e5f950552f91be336946bb677bb737b5c9424d19ffd7a736fd49a1d91bccc",
                  "browser_download_url": "https://github.com/Ari-Joon/KAM-Capture-Tool/releases/download/v1.1.2/KamCapture.exe"
                },
                {
                  "name": "SHA256SUMS.txt",
                  "size": 82,
                  "digest": "sha256:370a51da98416bb7cd67a5229bb9022399343b9a5288ee819e21107fae1eedf4",
                  "browser_download_url": "https://github.com/Ari-Joon/KAM-Capture-Tool/releases/download/v1.1.2/SHA256SUMS.txt"
                }
              ]
            }
            """;

        // Async work run from the UI thread, off it: awaiting on the dispatcher
        // while blocking it for the result would deadlock.
        private static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

        private static bool Throws<T>(Func<Task> work) where T : Exception
        {
            try { Run(work); return false; }
            catch (T) { return true; }
            catch { return false; }
        }

        /// <summary>A fingerprint of an install, to prove a check left it alone.</summary>
        private static string Snapshot(Setup.Installer.Footprint where)
        {
            string Value(string key, string name)
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
                    return k?.GetValue(name)?.ToString() ?? "-";
                }
                catch { return "?"; }
            }

            string Stamp(string file) => File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks.ToString() : "-";

            var link = Setup.Installer.ProductName + ".lnk";
            return string.Join(" | ",
                Value(where.RegRoot, "InstallPath"), Value(where.RegRoot, "Version"),
                Value(where.UninstallRoot, "DisplayVersion"), Value(where.RunKey, "KAM Capture Tool"),
                Stamp(Path.Combine(where.DesktopFolder, link)), Stamp(Path.Combine(where.StartMenuFolder, link)),
                Stamp(Path.Combine(where.DefaultTarget, Setup.Installer.ExeName)));
        }

        /// <summary>
        /// Drive the real capture-result code, minus the interactive overlay,
        /// and check the windows end up where they should. The bug this guards:
        /// after one capture the home window never came back, so the only way
        /// to take a second was to restart the application.
        /// </summary>
        public static int LifecycleTest()
        {
            var failures = new System.Collections.Generic.List<string>();
            void Expect(bool ok, string what)
            {
                Say((ok ? "  ok    " : "  FAIL  ") + what);
                if (!ok) failures.Add(what);
            }

            // Keep the clipboard and the captures folder out of it.
            var cfg = new Settings.AppSettings
            {
                CopyToClipboardOnCapture = false,
                AutoSave = false,
                OpenEditorAfterCapture = true
            };

            UI.MainWindow? home = null;
            try
            {
                home = new UI.MainWindow(cfg);
                home.Show();
                Pump(300);

                Say("one capture, then close the annotator");
                var first = CaptureOnce(cfg);
                Expect(!home.IsVisible, "home steps aside while annotating");
                Expect(first?.IsVisible == true, "annotator is open");

                first?.Close();
                Pump(300);
                Expect(home.IsVisible, "home comes back when the annotator closes");

                Say("capture from the home window, then again from the annotator");
                var a = CaptureOnce(cfg);
                var b = CaptureOnce(cfg);
                Expect(a?.IsVisible == true, "first annotator is brought back, work intact");
                Expect(b?.IsVisible == true, "second annotator is open");
                Expect(!home.IsVisible, "home stays aside while any annotator is open");

                b?.Close();
                Pump(300);
                Expect(!home.IsVisible, "closing one of two annotators leaves home aside");

                a?.Close();
                Pump(300);
                Expect(home.IsVisible, "closing the last annotator brings home back");

                Say("close the home window, then open it from the tray");
                home.Close();
                Pump(200);
                Expect(!home.IsVisible, "closing puts it away");
                home.Show();
                Pump(200);
                Expect(home.IsVisible, "it opens again, rather than being gone for good");
            }
            catch (Exception ex) { return Fail(ex.ToString()); }
            finally
            {
                foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
                    try { w.Close(); } catch { }
            }

            if (failures.Count > 0) return Fail(failures.Count + " expectation(s) not met");
            Say("lifecycle-test OK");
            return 0;
        }

        /// <summary>
        /// What CaptureController.RunAsync does around the overlay: clear our
        /// windows, then hand a finished selection to the result handling.
        /// </summary>
        private static UI.EditorWindow? CaptureOnce(Settings.AppSettings cfg)
        {
            var before = Application.Current.Windows.OfType<UI.EditorWindow>().ToHashSet();

            var ours = Application.Current.Windows.OfType<Window>()
                .Where(w => w is UI.MainWindow or UI.EditorWindow or UI.SettingsWindow)
                .ToList();
            var hidden = CaptureController.ClearTheGlass(ours);
            CaptureController.ReleaseTheGlass(hidden);

            var result = new Capture.CaptureResult
            {
                Action = Capture.CaptureAction.Edit,
                Image = SampleCapture(),
                Region = new Int32Rect(0, 0, 520, 300)
            };

            var task = CaptureController.HandleResultAsync(result, cfg, hidden);
            while (!task.IsCompleted) Pump(20);
            Pump(300);

            return Application.Current.Windows.OfType<UI.EditorWindow>().FirstOrDefault(w => !before.Contains(w));
        }

        /// <summary>
        /// Measure how much of one of our own windows survives into a desktop
        /// grab taken straight after hiding it — the "ghost" that appeared in
        /// captures. Each strategy is scored as the fraction of the window's
        /// contrast that is still there: 0% means the grab shows exactly what
        /// was behind the window, 100% means the window was captured whole.
        /// </summary>
        public static int GhostTest(int trials = 3)
        {
            try
            {
                var strategies = new (string Name, Action<Window> Hide)[]
                {
                    ("hide, wait 160 ms (as shipped)", w =>
                    {
                        w.Hide();
                        Pump(160);
                    }),
                    ("exclude from capture only, still shown", w =>
                    {
                        Interop.WindowStyling.ExcludeFromCapture(w, true);
                        Interop.NativeMethods.DwmFlush();
                        Interop.NativeMethods.DwmFlush();
                    }),
                    ("hide, wait 2 frames, animation on", w =>
                    {
                        w.Hide();
                        Interop.NativeMethods.DwmFlush();
                        Interop.NativeMethods.DwmFlush();
                    }),
                    ("hide, wait 2 frames, animation off", w =>
                    {
                        Interop.WindowStyling.SetTransitionsDisabled(w, true);
                        w.Hide();
                        Interop.NativeMethods.DwmFlush();
                        Interop.NativeMethods.DwmFlush();
                    }),
                    ("fixed: CaptureController.ClearTheGlass", w =>
                    {
                        CaptureController.ClearTheGlass(new[] { w });
                    }),
                };

                Say($"{"strategy",-44}  worst ghost over {trials} trials");
                bool fixedPasses = true;

                foreach (var (name, hide) in strategies)
                {
                    double worst = 0;
                    for (int t = 0; t < trials; t++)
                    {
                        double ghost = MeasureGhost(hide);
                        if (double.IsNaN(ghost)) return Fail("test window never appeared on screen");
                        worst = Math.Max(worst, ghost);
                    }

                    Say($"{name,-44}  {worst * 100,6:0.0}%");
                    if (name.StartsWith("fixed") && worst > 0.02) fixedPasses = false;
                }

                if (!fixedPasses) return Fail("the fixed path still leaves a ghost above 2%");
                Say("ghost-test OK");
                return 0;
            }
            catch (Exception ex) { return Fail(ex.ToString()); }
        }

        private static double MeasureGhost(Action<Window> hide)
        {
            // Built like the home window — a normal title bar, activated, not
            // topmost — because Windows only animates hiding for windows like
            // that. A borderless test window vanishes instantly and proves
            // nothing: every strategy scored 0% against one.
            var area = SystemParameters.WorkArea;
            var w = new Window
            {
                Title = "KAM ghost test",
                WindowStyle = WindowStyle.SingleBorderWindow,
                ResizeMode = ResizeMode.CanMinimize,
                ShowInTaskbar = false,
                ShowActivated = true,
                // Topmost only so every trial is actually visible: a background
                // process is not allowed to take the foreground more than once,
                // and a window left behind another one measures nothing.
                Topmost = true,
                Width = 300,
                Height = 200,
                Left = area.Right - 324,
                Top = area.Bottom - 224,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF))
            };
            Interop.WindowStyling.ApplyDarkChrome(w);

            try
            {
                // First show only to learn where the window lands in device
                // pixels; then take it away instantly to read the background.
                w.Show();
                w.Activate();
                Pump(300);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                var (bx, by, bw, bh) = Capture.WindowFinder.GetWindowBounds(hwnd);
                var rect = new Int32Rect(bx, by, bw, bh);

                Interop.WindowStyling.SetTransitionsDisabled(w, true);
                w.Hide();
                Pump(250);
                var behind = Grab(rect);

                // Show it properly, fade and all, and let the fade finish.
                Interop.WindowStyling.SetTransitionsDisabled(w, false);
                w.Show();
                w.Activate();
                Pump(800);
                var shown = Grab(rect);

                hide(w);
                var after = Grab(rect);

                double full = MeanDifference(shown, behind);
                if (full < 20) return double.NaN;
                return Math.Clamp(MeanDifference(after, behind) / full, 0, 1);
            }
            finally
            {
                Interop.WindowStyling.ExcludeFromCapture(w, false);
                w.Close();
                Pump(150);
            }
        }

        private static byte[] Grab(Int32Rect rect)
        {
            var snap = Capture.ScreenGrabber.CaptureVirtualDesktop();
            var crop = new FormatConvertedBitmap(snap.Crop(rect), PixelFormats.Bgra32, null, 0);
            var pixels = new byte[rect.Width * rect.Height * 4];
            crop.CopyPixels(pixels, rect.Width * 4, 0);
            return pixels;
        }

        private static double MeanDifference(byte[] a, byte[] b)
        {
            long sum = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i += 4)
                sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            return sum / (n / 4.0) / 3.0;
        }

        /// <summary>Let the dispatcher run for a while, so windows actually paint.</summary>
        private static void Pump(int milliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                System.Threading.Thread.Sleep(5);
            }
        }

        private static void Say(string message)
        {
            Console.WriteLine(message);
            Console.Out.Flush();
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine("self-test FAILED: " + message);
            return 1;
        }

        /// <summary>The stand-in screenshot, also used for documentation shots.</summary>
        public static BitmapSource SampleCapture() => BuildSample().Image!;

        private static BoardDocument BuildSample()
        {
            // Stand-in for a screenshot: a small "UI" with a button to point at.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)), null, new Rect(0, 0, 520, 300));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xD9, 0xA9, 0x3A)), null,
                    new Rect(60, 90, 150, 44), 6, 6);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31)), null,
                    new Rect(60, 160, 380, 26), 4, 4);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31)), null,
                    new Rect(60, 200, 300, 26), 4, 4);
            }
            var rtb = new RenderTargetBitmap(520, 300, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            var doc = BoardDocument.FromCapture(rtb, 260);

            var opts = new ToolOptions();

            // 1. A. i. — the numbering styles, on the board.
            doc.Items.Add(new StepItem
            {
                Center = new Point(300, 320), Radius = 20,
                Label = StepItem.LabelFor(StepStyle.Number, 1),
                StrokeColor = "#E5342A", FillColor = "#E5342A"
            });
            doc.Items.Add(new StepItem
            {
                Center = new Point(300, 400), Radius = 20,
                Label = StepItem.LabelFor(StepStyle.UpperLetter, 1),
                StrokeColor = "#FF8A00", FillColor = "#FF8A00"
            });
            doc.Items.Add(new StepItem
            {
                Center = new Point(300, 480), Radius = 20,
                Label = StepItem.LabelFor(StepStyle.LowerRoman, 3),
                StrokeColor = "#7A5CFF", FillColor = "#7A5CFF"
            });

            doc.Items.Add(new TextItem
            {
                Origin = new Point(40, 300), Text = "1.  make this button bigger", FontSize = 20,
                StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            });
            doc.Items.Add(new TextItem
            {
                Origin = new Point(40, 380), Text = "A.  this row wraps badly", FontSize = 20,
                StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            });
            doc.Items.Add(new TextItem
            {
                Origin = new Point(40, 460), Text = "iii.  and redact this", FontSize = 20,
                StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            });

            // Arrows from the notes to the thing being talked about.
            doc.Items.Add(new LineItem
            {
                A = new Point(330, 320), B = new Point(410, 370),
                StrokeColor = "#E5342A", Thickness = 4, ArrowEnd = true
            });
            doc.Items.Add(new RectItem
            {
                A = new Point(312, 342), B = new Point(472, 396),
                StrokeColor = "#E5342A", Thickness = 3
            });
            doc.Items.Add(new EllipseItem
            {
                A = new Point(300, 410), B = new Point(700, 460),
                StrokeColor = "#FF8A00", Thickness = 3
            });
            doc.Items.Add(new StrokeItem
            {
                StrokeColor = "#FFD400", Thickness = 22, IsHighlighter = true,
                Points = { new Point(320, 452), new Point(480, 452), new Point(640, 452) }
            });
            doc.Items.Add(new RedactItem
            {
                A = new Point(320, 460), B = new Point(620, 486), BlockSize = 9
            });

            // A sample of the symbol palette, drawn from the catalogue.
            double x = 300;
            foreach (var name in new[] { "arrow-right", "check", "cross", "warning", "callout", "target", "star", "cursor" })
            {
                doc.Items.Add(new SymbolItem
                {
                    A = new Point(x, 560), B = new Point(x + 52, 612),
                    Symbol = name, Filled = true, StrokeColor = "#D9A93A", Thickness = 6
                });
                x += 64;
            }

            return doc;
        }
    }
}
