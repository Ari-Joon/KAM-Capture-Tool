using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace KamCapture.Services
{
    /// <summary>
    /// Only one copy runs, because only one copy can own the global shortcuts.
    /// A second launch hands its arguments to the first and exits quietly —
    /// clicking the desktop shortcut while the tool sits in the tray should
    /// open it, not tell you off.
    /// </summary>
    public static class SingleInstance
    {
        // A sandboxed copy (the update check) gets names of its own, so it
        // never meets a real copy running on the same desktop.
        private static readonly string PipeName = "KAM.CaptureTool.Instance" + Sandbox.Suffix;
        private static readonly string MutexName = @"Local\KAM.CaptureTool.SingleInstance" + Sandbox.Suffix;
        private static Mutex? _mutex;

        public static bool IsFirst { get; private set; }

        /// <summary>Raised on the UI thread with the second instance's arguments.</summary>
        public static event Action<string[]>? SecondInstance;

        public static bool Claim()
        {
            _mutex = new Mutex(true, MutexName, out bool created);
            IsFirst = created;
            if (created) StartListener();
            return created;
        }

        /// <summary>Pass our arguments to the running copy. True if it answered.</summary>
        public static bool HandOver(string[] args)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1500);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(string.Join("", args));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not hand over to the running copy: " + ex.Message);
                return false;
            }
        }

        private static void StartListener()
        {
            var thread = new Thread(Listen)
            {
                IsBackground = true,
                Name = "KAM instance listener"
            };
            thread.Start();
        }

        private static void Listen()
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    server.WaitForConnection();

                    using var reader = new StreamReader(server);
                    var line = reader.ReadLine() ?? "";
                    var args = line.Length == 0
                        ? Array.Empty<string>()
                        : line.Split('', StringSplitOptions.RemoveEmptyEntries);

                    var app = System.Windows.Application.Current;
                    app?.Dispatcher.BeginInvoke(new Action(() => SecondInstance?.Invoke(args)));
                }
                catch (Exception ex)
                {
                    Log.Warn("Instance listener: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>True when another copy holds the single-instance lock.</summary>
        public static bool IsAnotherCopyRunning()
        {
            if (IsFirst) return false;
            try
            {
                using var existing = Mutex.OpenExisting(MutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch { return true; }
        }

        /// <summary>
        /// Ask a running copy to close, and wait for it to go. Setup commands
        /// need this: an uninstall handed over to the running copy would simply
        /// open its window, and a running copy cannot be deleted anyway.
        /// </summary>
        public static bool AskRunningCopyToExit(TimeSpan timeout)
        {
            if (!IsAnotherCopyRunning()) return true;
            HandOver(new[] { "--exit" });

            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (!IsAnotherCopyRunning()) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        public static void Release()
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
