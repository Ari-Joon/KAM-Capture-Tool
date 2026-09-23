using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KamCapture.Interop;

namespace KamCapture.UI
{
    /// <summary>
    /// Asks what to call a capture, with the automatic name already filled in
    /// and selected. Enter accepts it, so naming costs a keystroke when you do
    /// not care and a few seconds when you do.
    /// </summary>
    public sealed class NameDialog : Window
    {
        private readonly TextBox _box;
        private readonly TextBlock _warning;
        private readonly string _folder;
        private readonly string _extension;

        public string ChosenName { get; private set; } = "";

        /// <summary>The full path, with a suffix added if that name is taken.</summary>
        public string ChosenPath => UniquePath(Path.Combine(_folder, Sanitise(ChosenName) + _extension));

        public NameDialog(string suggested, string folder, string extension = ".png")
        {
            _folder = folder;
            _extension = extension;

            Title = "Save capture";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = (Brush)(Application.Current.TryFindResource("Navy") ?? Brushes.Black);
            UseLayoutRounding = true;
            WindowStyling.ApplyDarkChrome(this);

            var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

            root.Children.Add(new TextBlock
            {
                Text = "Name this capture",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)(Application.Current.TryFindResource("Fg") ?? Brushes.White),
                Margin = new Thickness(0, 0, 0, 12)
            });

            _box = new TextBox
            {
                Text = suggested,
                FontSize = 14,
                Padding = new Thickness(9, 7, 9, 7)
            };
            _box.KeyDown += OnKey;
            _box.TextChanged += (_, _) => Validate();
            root.Children.Add(_box);

            _warning = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = (Brush)(Application.Current.TryFindResource("FgDim") ?? Brushes.Gray)
            };
            root.Children.Add(_warning);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };

            var cancel = new Button { Content = "Cancel", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            buttons.Children.Add(cancel);

            var save = new Button
            {
                Content = "Save",
                MinWidth = 96,
                Style = (Style?)Application.Current.TryFindResource("PrimaryButton")
            };
            save.Click += (_, _) => Accept();
            buttons.Children.Add(save);

            root.Children.Add(buttons);
            Content = root;

            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); Validate(); };
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
        }

        private void Accept()
        {
            var name = Sanitise(_box.Text);
            if (name.Length == 0) { Validate(); return; }
            ChosenName = name;
            DialogResult = true;
            Close();
        }

        private void Validate()
        {
            var cleaned = Sanitise(_box.Text);
            if (cleaned.Length == 0)
            {
                _warning.Text = "Give it a name.";
                return;
            }

            var target = Path.Combine(_folder, cleaned + _extension);
            _warning.Text = File.Exists(target)
                ? $"{cleaned}{_extension} already exists — this will be saved as {Path.GetFileName(UniquePath(target))}"
                : $"Saving to {_folder}";
        }

        /// <summary>Strip anything Windows will not accept in a file name.</summary>
        public static string Sanitise(string name)
        {
            name = (name ?? "").Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');
            return name.Trim(' ', '.');
        }

        /// <summary>Never silently replace a capture that is already there.</summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path) ?? "";
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);

            for (int i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(dir, $"{stem}-{i}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
        }
    }
}
