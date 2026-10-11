using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace BlazorStudio.Setup
{
    public partial class MainWindow : Window
    {
        private enum Stage { Options, Working, Done }

        private Stage _stage = Stage.Options;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private string _installedDir;

        public MainWindow()
        {
            InitializeComponent();
            VersionText.Text = Text.F("version", Installer.Version);
            SecondaryButton.Content = Text.Get("btn.cancel");
            TargetLabel.Text = Text.Get("target");
            BrowseButton.Content = Text.Get("browse");
            OptDesktop.Content = Text.Get("opt.desktop");
            OptStartMenu.Content = Text.Get("opt.startmenu");
            OptFolder.Content = Text.Get("opt.folder");
            OptRemoveSettings.Content = Text.Get("opt.removeSettings");

            var existing = Installer.Existing();
            if (Installer.IsUninstaller)
            {
                Title = WindowTitle.Text = Text.Get("title.uninstall");
                Heading.Text = Text.Get("title.uninstall");
                Lead.Text = Text.Get("lead.uninstall");
                InstallOptions.Visibility = Visibility.Collapsed;
                OptRemoveSettings.Visibility = Visibility.Visible;
                PrimaryButton.Content = Text.Get("btn.uninstall");
            }
            else
            {
                var update = existing.HasValue;
                Title = WindowTitle.Text = Text.Get(update ? "title.update" : "title.install");
                Heading.Text = Text.Get(update ? "title.update" : "title.install");
                Lead.Text = update ? Text.F("lead.update", existing.Value.Version) : Text.Get("lead");
                PrimaryButton.Content = Text.Get(update ? "btn.update" : "btn.install");
                TargetBox.Text = update ? existing.Value.Location : Installer.DefaultDir;
                Footnote.Text = Text.F("space", PayloadSizeMb());
                ShowWarnings(update ? existing.Value.Location : null);
            }

            Loaded += (_, __) => Animate();
        }

        private void ShowWarnings(string existingDir)
        {
            var msgs = new System.Collections.Generic.List<string>();
            if (!Installer.HasWebView2()) msgs.Add(Text.Get("warn.webview"));
            if (existingDir != null && Installer.IsRunning(existingDir)) msgs.Add(Text.Get("warn.running"));
            // SDK-Prüfung kostet einen Prozessstart – im Hintergrund
            Task.Run(() => Installer.HasDotnetSdk()).ContinueWith(t =>
            {
                if (!t.Result) msgs.Add(Text.Get("warn.sdk"));
                Dispatcher.Invoke(() =>
                {
                    Warning.Text = string.Join("\n", msgs);
                    Warning.Visibility = msgs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            });
        }

        private static long PayloadSizeMb()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                // entpackt etwa 2,6× so groß wie das Archiv
                return s == null ? 0 : (long)(s.Length * 2.6 / 1024 / 1024 / 10) * 10;
            }
        }

        private void Animate()
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var lift = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Logo.BeginAnimation(OpacityProperty, fade);
            LogoShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, lift);
            PageOptions.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)));
        }

        private void Show(Stage stage)
        {
            _stage = stage;
            PageOptions.Visibility = stage == Stage.Options ? Visibility.Visible : Visibility.Collapsed;
            PageProgress.Visibility = stage == Stage.Working ? Visibility.Visible : Visibility.Collapsed;
            PageDone.Visibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;
            var page = stage == Stage.Working ? (FrameworkElement)PageProgress : stage == Stage.Done ? PageDone : PageOptions;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            PrimaryButton.Visibility = stage == Stage.Working ? Visibility.Hidden : Visibility.Visible;
            if (stage == Stage.Working) SecondaryButton.Visibility = Visibility.Hidden;
            else if (stage == Stage.Options || !Installer.IsUninstaller) SecondaryButton.Visibility = Visibility.Visible;
        }

        private async void OnPrimary(object sender, RoutedEventArgs e)
        {
            switch (_stage)
            {
                case Stage.Options:
                    await RunAsync();
                    break;
                case Stage.Done:
                    if (!Installer.IsUninstaller && _installedDir != null) Installer.Launch(_installedDir);
                    Close();
                    break;
            }
        }

        private async Task RunAsync()
        {
            Show(Stage.Working);
            PrimaryButton.IsEnabled = false;
            SecondaryButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            ProgressHeading.Text = Installer.IsUninstaller ? Text.Get("btn.uninstall") + " …" : Text.Get(Installer.Existing().HasValue ? "btn.update" : "btn.install") + " …";
            var progress = new Progress<(double Percent, string Status)>(p =>
            {
                Bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new DoubleAnimation(p.Percent, TimeSpan.FromMilliseconds(180)));
                ProgressPercent.Text = Math.Round(p.Percent) + " %";
                ProgressStatus.Text = p.Status;
            });
            try
            {
                if (Installer.IsUninstaller)
                {
                    var removeSettings = OptRemoveSettings.IsChecked == true;
                    await Task.Run(() => Installer.Uninstall(removeSettings, progress));
                    DoneHeading.Text = Text.Get("done.uninstalled");
                    DoneText.Text = Text.Get("done.uninstalledText");
                    PrimaryButton.Content = Text.Get("btn.close");
                    SecondaryButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    var o = new InstallOptions
                    {
                        TargetDir = TargetBox.Text,
                        DesktopShortcut = OptDesktop.IsChecked == true,
                        StartMenuShortcut = OptStartMenu.IsChecked == true,
                        OpenFolderAction = OptFolder.IsChecked == true,
                    };
                    _installedDir = Installer.SafeTargetDir(o.TargetDir);
                    o.TargetDir = _installedDir;
                    await Task.Run(() => Installer.Install(o, progress, _cts.Token));
                    DoneHeading.Text = Text.Get("done.title");
                    DoneText.Text = Text.Get("done.text");
                    PrimaryButton.Content = Text.Get("btn.launch");
                    SecondaryButton.Content = Text.Get("btn.close");
                }
                await Task.Delay(350);
                Show(Stage.Done);
            }
            catch (Exception ex)
            {
                var msg = ex is IOException || ex is UnauthorizedAccessException ? Text.Get("err.locked") + "\n\n" + ex.Message : ex.Message;
                MessageBox.Show(this, msg, Text.Get("err.title"), MessageBoxButton.OK, MessageBoxImage.Error);
                Show(Stage.Options);
            }
            finally
            {
                PrimaryButton.IsEnabled = true;
                SecondaryButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }

        private void OnSecondary(object sender, RoutedEventArgs e) => Close();

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = Text.Get("pick.folder"), ShowNewFolderButton = true, SelectedPath = TargetBox.Text })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) TargetBox.Text = Installer.SafeTargetDir(dlg.SelectedPath);
            }
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (Installer.IsUninstaller && _stage == Stage.Done) Installer.ScheduleSelfDelete();
        }
    }
}
