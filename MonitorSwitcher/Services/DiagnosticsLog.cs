using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WorkMonitorSwitcher.Services
{
    internal sealed class DiagnosticsLog
    {
        private const int MaxLines = 300;
        private readonly string _path;
        private readonly object _sync = new();

        public DiagnosticsLog(string appDataDir)
        {
            _path = Path.Combine(appDataDir, "diagnostics.log");
        }

        public void Write(string message)
        {
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                    File.AppendAllLines(_path, new[] { line });
                    Trim();
                }
                catch
                {
                    // Diagnostics must never affect monitor actions.
                }
            }
        }

        public string Read()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(_path))
                        return "No diagnostic events have been recorded yet.";

                    return string.Join(Environment.NewLine, File.ReadLines(_path).TakeLast(MaxLines));
                }
                catch
                {
                    return "Unable to read diagnostics log.";
                }
            }
        }

        private void Trim()
        {
            var lines = File.ReadLines(_path).TakeLast(MaxLines).ToList();
            File.WriteAllLines(_path, lines);
        }
    }
}
