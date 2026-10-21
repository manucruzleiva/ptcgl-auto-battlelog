using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Same runner as tests/CoreTests: numbered sections, one line per check, exit code = failures.
static partial class Program
{
    static int failures, checks;
    static readonly List<string> tempDirs = new List<string>();

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        checks++;
        if (!ok) failures++;
    }

    static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; }
        catch (T) { return true; }
        catch (Exception e) { Console.WriteLine("    (threw " + e.GetType().Name + ": " + e.Message + ")"); return false; }
    }

    static async Task<Exception> Catch(Func<Task> a)
    {
        try { await a(); return null; }
        catch (Exception e) { return e; }
    }

    static bool NoThrow(Action a)
    {
        try { a(); return true; }
        catch (Exception e) { Console.WriteLine("    (threw " + e.GetType().Name + ": " + e.Message + ")"); return false; }
    }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ptcgl-tray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        tempDirs.Add(d);
        return d;
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(d.FullName, "src")))
                return d.FullName;
        throw new DirectoryNotFoundException("repo root not found above " + AppContext.BaseDirectory);
    }

    static string[] FilesUnder(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : new string[0];

    static async Task<int> Main()
    {
        try
        {
            AppPathsTests();
            GameLocatorTests();
            PayloadTests();
            GameInstallTests();
            ConfigTests();
            SettingsTests();
            PluginStatusTests();
            ManifestTests();
            VersionTests();
            TrustedUrlTests();
            await DownloadTests();
            await FetchManifestTests();
            SelfUpdateTests();
            LogFileNameTests();
            TrayLogTests();
            PortablePathsTests();
            DisclaimerTests();
            CommandLineTests();
            LegacyInstallTests();
            DaemonMonitorTests();
            SaveNotificationTests();
            IcoFileTests();
            ConfigExtrasTests();
            StatusFileCrossTests();
            LogNameCrossTests();
            LogsFolderWatcherCrossTests();
            ConfigCrossTests();
            BuildNamesCrossTests();
            await ManifestCrossTests();
            UninstallFlowTests();
            WatchedFolderTests();
            LogsFolderSafetyTests();
            SealSummaryTests();
            await TcgkbApiTests();
            await TcgkbConnectionTests();
            await PokematchApiTests();
            await PokematchUploaderTests();
        }
        catch (Exception e)
        {
            Console.WriteLine("  FAIL unexpected exception: " + e);
            failures++;
        }
        finally
        {
            foreach (var d in tempDirs) try { Directory.Delete(d, true); } catch { }
        }
        Console.WriteLine(failures == 0 ? "\nALL PASSED (" + checks + " checks)" : "\n" + failures + " FAILED (of " + checks + " checks)");
        return failures == 0 ? 0 : 1;
    }
}
