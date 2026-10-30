using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Per-user registry entries and shortcuts (docs/ARCHITECTURE.md, "Windows integration").
    /// Everything lives under HKCU and the user's own folders, so nothing needs admin rights.
    /// </summary>
    internal static class WindowsIntegration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppPaths.AppId;
        private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        public static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.ProductName + ".lnk");

        public static string DesktopShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppPaths.LogsFolderName + ".lnk");

        /// <summary>1.0 started its PowerShell watchdog from here.</summary>
        public static string LegacyStartupShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LegacyInstall.StartupShortcutName);

        // ---- Start with Windows -------------------------------------------------------------

        /// <summary>
        /// The Run value starts this exe and Task Manager has not disabled it. The value name is
        /// shared by the installed and portable copies, so it must point at <paramref name="exe"/>.
        /// </summary>
        public static bool IsStartWithWindows(string exe)
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                if (!RunValuePointsTo(run?.GetValue(AppPaths.AppId) as string, exe)) return false;
            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey))
            {
                // Task Manager's switch: first byte even (02, 06) = enabled, odd (03) = disabled.
                var data = approved?.GetValue(AppPaths.AppId) as byte[];
                return data == null || data.Length == 0 || (data[0] & 1) == 0;
            }
        }

        /// <summary>Writes the Run value (a full command line, CommandLine.RunCommand) or deletes it.</summary>
        public static void SetStartWithWindows(string command, bool enabled)
        {
            if (enabled)
            {
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                    run.SetValue(AppPaths.AppId, command, RegistryValueKind.String);
            }
            else
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    run?.DeleteValue(AppPaths.AppId, false);
            }
            // A "disabled" left behind by Task Manager would silently outrank the menu's choice.
            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, true))
                approved?.DeleteValue(AppPaths.AppId, false);
        }

        /// <summary>Removes the Run value only when it starts <paramref name="exe"/> (not another copy).</summary>
        public static void RemoveStartWithWindows(string exe)
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                if (!RunValuePointsTo(run?.GetValue(AppPaths.AppId) as string, exe)) return;
            SetStartWithWindows(null, false);
        }

        private static bool RunValuePointsTo(string command, string exe)
        {
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(exe)) return false;
            var c = command.Trim();
            string target;
            if (c.StartsWith("\"", StringComparison.Ordinal))
            {
                int close = c.IndexOf('"', 1);
                target = close > 1 ? c.Substring(1, close - 1) : c.Substring(1);
            }
            else
            {
                int space = c.IndexOf(' ');
                target = space > 0 ? c.Substring(0, space) : c;
            }
            return PathUtil.Same(target, exe);
        }

        // ---- Apps & Features ----------------------------------------------------------------

        /// <summary>Written at install and refreshed at every start, so DisplayVersion follows OTA updates.</summary>
        public static void RegisterUninstallEntry(AppPaths paths)
        {
            var exe = paths.InstalledExe;
            long sizeKb = 0;
            try { sizeKb = new FileInfo(exe).Length / 1024; }
            catch (Exception) { }

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppInfo.ProductName, RegistryValueKind.String);
                k.SetValue("DisplayVersion", AppInfo.Version, RegistryValueKind.String);
                k.SetValue("Publisher", AppInfo.Publisher, RegistryValueKind.String);
                k.SetValue("DisplayIcon", exe + ",0", RegistryValueKind.String);
                k.SetValue("InstallLocation", paths.Root, RegistryValueKind.String);
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall", RegistryValueKind.String);
                k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --quiet", RegistryValueKind.String);
                k.SetValue("URLInfoAbout", AppInfo.ProjectUrl, RegistryValueKind.String);
                k.SetValue("URLUpdateInfo", AppInfo.ProjectUrl + "/releases", RegistryValueKind.String);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, Math.Min(int.MaxValue, sizeKb)), RegistryValueKind.DWord);
                if (k.GetValue("InstallDate") == null)
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }
        }

        public static bool HasUninstallEntry()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) return k != null;
        }

        public static void RemoveUninstallEntry()
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }

        /// <summary>
        /// Started with "Run as administrator" while UAC is on: the elevated half of a split token
        /// (TokenElevationTypeFull). With UAC off every process of an administrator has the full
        /// token and nothing differs between launches, so that does not count. False when unknown.
        /// </summary>
        public static bool IsElevatedWithUac()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return NativeMethods.GetTokenInformation(identity.Token, NativeMethods.TokenElevationType, out int type, sizeof(int), out _) &&
                           type == NativeMethods.TokenElevationTypeFull;
            }
            catch (Exception) { return false; }
        }

        // ---- Shortcuts ----------------------------------------------------------------------

        public static void CreateStartMenuShortcut(AppPaths paths)
        {
            CreateShortcut(StartMenuShortcut, paths.InstalledExe, null, paths.Root,
                           "Saves every Pok\u00e9mon TCG Live battle log automatically.", paths.InstalledExe + ",0");
        }

        public static void CreateLogsShortcut(string logsFolder)
        {
            CreateShortcut(DesktopShortcut, logsFolder, null, null, "Your Pok\u00e9mon TCG Live battle logs.", null);
        }

        /// <summary>
        /// Removes the Mark of the Web (the Zone.Identifier stream a browser adds to downloads), so
        /// the installed copy starts at logon without an "Open File - Security Warning". Best effort.
        /// </summary>
        public static void Unblock(string file)
        {
            try { NativeMethods.DeleteFile(file + ":Zone.Identifier"); }
            catch (Exception) { }
        }

        /// <summary>
        /// WScript.Shell, late bound: it is on every Windows and needs no interop DLL next to the exe.
        /// </summary>
        public static void CreateShortcut(string lnk, string target, string arguments, string workingDir,
                                          string description, string iconLocation)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell", true);
            object shell = Activator.CreateInstance(type);
            object link = null;
            try
            {
                var dir = Path.GetDirectoryName(lnk);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                link = Com(shell, "CreateShortcut", BindingFlags.InvokeMethod, lnk);
                Com(link, "TargetPath", BindingFlags.SetProperty, target);
                if (!string.IsNullOrEmpty(arguments)) Com(link, "Arguments", BindingFlags.SetProperty, arguments);
                if (!string.IsNullOrEmpty(workingDir)) Com(link, "WorkingDirectory", BindingFlags.SetProperty, workingDir);
                if (!string.IsNullOrEmpty(description)) Com(link, "Description", BindingFlags.SetProperty, description);
                if (!string.IsNullOrEmpty(iconLocation)) Com(link, "IconLocation", BindingFlags.SetProperty, iconLocation);
                Com(link, "Save", BindingFlags.InvokeMethod);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static object Com(object target, string member, BindingFlags flags, params object[] args)
        {
            try { return target.GetType().InvokeMember(member, flags, null, target, args, CultureInfo.InvariantCulture); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        // ---- Finding the game ---------------------------------------------------------------

        /// <summary>
        /// InstallLocation and DisplayIcon of every Apps &amp; Features entry that looks like the game
        /// (HKCU, HKLM and HKLM's 32-bit view). Lazy: only read when the cheaper guesses failed.
        /// </summary>
        public static IEnumerable<string> RegistryGameLocations()
        {
            foreach (var location in ReadRegistryGameLocations()) yield return location;
        }

        private static List<string> ReadRegistryGameLocations()
        {
            var found = new List<string>();
            var roots = new[]
            {
                (RegistryHive.CurrentUser, RegistryView.Default),
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
            };
            foreach (var (hive, view) in roots)
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = root.OpenSubKey(UninstallRoot))
                    {
                        if (uninstall == null) continue;
                        foreach (var name in uninstall.GetSubKeyNames())
                        {
                            using (var entry = uninstall.OpenSubKey(name))
                            {
                                var display = entry?.GetValue("DisplayName") as string;
                                if (display == null ||
                                    (display.IndexOf("TCG Live", StringComparison.OrdinalIgnoreCase) < 0 &&
                                     display.IndexOf("Trading Card Game Live", StringComparison.OrdinalIgnoreCase) < 0)) continue;
                                if (entry.GetValue("InstallLocation") is string location) found.Add(location);
                                if (entry.GetValue("DisplayIcon") is string icon) found.Add(icon);
                            }
                        }
                    }
                }
                catch (Exception) { }   // a locked-down or missing hive is just one source fewer
            }
            return found;
        }
    }

    /// <summary>Where Pokémon TCG Live is, using every Windows source GameLocator knows how to rank.</summary>
    internal static class GameFinder
    {
        public static string Find(string savedDir)
        {
            var candidates = GameLocator.DefaultCandidates(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            return GameLocator.Find(savedDir, candidates, WindowsIntegration.RegistryGameLocations(), GameProcess.RunningDir());
        }
    }

    internal static class GameProcess
    {
        public static bool IsRunning()
        {
            var processes = Process.GetProcessesByName(GameLocator.ProcessName);
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }

        /// <summary>Folder of the running game, or null.</summary>
        public static string RunningDir()
        {
            string dir = null;
            foreach (var p in Process.GetProcessesByName(GameLocator.ProcessName))
            {
                using (p)
                {
                    if (dir != null) continue;
                    var path = NativeMethods.ProcessPath(p.Id);
                    if (path != null) dir = Path.GetDirectoryName(path);
                }
            }
            return dir;
        }
    }

    internal static class Shell
    {
        /// <summary>Opens a folder, file or URL with whatever Windows associates with it.</summary>
        public static void Open(string target)
        {
            using (Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })) { }
        }

        /// <summary>A text file, in Notepad when nothing is associated with its extension (.log often is not).</summary>
        public static void OpenText(string path)
        {
            try { Open(path); }
            catch (Win32Exception)
            {
                using (Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true })) { }
            }
        }

        /// <summary>
        /// Moves files to the Recycle Bin in one shell operation (Explorer's progress window for many),
        /// without its own confirmation: the caller asked. False when Windows reports an error or the player cancelled.
        /// </summary>
        public static bool Recycle(IEnumerable<string> paths, IntPtr owner)
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", paths) + "\0\0",   // double-null-terminated list
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI,
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        private const uint FO_DELETE = 3;
        private const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    }

    internal static class PathUtil
    {
        public static bool Same(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        private static string Normalize(string p)
        {
            return Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static bool TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>A folder as the user thinks of it: "Documents\PTCGL Battle Logs" rather than the full path.</summary>
        public static string Friendly(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return folder;
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd(Path.DirectorySeparatorChar);
                if (docs.Length > 0 && folder.StartsWith(docs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine("Documents", folder.Substring(docs.Length + 1));
            }
            catch (Exception) { }
            return folder;
        }
    }

    internal static class NativeMethods
    {
        private const int ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr icon);

        internal const int TokenElevationType = 18;       // TOKEN_INFORMATION_CLASS
        internal const int TokenElevationTypeFull = 2;    // TOKEN_ELEVATION_TYPE: elevated, UAC on

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

        /// <summary>The Win32 call: unlike File.Delete it accepts a stream name ("file:Zone.Identifier").</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DeleteFileW")]
        internal static extern bool DeleteFile(string path);

        /// <summary>
        /// Full path of a process's exe. Works where Process.MainModule throws (elevated or
        /// protected processes need only "limited information" rights here). Null when unknown.
        /// </summary>
        public static string ProcessPath(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(32768);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(handle); }
        }
    }
}
