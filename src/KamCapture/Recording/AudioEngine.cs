using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
                try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID; } catch { }

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
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;

        private readonly MixingSampleProvider _mixer;
        private readonly object _lock = new();
        private readonly Dictionary<string, Source> _sources = new();
        private Stream? _output;
        private Thread? _writer;
        private volatile bool _running;

        public event Action<string>? Failed;

        /// <summary>Peak level of the last block, 0..1, for the meter on the bar.</summary>
        public float LastPeak { get; private set; }

        private sealed class Source : IDisposable
        {
            public IWaveIn? Capture;
            public BufferedWaveProvider? Buffer;
            public ISampleProvider? Provider;
            public VolumeSampleProvider? Volume;

            public void Dispose()
            {
                try { Capture?.StopRecording(); } catch { }
                try { Capture?.Dispose(); } catch { }
                Capture = null;
            }
        }

        public AudioEngine()
        {
            _mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
            { ReadFully = true };
        }

        public bool Has(string key)
        {
            lock (_lock) return _sources.ContainsKey(key);
        }

        public void SetGain(string key, double gain)
        {
            lock (_lock)
            {
                if (_sources.TryGetValue(key, out var s) && s.Volume != null)
                    s.Volume.Volume = (float)Math.Clamp(gain, 0, 4);
            }
        }

        /// <summary>System audio, captured as a loopback of an output device.</summary>
        public bool AddSystemAudio(string key, string? deviceId, double gain)
        {
            try
            {
                var device = AudioDevices.ById(deviceId ?? "", DataFlow.Render);
                var capture = device != null
                    ? new WasapiLoopbackCapture(device)
                    : new WasapiLoopbackCapture();
                return Add(key, capture, gain);
            }
            catch (Exception ex)
            {
                Failed?.Invoke("System audio could not be opened: " + ex.Message);
                return false;
            }
        }

        public bool AddMicrophone(string key, string? deviceId, double gain)
        {
            try
            {
                var device = AudioDevices.ById(deviceId ?? "", DataFlow.Capture);
                IWaveIn capture = device != null
                    ? new WasapiCapture(device)
                    : new WasapiCapture();
                return Add(key, capture, gain);
            }
            catch (Exception ex)
            {
                Failed?.Invoke("Microphone could not be opened: " + ex.Message);
                return false;
            }
        }

        private bool Add(string key, IWaveIn capture, double gain)
        {
            Remove(key);

            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true
            };

            capture.DataAvailable += (_, e) =>
            {
                try { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Failed?.Invoke(e.Exception.Message);
            };

            ISampleProvider provider = buffer.ToSampleProvider();

            if (provider.WaveFormat.Channels == 1)
                provider = new MonoToStereoSampleProvider(provider);
            else if (provider.WaveFormat.Channels > 2)
                provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };

            if (provider.WaveFormat.Channels == 1)
                provider = new MonoToStereoSampleProvider(provider);

            if (provider.WaveFormat.SampleRate != SampleRate)
                provider = new WdlResamplingSampleProvider(provider, SampleRate);

            var volume = new VolumeSampleProvider(provider) { Volume = (float)Math.Clamp(gain, 0, 4) };

            var source = new Source
            {
                Capture = capture,
                Buffer = buffer,
                Provider = volume,
                Volume = volume
            };

            lock (_lock)
            {
                _mixer.AddMixerInput(source.Provider);
                _sources[key] = source;
            }

            capture.StartRecording();
            return true;
        }

        public void Remove(string key)
        {
            Source? source;
            lock (_lock)
            {
                if (!_sources.TryGetValue(key, out source)) return;
                _sources.Remove(key);
                if (source.Provider != null)
                {
                    try { _mixer.RemoveMixerInput(source.Provider); } catch { }
                }
            }
            source.Dispose();
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

            while (_running)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
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
            }

            try { _output?.Flush(); } catch { }
        }

        public void Stop()
        {
            _running = false;
            try { _writer?.Join(500); } catch { }

            foreach (var key in _sources.Keys.ToList()) Remove(key);

            try { _output?.Flush(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
        }

        public void Dispose() => Stop();
    }
}
