using System;
using System.Collections.Generic;
using System.Text;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// The tray's command line (docs/ARCHITECTURE.md, "Command line"). Flags are case-insensitive;
    /// anything unknown is kept aside for the log instead of failing: an old Run key or a shortcut
    /// edited by hand must still start the app.
    /// </summary>
    public sealed class CommandLine
    {
        public const string BackgroundFlag = "--background";
        public const string FirstRunFlag = "--first-run";
        public const string UpdatedFromFlag = "--updated-from";
        public const string PortableFlag = "--portable";
        public const string UninstallFlag = "--uninstall";
        public const string QuietFlag = "--quiet";

        private readonly List<string> _unknown = new List<string>();

        public bool Background { get; private set; }
        public bool FirstRun { get; private set; }
        public bool Portable { get; private set; }
        public bool Uninstall { get; private set; }
        public bool Quiet { get; private set; }
        /// <summary>The version that was replaced; "" when the flag came without one; null without the flag.</summary>
        public string UpdatedFrom { get; private set; }
        public bool Updated => UpdatedFrom != null;
        public IReadOnlyList<string> Unknown => _unknown;

        public static CommandLine Parse(IEnumerable<string> args)
        {
            var cl = new CommandLine();
            var list = new List<string>();
            if (args != null) foreach (var a in args) if (a != null) list.Add(a.Trim());

            for (int i = 0; i < list.Count; i++)
            {
                var arg = list[i];
                if (arg.Length == 0) continue;
                var lower = arg.ToLowerInvariant();
                if (lower == BackgroundFlag) cl.Background = true;
                else if (lower == FirstRunFlag) cl.FirstRun = true;
                else if (lower == PortableFlag) cl.Portable = true;
                else if (lower == UninstallFlag) cl.Uninstall = true;
                else if (lower == QuietFlag) cl.Quiet = true;
                else if (lower == UpdatedFromFlag)
                {
                    if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) cl.UpdatedFrom = list[++i];
                    else cl.UpdatedFrom = "";
                }
                else if (lower.StartsWith(UpdatedFromFlag + "=", StringComparison.Ordinal)) cl.UpdatedFrom = arg.Substring(UpdatedFromFlag.Length + 1).Trim();
                else cl._unknown.Add(arg);
            }
            return cl;
        }

        /// <summary>
        /// One argument quoted for CreateProcess / CommandLineToArgvW: quotes only when needed,
        /// backslashes doubled only where they precede a quote.
        /// </summary>
        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
                else sb.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        public static string Join(IEnumerable<string> args)
        {
            var parts = new List<string>();
            if (args != null) foreach (var a in args) parts.Add(Quote(a));
            return string.Join(" ", parts);
        }

        /// <summary>The Run key value: "&lt;exe&gt;" --background, plus --portable for a portable copy without its marker.</summary>
        public static string RunCommand(string exe, bool appendPortable)
        {
            var args = new List<string> { BackgroundFlag };
            if (appendPortable) args.Add(PortableFlag);
            return "\"" + exe + "\" " + Join(args);
        }
    }
}
