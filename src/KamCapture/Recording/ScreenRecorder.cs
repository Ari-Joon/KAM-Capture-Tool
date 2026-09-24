using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KamCapture.Capture;
using KamCapture.Interop;
using KamCapture.Settings;

namespace KamCapture.Recording
{
    public enum RecordKind { FullScreen, Monitor, Region, Window, AudioOnly }

    public sealed class RecordTarget
    {
        public RecordKind Kind { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public IntPtr Window { get; init; }
        public string Description { get; init; } = "";

        public static RecordTarget FullScreen()
        {
            var (x, y, w, h) = Screens.VirtualBounds();
            return new RecordTarget
            {
                Kind = RecordKind.FullScreen, X = x, Y = y, Width = w, Height = h,
                Description = $"All displays  ({w} × {h})"
            };
        }

        public static RecordTarget Monitor(MonitorInfo m) => new()
        {
            Kind = RecordKind.Monitor, X = m.X, Y = m.Y, Width = m.Width, Height = m.Height,
            Description = $"{(m.IsPrimary ? "Primary display" : m.DeviceName)}  ({m.Width} × {m.Height})"
        };

        public static RecordTarget Region(System.Windows.Int32Rect r) => new()
        {
            Kind = RecordKind.Region, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
            Description = $"Region  ({r.Width} × {r.Height})"
        };

        /// <summary>No picture at all: system audio, the microphone, or both.</summary>
        public static RecordTarget AudioOnly() => new()
        {
            Kind = RecordKind.AudioOnly,
            Description = "Audio only"
        };

        public static RecordTarget WindowTarget(CapturableWindow w) => new()
        {
            Kind = RecordKind.Window, X = w.X, Y = w.Y, Width = w.Width, Height = w.Height,
            Window = w.Handle, Description = $"{w.Title}  ({w.Width} × {w.Height})"
        };
    }

    /// <summary>
    /// Captures a rectangle of the desktop into a DIB section. The bits live in
    /// unmanaged memory that BitBlt writes straight into, so a frame costs one
    /// blit and no copy on the way to the encoder.
    /// </summary>
    internal sealed unsafe class FrameGrabber : IDisposable
    {
        private readonly IntPtr _screenDc, _memDc, _bitmap, _old;
        private readonly IntPtr _bits;
        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 4;
        public int FrameBytes => Stride * Height;

        public FrameGrabber(int width, int height)
        {
            Width = width;
            Height = height;

            _screenDc = NativeMethods.GetDC(IntPtr.Zero);
            _memDc = NativeMethods.CreateCompatibleDC(_screenDc);

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,      // negative: top-down, which is what encoders expect
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0        // BI_RGB
                }
            };

            _bitmap = NativeMethods.CreateDIBSection(_memDc, ref bmi, NativeMethods.DIB_RGB_COLORS,
                out _bits, IntPtr.Zero, 0);
            _old = NativeMethods.SelectObject(_memDc, _bitmap);
        }

        public bool Grab(int srcX, int srcY, bool withCursor)
        {
            bool ok = NativeMethods.BitBlt(_memDc, 0, 0, Width, Height, _screenDc, srcX, srcY,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (ok && withCursor) DrawCursor(srcX, srcY);
            return ok;
        }

        private void DrawCursor(int originX, int originY)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!NativeMethods.GetCursorInfo(ref ci)) return;
            if ((ci.flags & NativeMethods.CURSOR_SHOWING) == 0) return;

            IntPtr copy = NativeMethods.CopyIcon(ci.hCursor);
            if (copy == IntPtr.Zero) return;
            try
            {
                if (NativeMethods.GetIconInfo(copy, out ICONINFO info))
                {
                    int x = ci.ptScreenPos.X - originX - info.xHotspot;
                    int y = ci.ptScreenPos.Y - originY - info.yHotspot;
                    NativeMethods.DrawIconEx(_memDc, x, y, copy, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
                    if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
                    if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
                }
            }
            finally { NativeMethods.DestroyIcon(copy); }
        }

