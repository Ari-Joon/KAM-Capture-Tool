using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using KamCapture.Interop;

namespace KamCapture.Setup
{
    public partial class SetupWindow : Window
    {
        /// <summary>True when the user chose to keep running this copy as-is.</summary>
        public bool RunPortable { get; private set; }

        private bool _replaces;

        /// <summary>
        /// Opened over a copy that is already running — a newer download,
        /// usually. The running copy is asked to close only once the user
        /// clicks to go ahead; until then nothing is touched.
        /// </summary>
        public bool ReplacesRunningCopy
        {
            get => _replaces;
            init
            {
                _replaces = value;
                if (!value) return;

                var running = Installer.InstalledVersion;
                var mine = Installer.Version;
                bool newer = System.Version.TryParse(running, out var r) &&
                             System.Version.TryParse(mine, out var m) && m > r;

                Title = newer ? "Update KAM Capture Tool" : "Install KAM Capture Tool";
                BtnInstall.Content = newer ? "Update" : "Install";
                BtnPortable.Visibility = Visibility.Collapsed;
                LblStatus.Text = running == null
                    ? "KAM Capture Tool is running. Installing closes it and puts this copy in its place."
                    : newer
                        ? $"KAM Capture Tool {running} is running. Updating closes it and replaces it with {mine}."
                        : $"KAM Capture Tool {running} is running. Installing closes it and puts this copy of {mine} in its place.";
            }
        }

        public SetupWindow()
        {
            InitializeComponent();
            WindowStyling.ApplyDarkChrome(this);
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/kam-capture.ico")); } catch { }

            LblVersion.Text = $"Version {Installer.Version}  ·  no runtime needed  ·  nothing installed for other users";
            TxtFolder.Text = Installer.InstalledDir ?? Installer.DefaultTarget;

            try
            {
                var size = new FileInfo(Installer.CurrentExe).Length / 1024.0 / 1024.0;
                LblSpace.Text = $"About {size:0} MB. The whole application is this one file.";
            }
            catch { }

            LblStatus.Text = Installer.InstalledDir != null
                ? "An existing installation was found at that location. Installing again will replace it."
                : "";

            // Installing over an existing copy is an update, so start from what
            // that copy has now rather than from the first-install defaults.
            if (Installer.InstalledDir != null)
            {
                var current = Installer.Unattended();
                ChkDesktop.IsChecked = current.DesktopShortcut;
                ChkStartMenu.IsChecked = current.StartMenuShortcut;
                ChkStartup.IsChecked = current.StartWithWindows;
            }
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to install KAM Capture Tool" };
            try
            {
                var start = TxtFolder.Text;
                while (!string.IsNullOrEmpty(start) && !Directory.Exists(start))
                    start = Path.GetDirectoryName(start);
                if (!string.IsNullOrEmpty(start)) dlg.InitialDirectory = start;
            }
            catch { }

            if (dlg.ShowDialog(this) != true) return;

            // Choosing "Programs" should not scatter an exe loose in it.
            var chosen = dlg.FolderName;
            if (!chosen.TrimEnd(Path.DirectorySeparatorChar)
                       .EndsWith(Installer.ProductName, StringComparison.OrdinalIgnoreCase))
                chosen = Path.Combine(chosen, Installer.ProductName);

            TxtFolder.Text = chosen;
        }

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            BtnInstall.IsEnabled = false;
            BtnPortable.IsEnabled = false;
            BtnClose.IsEnabled = false;
            Bar.Visibility = Visibility.Visible;

            // The running copy holds the program file and the shortcuts, and
            // would take the new copy's launch for itself. It goes first.
            if (ReplacesRunningCopy)
            {
                LblStatus.Text = "Closing the running copy…";
                bool closed = await Task.Run(() =>
                    Services.SingleInstance.AskRunningCopyToExit(TimeSpan.FromSeconds(15)));
                if (!closed)
                {
                    Bar.Visibility = Visibility.Collapsed;
                    LblStatus.Text = "KAM Capture Tool is still running. Close it from the tray, then try again.";
                    BtnInstall.IsEnabled = true;
                    BtnClose.IsEnabled = true;
                    return;
                }
            }

            var options = new Installer.Options
            {
                TargetDir = TxtFolder.Text,
                DesktopShortcut = ChkDesktop.IsChecked == true,
                StartMenuShortcut = ChkStartMenu.IsChecked == true,
                StartWithWindows = ChkStartup.IsChecked == true
            };

            string? installed = null;
            string? error = null;

            await Task.Run(() =>
            {
                try
                {
                    installed = Installer.Install(options, step =>
                        Dispatcher.Invoke(() => LblStatus.Text = step + "…"));
                }
                catch (Exception ex) { error = ex.Message; }
            });

            Bar.Visibility = Visibility.Collapsed;

            if (error != null || installed == null)
            {
                LblStatus.Text = "Could not install: " + error;
                BtnInstall.IsEnabled = true;
                BtnPortable.IsEnabled = true;
                BtnClose.IsEnabled = true;
                return;
            }

            LblStatus.Text = "Installed to " + Path.GetDirectoryName(installed);

            try
            {
                Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Installed, but it could not be started.\n\n" + ex.Message,
                    Installer.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            }

            DialogResult = true;
            Close();
        }

        private void OnRunPortable(object sender, RoutedEventArgs e)
        {
            RunPortable = true;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
