using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KamCapture.Controls
{
    /// <summary>A text box that records the next chord you press.</summary>
    public sealed class HotkeyBox : TextBox
    {
        public string Hotkey
        {
            get => Text;
            set { Text = value ?? ""; }
        }

        public HotkeyBox()
        {
            IsReadOnly = true;
            Cursor = Cursors.Hand;
            ToolTip = "Click, then press the combination. Backspace clears it.";
            PreviewKeyDown += OnKey;
            GotFocus += (_, _) => SelectAll();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is Key.Back or Key.Delete) { Text = ""; return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Tab) return;

            var sb = new StringBuilder();
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
            if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
            if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
            if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");

            // A bare letter is a terrible global hotkey; require a modifier.
            if (sb.Length == 0 && key is not (Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6
                or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12 or Key.PrintScreen))
                return;

            sb.Append(KeyName(key));
            Text = sb.ToString();
        }

        private static string KeyName(Key key) => key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
            Key.PrintScreen => "PrintScreen",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            _ => key.ToString()
        };
    }
}