        public void WriteTo(Stream stream)
        {
            var span = new ReadOnlySpan<byte>((void*)_bits, FrameBytes);
            stream.Write(span);
        }

        public void Dispose()
        {
            NativeMethods.SelectObject(_memDc, _old);
            NativeMethods.DeleteObject(_bitmap);
            NativeMethods.DeleteDC(_memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
        }
    }

    public sealed class ScreenRecorder : IDisposable
    {
        private readonly AppSettings _cfg;
        private readonly string _ffmpeg;

        private Process? _proc;
        private NamedPipeServerStream? _audioPipe;
        private AudioEngine? _audio;
        private Thread? _videoThread;
        private volatile bool _running;
        private volatile bool _paused;
        private readonly Stopwatch _clock = new();
        private TimeSpan _pausedTotal;
        private DateTime _pauseStarted;

        public RecordTarget Target { get; }
        public string OutputPath { get; }

        public bool IsAudioOnly => Target.Kind == RecordKind.AudioOnly;

        /// <summary>The container actually used, which may differ from the one asked for.</summary>
        public string AudioFormat { get; } = "mp3";

        /// <summary>Set when the chosen format could not be used, to say so rather than hide it.</summary>
        public string? FormatNote { get; }
        public bool IsPaused => _paused;
        public TimeSpan Elapsed => _clock.Elapsed - _pausedTotal -
            (_paused ? DateTime.UtcNow - _pauseStarted : TimeSpan.Zero);

        public long FramesWritten { get; private set; }

        /// <summary>A second of catch-up is plenty; beyond that, accept the gap.</summary>
        private const int MaxCatchUpFrames = 30;
        public long FramesDuplicated { get; private set; }
        public float AudioPeak => _audio?.LastPeak ?? 0;

        public event Action<string>? Failed;
        public event Action<string>? Finished;

        public AudioEngine Audio => _audio ??= new AudioEngine();

        public ScreenRecorder(AppSettings cfg, RecordTarget target, string ffmpegPath)
        {
            _cfg = cfg;
            Target = target;
            _ffmpeg = ffmpegPath;

            // Sound has its own folder; a confirmed OneDrive choice is honoured.
            var folder = IsAudioOnly ? cfg.EnsureAudioFolder() : cfg.EnsureRecordFolder();
            string extension = ".mp4";
            if (IsAudioOnly)
            {
                AudioFormat = (cfg.AudioFormat ?? "mp3").Trim().ToLowerInvariant() switch
                {
                    "m4a" => "m4a",
                    "wav" => "wav",
                    _ => "mp3"
                };

                // MP3 needs libmp3lame, which some ffmpeg builds leave out. M4A
                // uses ffmpeg's own AAC encoder, which every build has.
                if (AudioFormat == "mp3" && !FfmpegLocator.HasEncoder(ffmpegPath, "libmp3lame"))
                {
                    AudioFormat = "m4a";
                    FormatNote = "This ffmpeg cannot write MP3, so the audio is saved as M4A.";
                }
                extension = "." + AudioFormat;
            }

            // Named to the second, so two recordings started in the same second
            // would otherwise share a name and the second would replace the first.
            OutputPath = UI.NameDialog.UniquePath(Path.Combine(folder, cfg.BuildFileName(extension)));
        }

        private static int Even(int v) => v % 2 == 0 ? v : v - 1;

        public void Start(bool systemAudio, string? micDeviceId, string? systemDeviceId = null)
        {
            if (IsAudioOnly)
            {
                StartAudioOnly(systemAudio, micDeviceId, systemDeviceId);
                return;
            }

            int w = Even(Math.Max(2, Target.Width));
            int h = Even(Math.Max(2, Target.Height));

            // The audio track is always there, even when the recording starts
            // silent. Without it, switching the microphone on half way through
            // had nowhere to go and quietly recorded nothing.
            string? pipeName = "kam-audio-" + Guid.NewGuid().ToString("N");

            if (pipeName != null)
            {
                _audioPipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
            }

            var args = BuildArgs(w, h, pipeName);

            _proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = null
                },
                EnableRaisingEvents = true
            };

