using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace BlazorStudio.Setup
{
    /// <summary>Installations-Optionen aus dem Dialog bzw. der Kommandozeile.</summary>
    public sealed class InstallOptions
    {
        public string TargetDir { get; set; } = Installer.DefaultDir;

        public bool DesktopShortcut { get; set; } = true;

        public bool StartMenuShortcut { get; set; } = true;

        public bool OpenFolderAction { get; set; } = true;
    }

    /// <summary>
    /// Installiert Blazor Studio pro Benutzer (ohne Administratorrechte):
    /// Dateien nach %LocalAppData%\Programs\Blazor Studio, Verknüpfungen, Eintrag in „Apps &amp; Features“.
    /// </summary>
    public static class Installer
    {
        public const string AppName = "Blazor Studio";
        public const string ExeName = "BlazorStudio.exe";
        public const string UninstallExe = "Uninstall.exe";
        /// <summary>Liste aller installierten Dateien (relativ) – nur diese werden später entfernt.</summary>
        public const string ManifestFile = "install.files";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BlazorStudio";

        public static string Version =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        public static bool IsUninstaller
        {
            get
            {
#if UNINSTALLER
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>Bereits installierte Version (aus der Registry), sonst null.</summary>
        public static (string Version, string Location)? Existing()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (k == null) return null;
                var loc = k.GetValue("InstallLocation") as string;
                return loc == null ? ((string, string)?)null : ((k.GetValue("DisplayVersion") as string) ?? "?", loc);
            }
        }

        // ------------------------------------------------------------------
        // Installation
        // ------------------------------------------------------------------

        public static void Install(InstallOptions o, IProgress<(double Percent, string Status)> progress, CancellationToken ct)
        {
            var dir = SafeTargetDir(o.TargetDir);
            progress.Report((0, Text.Get("st.prepare")));
            CloseRunningInstances(dir);
            var installed = new List<string>();

            var asm = Assembly.GetExecutingAssembly();
            using (var payload = asm.GetManifestResourceStream("payload.zip"))
            {
                if (payload == null) throw new InvalidOperationException(Text.Get("err.noPayload"));
                Directory.CreateDirectory(dir);

                // Alte Programmdateien entfernen – nur die laut Manifest installierten (Einstellungen in %AppData% bleiben)
                if (File.Exists(Path.Combine(dir, ManifestFile)))
                {
                    progress.Report((2, Text.Get("st.removeOld")));
                    RemoveListed(dir, keep: null);
                }

                using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                    long total = entries.Sum(e => e.Length), done = 0;
                    foreach (var e in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = Path.GetFullPath(Path.Combine(dir, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Ungültiger Pfad im Paket: " + e.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var src = e.Open())
                        using (var dst = File.Create(target))
                        {
                            src.CopyTo(dst, 81920);
                        }
                        File.SetLastWriteTime(target, e.LastWriteTime.DateTime);
                        installed.Add(e.FullName.Replace('/', '\\'));
                        done += e.Length;
                        progress.Report((3 + 87.0 * done / Math.Max(1, total), Text.Get("st.copy") + "  " + e.Name));
                    }
                }
            }

            // Deinstallationsprogramm
            using (var un = asm.GetManifestResourceStream(UninstallExe))
            {
                if (un != null)
                {
                    using (var f = File.Create(Path.Combine(dir, UninstallExe))) un.CopyTo(f);
                    installed.Add(UninstallExe);
                }
            }
            File.WriteAllLines(Path.Combine(dir, ManifestFile), installed);

            progress.Report((92, Text.Get("st.shortcuts")));
            var exe = Path.Combine(dir, ExeName);
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
            if (o.StartMenuShortcut) CreateShortcut(startMenu, exe, dir, Text.Get("app.description"));
            else TryDelete(startMenu);
            if (o.DesktopShortcut) CreateShortcut(desktop, exe, dir, Text.Get("app.description"));
            else TryDelete(desktop);

            progress.Report((96, Text.Get("st.register")));
            Register(dir, o);
            if (o.OpenFolderAction) RegisterFolderAction(exe);
            else UnregisterFolderAction();
            NotifyShell();
            progress.Report((100, Text.Get("st.done")));
        }

        private static void Register(string dir, InstallOptions o)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                var exe = Path.Combine(dir, ExeName);
                var un = Path.Combine(dir, UninstallExe);
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", AppName);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("UninstallString", "\"" + un + "\"");
                k.SetValue("QuietUninstallString", "\"" + un + "\" /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
                k.SetValue("DesktopShortcut", o.DesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        /// <summary>„Mit Blazor Studio öffnen“ im Kontextmenü von Ordnern und .sln/.slnx/.csproj.</summary>
        private static void RegisterFolderAction(string exe)
        {
            foreach (var cls in new[] { @"Directory\shell\BlazorStudio", @"Directory\Background\shell\BlazorStudio" })
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + cls))
                {
                    k.SetValue("", Text.Get("shell.open"));
                    k.SetValue("Icon", "\"" + exe + "\"");
                    using (var c = k.CreateSubKey("command"))
                        c.SetValue("", "\"" + exe + "\" \"" + (cls.Contains("Background") ? "%V" : "%1") + "\"");
                }
            }
            foreach (var ext in new[] { ".sln", ".slnx", ".csproj", ".razor" })
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\SystemFileAssociations\" + ext + @"\shell\BlazorStudio"))
                {
                    k.SetValue("", Text.Get("shell.open"));
                    k.SetValue("Icon", "\"" + exe + "\"");
                    using (var c = k.CreateSubKey("command")) c.SetValue("", "\"" + exe + "\" \"%1\"");
                }
            }
        }

        private static void UnregisterFolderAction()
        {
            foreach (var cls in new[] { @"Directory\shell\BlazorStudio", @"Directory\Background\shell\BlazorStudio" })
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + cls, false);
            foreach (var ext in new[] { ".sln", ".slnx", ".csproj", ".razor" })
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\" + ext + @"\shell\BlazorStudio", false);
        }

        // ------------------------------------------------------------------
        // Deinstallation
        // ------------------------------------------------------------------

        public static void Uninstall(bool removeSettings, IProgress<(double Percent, string Status)> progress)
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var existing = Existing();
            if (existing.HasValue && Directory.Exists(existing.Value.Location)) dir = existing.Value.Location;
            progress.Report((5, Text.Get("st.prepare")));
            CloseRunningInstances(dir);

            progress.Report((20, Text.Get("st.shortcuts")));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
            UnregisterFolderAction();
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

            progress.Report((40, Text.Get("st.removeFiles")));
            RemoveListed(dir, keep: Assembly.GetExecutingAssembly().Location);
            progress.Report((90, Text.Get("st.removeFiles")));

            if (removeSettings)
                TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlazorStudio"));

            NotifyShell();
            progress.Report((100, Text.Get("st.uninstalled")));
        }

        /// <summary>Uninstall.exe und den Programmordner nach dem Beenden löschen.</summary>
        public static void ScheduleSelfDelete()
        {
            var self = Assembly.GetExecutingAssembly().Location;
            var dir = Path.GetDirectoryName(self);
            var cmd = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + self + "\" & rmdir /q \"" + dir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }

        // ------------------------------------------------------------------
        // Hilfen
        // ------------------------------------------------------------------

        /// <summary>
        /// Zielordner absichern: Ist der gewählte Ordner nicht leer und keine Blazor-Studio-Installation,
        /// wird ein Unterordner „Blazor Studio“ verwendet – so landen nie fremde Dateien im Programmordner.
        /// </summary>
        public static string SafeTargetDir(string chosen)
        {
            var dir = Path.GetFullPath(chosen.Trim().Trim('"'));
            var root = Path.GetPathRoot(dir);
            if (string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return Path.Combine(dir, AppName);
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()
                && !File.Exists(Path.Combine(dir, ManifestFile)) && !File.Exists(Path.Combine(dir, ExeName)))
                return Path.Combine(dir, AppName);
            return dir;
        }

        /// <summary>Entfernt die laut Manifest installierten Dateien und danach leere Unterordner.</summary>
        private static void RemoveListed(string dir, string keep)
        {
            var manifest = Path.Combine(dir, ManifestFile);
            if (!File.Exists(manifest)) return;
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in File.ReadAllLines(manifest))
            {
                if (rel.Length == 0 || rel.Contains("..") || Path.IsPathRooted(rel)) continue;
                var full = Path.GetFullPath(Path.Combine(dir, rel));
                if (!full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (keep != null && string.Equals(full, keep, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(full);
                for (var d = Path.GetDirectoryName(full); d != null && d.Length > dir.Length; d = Path.GetDirectoryName(d)) dirs.Add(d);
            }
            TryDelete(manifest);
            foreach (var d in dirs.OrderByDescending(x => x.Length))
            {
                try
                {
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>Läuft Blazor Studio aus diesem Ordner? Dann beenden (sonst sind Dateien gesperrt).</summary>
        private static void CloseRunningInstances(string dir)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path == null || !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                    p.CloseMainWindow();
                    if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        public static bool IsRunning(string dir)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    if (p.MainModule?.FileName?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true) return true;
                }
                catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                {
                }
            }
            return false;
        }

        private static void CreateShortcut(string lnk, string target, string workDir, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic s = shell.CreateShortcut(lnk);
                s.TargetPath = target;
                s.WorkingDirectory = workDir;
                s.IconLocation = target + ",0";
                s.Description = description;
                s.Save();
                Marshal.FinalReleaseComObject(s);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        private static void NotifyShell() => SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED

        private static long DirSize(string dir)
        {
            try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
            catch (IOException) { return 0; }
        }

        private static void TryDelete(string f)
        {
            try
            {
                if (!File.Exists(f)) return;
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteDir(string d)
        {
            try
            {
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Ist die WebView2-Runtime installiert? (Auf Windows 11 immer; auf Windows 10 meist über Edge.)</summary>
        public static bool HasWebView2()
        {
            const string id = @"\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            foreach (var (hive, path) in new[] { (Registry.LocalMachine, @"SOFTWARE\WOW6432Node" + id), (Registry.LocalMachine, @"SOFTWARE" + id), (Registry.CurrentUser, @"Software" + id) })
            {
                using (var k = hive.OpenSubKey(path))
                {
                    var pv = k?.GetValue("pv") as string;
                    if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return true;
                }
            }
            return false;
        }

        /// <summary>Ist ein .NET SDK installiert? (Nötig, um Projekte zu bauen und zu starten.)</summary>
        public static bool HasDotnetSdk()
        {
            try
            {
                var psi = new ProcessStartInfo("dotnet", "--list-sdks") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return o.Trim().Length > 0;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
            {
                return false;
            }
        }

        public static void Launch(string dir)
        {
            var exe = Path.Combine(dir, ExeName);
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
        }

        // ------------------------------------------------------------------
        // Stiller Modus
        // ------------------------------------------------------------------

        private static string Arg(string[] args, string name)
        {
            var a = args.FirstOrDefault(x => x.TrimStart('/', '-').StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            return a?.Substring(a.IndexOf('=') + 1).Trim('"');
        }

        /// <summary>Optionen der bestehenden Installation (Ordner, Desktop-Verknüpfung, Kontextmenü), sonst null.</summary>
        public static InstallOptions PreviousOptions()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (!(k?.GetValue("InstallLocation") is string loc)) return null;
                using (var ctx = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\BlazorStudio"))
                {
                    return new InstallOptions
                    {
                        TargetDir = loc,
                        DesktopShortcut = !(k.GetValue("DesktopShortcut") is int desk) || desk != 0,
                        StartMenuShortcut = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk")),
                        OpenFolderAction = ctx != null,
                    };
                }
            }
        }

        public static bool TryRunSilent(string[] args, out int exitCode)
        {
            exitCode = 0;
            var flags = new HashSet<string>(args.Select(a => a.Split('=')[0].TrimStart('/', '-').ToLowerInvariant()));
            if (!flags.Contains("s") && !flags.Contains("silent") && !flags.Contains("quiet")) return false;
            var progress = new Progress<(double, string)>(_ => { });
            try
            {
                if (IsUninstaller)
                {
                    Uninstall(flags.Contains("removesettings"), progress);
                    ScheduleSelfDelete();
                }
                else
                {
                    // Auto-Update aus der App: warten, bis die alte Instanz beendet ist
                    var wait = Arg(args, "WaitPid");
                    if (wait != null && int.TryParse(wait, out var pid))
                    {
                        try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(60000); }
                        catch (ArgumentException) { }
                    }
                    // Bei einem Update die bisherigen Entscheidungen übernehmen
                    var previous = PreviousOptions();
                    var o = previous ?? new InstallOptions();
                    if (flags.Contains("nodesktop")) o.DesktopShortcut = false;
                    var d = Arg(args, "D");
                    if (d != null) o.TargetDir = d;
                    Install(o, progress, CancellationToken.None);
                    if (flags.Contains("launch")) Launch(SafeTargetDir(o.TargetDir));
                }
            }
            catch (Exception)
            {
                exitCode = 1;
            }
            return true;
        }
    }
}
