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
