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
        private const string PipeName = "KAM.CaptureTool.Instance";
        private static Mutex? _mutex;

        public static bool IsFirst { get; private set; }

        /// <summary>Raised on the UI thread with the second instance's arguments.</summary>
        public static event Action<string[]>? SecondInstance;

        public static bool Claim()
        {
            _mutex = new Mutex(true, @"Local\KAM.CaptureTool.SingleInstance", out bool created);
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

        public static void Release()
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
