using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using KamCapture.Interop;

namespace KamCapture.Services
{
    /// <summary>
    /// System-wide shortcuts, hung off a message-only window so they work
    /// whether or not any KAM window is open.
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _handlers = new();
        private int _nextId = 0xC0DE;

        public HotkeyService()
        {
            var parameters = new HwndSourceParameters("KAM Capture Tool hotkeys")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = new IntPtr(-3)   // HWND_MESSAGE
            };
            _source = new HwndSource(parameters);
            _source.AddHook(Hook);
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
            if (_handlers.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        /// <summary>Returns false when the combination is already taken by something else.</summary>
        public bool Register(string chord, Action action)
        {
            if (!TryParse(chord, out uint mods, out uint vk)) return false;

            int id = _nextId++;
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods | NativeMethods.MOD_NOREPEAT, vk))
                return false;

            _handlers[id] = action;
            return true;
        }

        public void UnregisterAll()
        {
            foreach (var id in _handlers.Keys)
                NativeMethods.UnregisterHotKey(_source.Handle, id);
            _handlers.Clear();
        }

        public static bool TryParse(string chord, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(chord)) return false;

            var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
            string? keyPart = null;

            foreach (var raw in parts)
            {
                var p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl" or "control": mods |= NativeMethods.MOD_CONTROL; break;
                    case "shift": mods |= NativeMethods.MOD_SHIFT; break;
                    case "alt": mods |= NativeMethods.MOD_ALT; break;
                    case "win": mods |= NativeMethods.MOD_WIN; break;
                    default: keyPart = p; break;
                }
            }

            if (keyPart == null) return false;

            if (keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0]))
            {
                vk = char.IsDigit(keyPart[0])
                    ? (uint)(0x30 + (keyPart[0] - '0'))
                    : (uint)char.ToUpperInvariant(keyPart[0]);
                return true;
            }

            if (Enum.TryParse<Key>(keyPart, ignoreCase: true, out var key))
            {
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                return vk != 0;
            }

            return false;
        }

        public void Dispose()
        {
            UnregisterAll();
            _source.RemoveHook(Hook);
            _source.Dispose();
        }
    }
}
