using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Log = KamCapture.Services.Log;

namespace KamCapture.Recording
{
    public sealed class AudioDevice
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsDefault { get; init; }
        public override string ToString() => Name + (IsDefault ? "  (default)" : "");
    }

    public static class AudioDevices
    {
        public static List<AudioDevice> Inputs()
        {
            var list = new List<AudioDevice>();
            try
            {
                using var en = new MMDeviceEnumerator();
                string defaultId = "";
                try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID; } catch { }

                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    list.Add(new AudioDevice { Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defaultId });
            }
            catch { }
            return list;
        }

        public static List<AudioDevice> Outputs()
        {
            var list = new List<AudioDevice>();
            try
            {
                using var en = new MMDeviceEnumerator();
                string defaultId = "";
                try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }

                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    list.Add(new AudioDevice { Id = d.ID, Name = d.FriendlyName, IsDefault = d.ID == defaultId });
            }
            catch { }
            return list;
        }

        /// <summary>
        /// The name of the device "Default" means right now: Windows' default
        /// microphone, or its default output. Shown beside the word, because the
        /// default is not always the device you would guess — a headset jack
        /// can quietly be the default microphone while you talk into a USB one.
        /// </summary>
        public static string DefaultName(DataFlow flow)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                return en.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Console : Role.Multimedia).FriendlyName;
            }
            catch { return ""; }
        }

        /// <summary>"Default: External Microphone (Realtek(R) Audio)", or just "Default microphone".</summary>
        public static string DefaultMicrophoneLabel()
        {
            var name = DefaultName(DataFlow.Capture);
            return name.Length > 0 ? "Default: " + name : "Default microphone";
        }

        public const string EveryOutputLabel = "Every output";

        public static MMDevice? ById(string id, DataFlow flow)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                using var en = new MMDeviceEnumerator();
                return en.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(d => d.ID == id);
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Mixes any number of live sources into one continuous 48 kHz stereo
    /// stream.
    ///
    /// The output never stops: the writer paces itself against the wall clock
    /// and emits silence when nothing is playing. That is what makes it safe to
    /// add or drop a microphone in the middle of a recording — the file keeps
    /// receiving an unbroken track, so audio and video never drift apart.
    ///
    /// System audio is taken from every output at once unless one is chosen.
    /// It used to be the default output only, and a laptop has several — the
    /// speakers, the headphone jack, a monitor's HDMI audio — so a call playing
    /// through one while Windows' default was another recorded as pure silence,
    /// and plugging headphones in part way through moved the sound out from
    /// under the recording. A watcher also picks up outputs that appear during a
    /// recording, and reopens any that stop.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;

        private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        private readonly MixingSampleProvider _mixer;
        private readonly object _lock = new();
        private readonly Dictionary<string, Source> _sources = new();
        private readonly Timer _watch;
        private Stream? _output;
        private Thread? _writer;
        private volatile bool _running;
        private volatile bool _paused;
        private volatile bool _disposed;

        /// <summary>Sample frames written so far: the length of the track, exactly.</summary>
        public long FramesWritten { get; private set; }

        public bool IsPaused => _paused;

        public event Action<string>? Failed;

        /// <summary>Peak level of the last block, 0..1, for the meter on the bar.</summary>
        public float LastPeak { get; private set; }

        /// <summary>One device feeding a source: its capture, its buffer, and 48 kHz stereo out.</summary>
        private sealed class Tap
        {
            public string DeviceId = "";
            public string Name = "";
            public IWaveIn Capture = null!;
            public BufferedWaveProvider Buffer = null!;
            public ISampleProvider Output = null!;
            public volatile bool Dead;
            public volatile bool Closing;

            public void Close()
            {
                Closing = true;
                try { Capture.StopRecording(); } catch { }
                try { Capture.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// A named input to the mix — "system" or "mic" — made of one tap, or
        /// for system audio from every output, one tap per output.
        /// </summary>
        private sealed class Source
        {
            public string Key = "";
            public DataFlow Flow;
            public string DeviceId = "";          // empty: every output, or the default microphone
            public readonly List<Tap> Taps = new();
            public readonly MixingSampleProvider Mix = new(MixFormat) { ReadFully = true };
            public VolumeSampleProvider Volume = null!;
            public Meter Level = null!;

            public bool EveryOutput => Flow == DataFlow.Render && DeviceId.Length == 0;
        }

        public AudioEngine()
        {
            _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            _watch = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public bool Has(string key)
        {
            lock (_lock) return _sources.ContainsKey(key);
        }

        public void SetGain(string key, double gain)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(key, out var s))
                    s.Volume.Volume = (float)Math.Clamp(gain, 0, 4);
            }
        }

        /// <summary>The level one source reached in the last block, 0..1: is it hearing anything.</summary>
        public float PeakOf(string key)
        {
            lock (_lock) return _sources.TryGetValue(key, out var s) ? s.Level.Peak : 0;
        }

        /// <summary>
        /// System audio. With no device given, every output at once — whichever
        /// one a call or a video plays through, it is heard.
        /// </summary>
        public bool AddSystemAudio(string key, string? deviceId, double gain) =>
            AddSource(key, DataFlow.Render, deviceId ?? "", gain);

        public bool AddMicrophone(string key, string? deviceId, double gain) =>
            AddSource(key, DataFlow.Capture, deviceId ?? "", gain);

        private bool AddSource(string key, DataFlow flow, string deviceId, double gain)
        {
            Remove(key);

            var source = new Source { Key = key, Flow = flow, DeviceId = deviceId };
            source.Volume = new VolumeSampleProvider(source.Mix) { Volume = (float)Math.Clamp(gain, 0, 4) };
            source.Level = new Meter(source.Volume);

            var opened = new List<Tap>();
            foreach (var device in DevicesFor(source))
            {
                var tap = Open(device, flow);
                if (tap != null) opened.Add(tap);
            }

            lock (_lock)
            {
                foreach (var tap in opened)
                {
                    source.Taps.Add(tap);
                    source.Mix.AddMixerInput(tap.Output);
                }
                _sources[key] = source;
                _mixer.AddMixerInput(source.Level);
            }

            var what = flow == DataFlow.Render ? "System audio" : "Microphone";
            if (opened.Count == 0)
            {
                Report($"{what} could not be opened on any device.");
                return false;
            }
            Log.Info($"{what} from: " + string.Join(", ", opened.Select(t => t.Name)));
            return true;
        }

        /// <summary>The devices a source should be listening to right now.</summary>
        private static List<MMDevice> DevicesFor(Source source)
        {
            var list = new List<MMDevice>();
            try
            {
                using var en = new MMDeviceEnumerator();
                if (source.EveryOutput)
                {
                    list.AddRange(en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));
                }
                else if (source.DeviceId.Length > 0)
                {
                    var d = en.EnumerateAudioEndPoints(source.Flow, DeviceState.Active)
                              .FirstOrDefault(x => x.ID == source.DeviceId);
                    if (d != null) list.Add(d);
                }
                else
                {
                    // The default microphone: what Windows calls the default device.
                    list.Add(en.GetDefaultAudioEndpoint(source.Flow, Role.Console));
                }
            }
            catch { }
            return list;
        }

        private Tap? Open(MMDevice device, DataFlow flow)
        {
            string name = "";
            try
            {
                name = device.FriendlyName;
                IWaveIn capture = flow == DataFlow.Render
                    ? new WasapiLoopbackCapture(device)
                    : new WasapiCapture(device);

                var tap = new Tap
                {
                    DeviceId = device.ID,
                    Name = name,
                    Capture = capture,
                    Buffer = new BufferedWaveProvider(capture.WaveFormat)
                    {
                        BufferDuration = TimeSpan.FromSeconds(3),
                        DiscardOnBufferOverflow = true
                    }
                };

                capture.DataAvailable += (_, e) =>
                {
                    try { tap.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
                };
                capture.RecordingStopped += (_, e) =>
                {
                    if (tap.Closing) return;
                    // Unplugged, disabled, or its format changed. The watcher
                    // takes it out, and opens it again if it comes back.
                    tap.Dead = true;
                    Log.Warn($"Audio from {tap.Name} stopped: {e.Exception?.Message ?? "no reason given"}");
                };

                tap.Output = new Endless(ToMixFormat(tap.Buffer.ToSampleProvider()));
                capture.StartRecording();
                return tap;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not open {(flow == DataFlow.Render ? "output" : "microphone")} {name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Any device's format, as the mix wants it: 48 kHz, stereo, float.</summary>
        private static ISampleProvider ToMixFormat(ISampleProvider provider)
        {
            int channels = provider.WaveFormat.Channels;
            if (channels == 1)
                provider = new MonoToStereoSampleProvider(provider);
            else if (channels > 2)
                provider = new Downmix(provider);

            if (provider.WaveFormat.SampleRate != SampleRate)
                provider = new WdlResamplingSampleProvider(provider, SampleRate);
            return provider;
        }

        /// <summary>
        /// Every two seconds: take out taps whose device stopped, and open any
        /// that should be there and are not — an output that appeared, a
        /// microphone plugged back in.
        /// </summary>
        private void Watch()
        {
            if (_disposed) return;
            List<Source> sources;
            lock (_lock) sources = _sources.Values.ToList();

            foreach (var source in sources)
            {
                List<Tap> dead;
                HashSet<string> live;
                lock (_lock)
                {
                    dead = source.Taps.Where(t => t.Dead).ToList();
                    foreach (var t in dead)
                    {
                        source.Taps.Remove(t);
                        source.Mix.RemoveMixerInput(t.Output);
                    }
                    live = source.Taps.Select(t => t.DeviceId).ToHashSet();
                }
                foreach (var t in dead) t.Close();

                // The default microphone is one device, whichever it is; the
                // others are exactly the devices listed.
                bool wantsOne = !source.EveryOutput;
                if (wantsOne && live.Count > 0) continue;

                foreach (var device in DevicesFor(source))
                {
                    if (live.Contains(device.ID)) continue;
                    var tap = Open(device, source.Flow);
                    if (tap == null) continue;

                    bool kept = false;
                    lock (_lock)
                    {
                        if (!_disposed && _sources.TryGetValue(source.Key, out var current) && ReferenceEquals(current, source))
                        {
                            source.Taps.Add(tap);
                            source.Mix.AddMixerInput(tap.Output);
                            kept = true;
                        }
                    }
                    if (kept) Log.Info($"Now also recording from {tap.Name}");
                    else tap.Close();
                    if (wantsOne) break;
                }
            }
        }

        private void Report(string message)
        {
            Log.Warn(message);
            Failed?.Invoke(message);
        }

        /// <summary>
        /// Stop writing without ending the track. The writer's clock stops with
        /// it, so the paused stretch is simply absent from the file.
        /// </summary>
        public void Pause()
        {
            _paused = true;
            LastPeak = 0;
        }

        /// <summary>
        /// Carry on from where it stopped. Each source kept buffering while
        /// paused; that audio is thrown away here, or the first seconds after
        /// resuming would be what was said during the pause.
        /// </summary>
        public void Resume()
        {
            lock (_lock)
            {
                foreach (var s in _sources.Values)
                    foreach (var t in s.Taps)
                    {
                        try { t.Buffer.ClearBuffer(); } catch { }
                    }
            }
            _paused = false;
        }

        public void Remove(string key)
        {
            Source? source;
            lock (_lock)
            {
                if (!_sources.TryGetValue(key, out source)) return;
                _sources.Remove(key);
                try { _mixer.RemoveMixerInput(source.Level); } catch { }
            }
            foreach (var t in source.Taps) t.Close();
        }

        /// <summary>Begin writing the mix. The stream is written until Stop.</summary>
        public void Start(Stream output)
        {
            _output = output;
            _running = true;
            _writer = new Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "KAM audio mix",
                Priority = ThreadPriority.AboveNormal
            };
            _writer.Start();
        }

        private void WriteLoop()
        {
            const int blockFrames = 480;                 // 10 ms
            var floats = new float[blockFrames * Channels];
            var bytes = new byte[blockFrames * Channels * 2];

            var clock = Stopwatch.StartNew();
            long written = 0;                            // frames emitted so far
            TimeSpan pausedTotal = TimeSpan.Zero;
            TimeSpan? pausedAt = null;

            while (_running)
            {
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

                long due = (long)((clock.Elapsed - pausedTotal).TotalSeconds * SampleRate);
                if (due - written < blockFrames)
                {
                    Thread.Sleep(2);
                    continue;
                }

                int read;
                lock (_lock)
                {
                    read = _mixer.Read(floats, 0, floats.Length);
                }
                for (int i = read; i < floats.Length; i++) floats[i] = 0;

                float peak = 0;
                for (int i = 0; i < floats.Length; i++)
                {
                    float v = floats[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    float a = v < 0 ? -v : v;
                    if (a > peak) peak = a;

                    short s = (short)(v * 32767f);
                    bytes[i * 2] = (byte)(s & 0xFF);
                    bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                LastPeak = peak;

                try
                {
                    _output?.Write(bytes, 0, bytes.Length);
                }
                catch
                {
                    // ffmpeg went away; the recorder will notice and stop.
                    break;
                }

                written += blockFrames;
                FramesWritten = written;
            }

            try { _output?.Flush(); } catch { }
        }

        public void Stop()
        {
            _disposed = true;
            try { _watch.Dispose(); } catch { }
            _running = false;
            try { _writer?.Join(500); } catch { }

            List<string> keys;
            lock (_lock)
            {
                keys = _sources.Keys.ToList();
                // One line per take: what each source actually heard. A source
                // that heard nothing for a whole take is the first thing to know
                // when a recording comes back silent.
                if (FramesWritten > 0 && _sources.Count > 0)
                    Log.Info("Take ended after " + (FramesWritten / (double)SampleRate).ToString("0.0") + " s; loudest: " +
                             string.Join(", ", _sources.Values.Select(s =>
                                 $"{s.Key} {s.Level.Loudest:0.000} from {(s.Taps.Count == 0 ? "no device" : string.Join(" + ", s.Taps.Select(t => t.Name)))}")));
            }
            foreach (var key in keys) Remove(key);

            try { _output?.Flush(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Always hands back as much as was asked for. The mixer drops any input
        /// that returns short, taking it as finished — and a resampler can return
        /// short on an ordinary read, which silently lost that device for the
        /// rest of the recording.
        /// </summary>
        private sealed class Endless : ISampleProvider
        {
            private readonly ISampleProvider _inner;
            public Endless(ISampleProvider inner) => _inner = inner;
            public WaveFormat WaveFormat => _inner.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int total = 0;
                while (total < count)
                {
                    int read = _inner.Read(buffer, offset + total, count - total);
                    if (read <= 0) break;
                    total += read;
                }
                if (total < count) Array.Clear(buffer, offset + total, count - total);
                return count;
            }
        }

        /// <summary>Passes samples through and remembers the loudest in each block.</summary>
        private sealed class Meter : ISampleProvider
        {
            private readonly ISampleProvider _inner;
            public volatile float Peak;
            public volatile float Loudest;
            public Meter(ISampleProvider inner) => _inner = inner;
            public WaveFormat WaveFormat => _inner.WaveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _inner.Read(buffer, offset, count);
                float peak = 0;
                for (int i = offset; i < offset + read; i++)
                {
                    float a = Math.Abs(buffer[i]);
                    if (a > peak) peak = a;
                }
                Peak = Math.Min(1f, peak);
                if (Peak > Loudest) Loudest = Peak;
                return read;
            }
        }

        /// <summary>
        /// Surround to stereo. 5.1 and 7.1 fold the centre and the surrounds in;
        /// anything else keeps its front pair. A monitor's HDMI output is often
        /// eight channels, which the old stereo-only conversion refused outright.
        /// </summary>
        private sealed class Downmix : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _buffer = Array.Empty<float>();

            public Downmix(ISampleProvider source)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int frames = count / 2;
                int need = frames * _channels;
                if (_buffer.Length < need) _buffer = new float[need];

                int got = _source.Read(_buffer, 0, need) / _channels;
                bool surround = _channels is 6 or 8;
                for (int f = 0; f < got; f++)
                {
                    int b = f * _channels;
                    float left = _buffer[b], right = _buffer[b + 1];
                    if (surround)
                    {
                        float centre = _buffer[b + 2] * 0.707f;
                        left += centre + _buffer[b + 4] * 0.5f;
                        right += centre + _buffer[b + 5] * 0.5f;
                        if (_channels == 8)
                        {
                            left += _buffer[b + 6] * 0.5f;
                            right += _buffer[b + 7] * 0.5f;
                        }
                    }
                    buffer[offset + f * 2] = left;
                    buffer[offset + f * 2 + 1] = right;
                }
                return got * 2;
            }
        }
    }
}
