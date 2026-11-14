using System;
using System.IO;
using System.Text;

namespace Launcher.Host
{
    /// <summary>
    /// Thread-safe rotating file logger for the resident launcher process.
    /// </summary>
    internal static class Logger
    {
        private static readonly object Sync = new object();
        private const long LimitBytes = 256 * 1024;
        private static string _path;
        private static long _bytes;

        public static void Init(string path)
        {
            _path = path;
            try
            {
                lock (Sync)
                {
                    var f = new FileInfo(_path);
                    if (f.Exists && f.Length > LimitBytes) { f.Delete(); _bytes = 0; }
                    else _bytes = f.Exists ? f.Length : 0;
                }
            }
            catch (Exception) { }
        }

        public static void Write(string message)
        {
            if (_path == null) return;
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + "\n";
                lock (Sync)
                {
                    int n = Encoding.UTF8.GetByteCount(line);
                    _bytes += n;
                    if (_bytes > LimitBytes)
                    {
                        _bytes = n;
                        try { File.Delete(_path); } catch (Exception) { }
                    }
                    File.AppendAllText(_path, line);
                }
            }
            catch (Exception) { }
        }
    }
}