            var log = new StringBuilder();
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (log)
                {
                    log.AppendLine(e.Data);
                    if (log.Length > 20000) log.Remove(0, 10000);
                }
            };

            _proc.Start();
            _proc.BeginErrorReadLine();

            if (_audioPipe != null)
            {
                var engine = Audio;
                engine.Failed += m => Failed?.Invoke(m);

                if (systemAudio) engine.AddSystemAudio("system", systemDeviceId, _cfg.SystemAudioGain);
                if (micDeviceId != null) engine.AddMicrophone("mic", micDeviceId, _cfg.MicrophoneGain);

                var pipe = _audioPipe;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync();
                        engine.Start(pipe);
                    }
                    catch (Exception ex) { Failed?.Invoke("Audio pipe: " + ex.Message); }
                });
            }

            _running = true;
            _clock.Restart();
            _pausedTotal = TimeSpan.Zero;

            _videoThread = new Thread(() => VideoLoop(w, h, log))
            {
                IsBackground = true,
                Name = "KAM frame pump",
                Priority = ThreadPriority.AboveNormal
            };
            _videoThread.Start();
        }

        /// <summary>
        /// Sound only. The mix goes straight into ffmpeg's standard input —
        /// there is no second stream to keep in step, so no pipe is needed —
        /// and sources can still be switched on and off while it runs, exactly
        /// as they can during a video.
        /// </summary>
        private void StartAudioOnly(bool systemAudio, string? micDeviceId, string? systemDeviceId)
        {
            string codec = AudioFormat switch
            {
                "wav" => "-c:a pcm_s16le",
                // AAC holds up at a lower rate than MP3 does, which is the
                // whole reason to offer it: two thirds the size for the same ear.
                "m4a" => "-c:a aac -b:a 128k -movflags +faststart",
                _ => "-c:a libmp3lame -b:a 192k"
            };

            var args = "-hide_banner -loglevel warning -y " +
                       "-analyzeduration 0 -probesize 32 " +
                       $"-f s16le -ar {AudioEngine.SampleRate} -ac {AudioEngine.Channels} -i - " +
                       $"{codec} \"{OutputPath}\"";

            _proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = null
                },
                EnableRaisingEvents = true
            };
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Services.Log.Warn("ffmpeg: " + e.Data);
            };

            _proc.Start();
            _proc.BeginErrorReadLine();

            var engine = Audio;
            engine.Failed += m => Failed?.Invoke(m);
            if (systemAudio) engine.AddSystemAudio("system", systemDeviceId, _cfg.SystemAudioGain);
            if (micDeviceId != null) engine.AddMicrophone("mic", micDeviceId, _cfg.MicrophoneGain);

            _running = true;
            _clock.Restart();
            _pausedTotal = TimeSpan.Zero;

            engine.Start(_proc.StandardInput.BaseStream);
        }

        private string BuildArgs(int w, int h, string? pipeName)
        {
            var encoder = ResolveEncoder();
            var sb = new StringBuilder();

            sb.Append("-hide_banner -loglevel warning -y ");

            // Both inputs are raw with every parameter already stated, so there
            // is nothing to probe. Left to its defaults ffmpeg sits reading the
            // audio pipe looking for a stream description, and while it does
            // that it is not draining the video pipe — which fills, blocks the
            // frame pump, and yields a handful of frames for a whole take.
            const string rawInput = "-thread_queue_size 4096 -analyzeduration 0 -probesize 32 ";

            sb.Append(rawInput);
            sb.Append($"-f rawvideo -pixel_format bgra -video_size {w}x{h} -framerate {_cfg.RecordFps} -i - ");

            if (pipeName != null)
            {
                sb.Append(rawInput);
                sb.Append($"-f s16le -ar {AudioEngine.SampleRate} -ac {AudioEngine.Channels} -i \\\\.\\pipe\\{pipeName} ");
            }

            sb.Append($"-c:v {encoder} ");
            sb.Append(encoder == "libx264"
                ? $"-preset veryfast -crf {_cfg.RecordQuality} "
                : $"-rc vbr -cq {_cfg.RecordQuality} -b:v 0 ");

            sb.Append("-pix_fmt yuv420p -movflags +faststart ");

            if (pipeName != null) sb.Append("-c:a aac -b:a 192k ");

            sb.Append($"\"{OutputPath}\"");
            return sb.ToString();
        }

        private string ResolveEncoder()
        {
            if (!string.IsNullOrWhiteSpace(_cfg.VideoEncoder) && _cfg.VideoEncoder != "auto")
                return _cfg.VideoEncoder;
            return "libx264";
        }

        private void VideoLoop(int w, int h, StringBuilder log)
        {
            FrameGrabber? grabber = null;
            try
            {
                grabber = new FrameGrabber(w, h);
                var stdin = _proc!.StandardInput.BaseStream;

                double interval = 1000.0 / Math.Max(1, _cfg.RecordFps);
                var clock = Stopwatch.StartNew();
                long frame = 0;
                TimeSpan pausedTotal = TimeSpan.Zero;
                TimeSpan? pausedAt = null;

                while (_running)
                {
                    // Paused means paused: no frames, and a clock that stops with
                    // them. The audio writer does the same, so the stretch is
                    // absent from both tracks and they stay in step.
                    if (_paused)
                    {
                        pausedAt ??= clock.Elapsed;
                        Thread.Sleep(5);
                        continue;
                    }
                    if (pausedAt != null)
                    {
                        pausedTotal += clock.Elapsed - pausedAt.Value;
                        pausedAt = null;
                    }

                    long target = (long)((clock.Elapsed - pausedTotal).TotalMilliseconds / interval);
                    if (target <= frame)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    int sx = Target.X, sy = Target.Y;

                    // A window target follows the window as it is moved.
                    if (Target.Kind == RecordKind.Window && Target.Window != IntPtr.Zero)
                    {
                        var (wx, wy, ww, wh) = WindowFinder.GetWindowBounds(Target.Window);
                        if (ww > 0 && wh > 0) { sx = wx; sy = wy; }
                    }

                    grabber.Grab(sx, sy, _cfg.RecordCursor);

                    // The encoder is fed a constant frame rate, so every tick of
                    // wall time owes it a frame. When capture cannot keep up the
                    // last frame is repeated rather than skipped: dropping them
                    // here would shorten the file and play the recording back
                    // faster than it actually happened.
                    long owed = Math.Min(target - frame, MaxCatchUpFrames);
                    for (long i = 0; i < owed; i++)
                    {
                        try { grabber.WriteTo(stdin); }
                        catch { _running = false; break; }
                    }

                    FramesWritten += owed;
                    FramesDuplicated += owed - 1;
                    frame = target;
                }

                try { stdin.Flush(); } catch { }
                try { _proc.StandardInput.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
            }
            finally
            {
                grabber?.Dispose();
            }
        }

        public void Pause()
        {
            if (_paused) return;
            _paused = true;
            _pauseStarted = DateTime.UtcNow;
            _audio?.Pause();
        }

        public void Resume()
        {
            if (!_paused) return;
            _pausedTotal += DateTime.UtcNow - _pauseStarted;
            _audio?.Resume();
            _paused = false;
        }

        public string? Stop()
        {
            if (!_running) return OutputPath;
            _running = false;

            try { _videoThread?.Join(2000); } catch { }

            _audio?.Stop();
            try { _audioPipe?.Dispose(); } catch { }
            _audioPipe = null;

            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    if (!_proc.WaitForExit(15000))
                    {
                        _proc.Kill(true);
                        Failed?.Invoke("ffmpeg did not finish in time; the file may be incomplete.");
                    }
                }
            }
            catch { }

            _clock.Stop();
            Finished?.Invoke(OutputPath);
            return OutputPath;
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            _audio?.Dispose();
            try { _proc?.Dispose(); } catch { }
        }
    }
}
