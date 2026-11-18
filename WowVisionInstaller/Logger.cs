using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WowVisionInstaller
{
    /// <summary>
    /// The installer's log. Every line is kept in memory (for the Desktop log on
    /// a failed run and for <see cref="LastLine"/>) AND, once
    /// <see cref="StartSessionFile"/> has run, written straight to a session file
    /// in %LOCALAPPDATA%\WowVisionUpdater\Logs.
    ///
    /// The session file exists because a memory-only log dies with the process:
    /// a player who meets a problem, closes the installer and starts it again to
    /// press "collect logs" would otherwise send a bundle with no record of the
    /// run that went wrong. The last <see cref="KeptSessions"/> runs are kept, so
    /// a self-update restart (which is a run of its own) does not push the
    /// interesting run out. Lines are flushed as they come, so a crash keeps
    /// everything up to it.
    /// </summary>
    public static class Logger
    {
        /// <summary>Session files kept: this run plus the ones before it.</summary>
        public const int KeptSessions = 3;

        public const string LogDirName = "Logs";

        private static readonly List<string> _lines = new List<string>();
        private static readonly object _lock = new object();
        private static bool _hasErrors;
        private static StreamWriter _file;

        /// <summary>
        /// Test hook: where the session files go instead of the updater's own
        /// folder. Null in every production path.
        /// </summary>
        internal static string DirOverride = null;

        public static string LastLine { get; private set; } = "";
        public static bool HasErrors => _hasErrors;

        /// <summary>The folder holding the session files.</summary>
        public static string SessionDir =>
            DirOverride ?? Path.Combine(Shortcut.PersistentDir, LogDirName);

        /// <summary>
        /// Session file N: 1 is the running session, 2 the one before, and so on.
        /// </summary>
        public static string SessionFile(int n) => Path.Combine(SessionDir, $"installer-run{n}.log");

        public static void Info(string msg) => Add("INFO", msg);
        public static void Warning(string msg) { _hasErrors = true; Add("WARN", msg); }

        public static void Error(string msg, Exception ex = null)
        {
            _hasErrors = true;
            Add("ERROR", ex == null ? msg : $"{msg}: {ex.Message}\n{ex.StackTrace}");
        }

        private static void Add(string level, string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {level}: {msg}";
            lock (_lock)
            {
                _lines.Add(line);
                LastLine = $"{level}: {msg}";
                WriteToFile(line);
            }
        }

        private static void WriteToFile(string line)
        {
            if (_file == null) return;
            try { _file.WriteLine(line); }
            catch
            {
                // A full disk or a pulled drive must not take the installer down;
                // the memory copy carries on.
                try { _file.Dispose(); } catch { }
                _file = null;
            }
        }

        /// <summary>
        /// Starts this run's session file: shifts the older ones down (dropping
        /// the oldest), then writes every line logged so far and keeps the file
        /// open for the rest of the run. Never throws. Calling it again (tests)
        /// closes the current file and starts a new session.
        /// </summary>
        public static void StartSessionFile()
        {
            lock (_lock)
            {
                StopSessionFile();
                try
                {
                    Directory.CreateDirectory(SessionDir);
                    for (int n = KeptSessions; n >= 1; n--)
                    {
                        string from = SessionFile(n);
                        if (!File.Exists(from)) continue;
                        if (n == KeptSessions) File.Delete(from);
                        else
                        {
                            string to = SessionFile(n + 1);
                            if (File.Exists(to)) File.Delete(to);
                            File.Move(from, to);
                        }
                    }

                    // FileShare.ReadWrite: the log collector reads this file while
                    // it is still open here.
                    var stream = new FileStream(SessionFile(1), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _file.WriteLine($"WowVision installer {AppVersion.Number()}, run started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    foreach (var line in _lines) _file.WriteLine(line);
                }
                catch (Exception ex)
                {
                    // Most likely a second installer window holding run1 open.
                    // Logged to memory only; the run itself is unaffected.
                    StopSessionFile();
                    string line = $"[{DateTime.Now:HH:mm:ss}] WARN: Could not start the session log in {SessionDir}: {ex.Message}";
                    _lines.Add(line);
                }
            }
        }

        /// <summary>Closes the session file (tests; production lets process exit close it).</summary>
        public static void StopSessionFile()
        {
            lock (_lock)
            {
                if (_file == null) return;
                try { _file.Dispose(); } catch { }
                _file = null;
            }
        }

        /// <summary>True while lines are being written to the session file.</summary>
        public static bool WritingSessionFile => _file != null;

        /// <summary>Every line logged in this run so far.</summary>
        public static string[] Snapshot()
        {
            lock (_lock) return _lines.ToArray();
        }

        /// <summary>
        /// Writes the buffer to the Desktop if there were warnings/errors (or if
        /// <paramref name="force"/>). Returns the path written, or null.
        /// </summary>
        public static string SaveIfNeeded(bool force = false)
        {
            if (!_hasErrors && !force)
                return null;

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string path = Path.Combine(desktop, "WowVisionInstaller.log");
                File.WriteAllLines(path, Snapshot());
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
