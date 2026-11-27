using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tuner.Core;
using Tuner.Native;

namespace Tuner.Tvs
{
    /// <summary>
    /// Reads Samsung's local <c>slive-daemon</c> cached IPG schedules (<c>meta_schedule/*/*.gz</c>) and channel
    /// mapping (<c>freesia_ontv_*.db</c>) as a read-only fallback when <c>tvs-daemon</c> has no DVB EIT data
    /// for off-multiplex channels. Downloads nothing and writes nothing.
    /// </summary>
    internal sealed class SliveScheduleSource
    {
        private const string DaemonDir = "/opt/usr/home/owner/apps_rw/com.samsung.tv.slive-daemon";
        private const string DbDir = DaemonDir + "/data";
        private const string ScheduleDir = DbDir + "/meta_schedule";
        private const long RecheckMs = 60 * 1000;

        private readonly object _gate = new object();
        private SliveSourceIndex _index;
        private Dictionary<string, List<GuideEvent>> _schedules = new Dictionary<string, List<GuideEvent>>();
        private string _stamp = "";
        private long _lastCheckMs = -RecheckMs;

        /// <summary>Worker thread: loads channel mappings and cached schedules, returning a one-line status.</summary>
        public string Load(IList<Channel> channels)
        {
            var gallery = new List<LogoRow>();
            var tunable = new List<KeyValuePair<ulong, string>>();
            for (int n = 0; n < 4; n++)
            {
                string path = DbDir + "/freesia_ontv_" + n + ".db";
                if (!File.Exists(path)) continue;
                try { ReadDb(path, gallery, tunable); } catch (Exception) { }
            }
            var index = new SliveSourceIndex(gallery, tunable);
            int files;
            Dictionary<string, List<GuideEvent>> map = ReadGzFiles(out files, out string stamp);
            int matched = 0, withEvents = 0;
            if (channels != null)
            {
                foreach (Channel c in channels)
                {
                    string sid = index.SourceIdFor(c);
                    if (sid == null) continue;
                    matched++;
                    List<GuideEvent> ev;
                    if (map.TryGetValue(sid, out ev) && ev.Count > 0) withEvents++;
                }
            }
            lock (_gate)
            {
                _index = index;
                _schedules = map;
                _stamp = stamp;
                _lastCheckMs = Environment.TickCount64;
            }
            return "slive guide: " + files + " gz files, " + map.Count + " sources, channels " + (channels == null ? 0 : channels.Count) + " matched " + matched + " with schedule " + withEvents;
        }

        /// <summary>Worker thread: returns cached programmes for this channel (empty if none).</summary>
        public List<GuideEvent> GetEvents(Channel c)
        {
            var result = new List<GuideEvent>();
            if (c == null) return result;
            RefreshIfChanged();
            lock (_gate)
            {
                if (_index == null) return result;
                string sid = _index.SourceIdFor(c);
                List<GuideEvent> src;
                if (sid == null || !_schedules.TryGetValue(sid, out src)) return result;
                foreach (GuideEvent e in src)
                {
                    result.Add(new GuideEvent { EventId = e.EventId, Start = e.Start, Duration = e.Duration, Title = e.Title });
                }
            }
            return result;
        }

        /// <summary>Worker thread: returns the description of a programme from the local slive cache, or "".</summary>
        public string GetDescription(Channel c, long start, string title)
        {
            if (c == null) return "";
            RefreshIfChanged();
            lock (_gate)
            {
                if (_index == null) return "";
                string sid = _index.SourceIdFor(c);
                List<GuideEvent> src;
                if (sid == null || !_schedules.TryGetValue(sid, out src)) return "";
                foreach (GuideEvent e in src)
                {
                    if (e.Start == start && !string.IsNullOrEmpty(e.Description)) return e.Description;
                }
                if (!string.IsNullOrEmpty(title))
                {
                    foreach (GuideEvent e in src)
                    {
                        if (Math.Abs(e.Start - start) <= 600 && string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Description))
                            return e.Description;
                    }
                }
            }
            return "";
        }

        private void RefreshIfChanged()
        {
            long now = Environment.TickCount64;
            lock (_gate)
            {
                if (now - _lastCheckMs < RecheckMs) return;
                _lastCheckMs = now;
            }
            string currentStamp = ComputeStamp();
            bool changed;
            lock (_gate) changed = currentStamp != _stamp;
            if (!changed) return;
            int files;
            Dictionary<string, List<GuideEvent>> map = ReadGzFiles(out files, out string stamp);
            lock (_gate)
            {
                _schedules = map;
                _stamp = stamp;
            }
        }

        private static string ComputeStamp()
        {
            if (!Directory.Exists(ScheduleDir)) return "";
            var sb = new StringBuilder();
            try
            {
                string[] files = Directory.GetFiles(ScheduleDir, "*.gz", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string f in files)
                {
                    var fi = new FileInfo(f);
                    sb.Append(fi.Name).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('|');
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        private static Dictionary<string, List<GuideEvent>> ReadGzFiles(out int fileCount, out string stamp)
        {
            fileCount = 0;
            stamp = "";
            var map = new Dictionary<string, List<GuideEvent>>();
            if (!Directory.Exists(ScheduleDir)) return map;
            var sb = new StringBuilder();
            try
            {
                string[] files = Directory.GetFiles(ScheduleDir, "*.gz", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        sb.Append(fi.Name).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('|');
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                        using (var sr = new StreamReader(gz, Encoding.UTF8))
                        {
                            SliveScheduleParser.ParseInto(sr.ReadToEnd(), map);
                            fileCount++;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            SliveScheduleParser.FinalizeAll(map);
            stamp = sb.ToString();
            return map;
        }

        private static void ReadDb(string path, List<LogoRow> gallery, List<KeyValuePair<ulong, string>> tunable)
        {
            IntPtr db;
            int rc = Sqlite.sqlite3_open_v2(path, out db, Sqlite.OpenReadOnly, IntPtr.Zero);
            try
            {
                if (rc != Sqlite.Ok) return;
                Sqlite.sqlite3_busy_timeout(db, 300);
                Query(db, "SELECT program_source_id, channel_name, onid, tsid, sid FROM Gallery", s =>
                {
                    gallery.Add(new LogoRow
                    {
                        SourceId = Sqlite.Text(s, 0),
                        Name = Sqlite.Text(s, 1),
                        Onid = LogoNames.HexList(Sqlite.Text(s, 2)),
                        Tsid = LogoNames.HexList(Sqlite.Text(s, 3)),
                        Sid = LogoNames.HexList(Sqlite.Text(s, 4))
                    });
                });
                Query(db, "SELECT TCServiceId, program_source_id FROM TunableChannel", s =>
                {
                    ulong id;
                    if (ulong.TryParse(Sqlite.Text(s, 0), out id))
                        tunable.Add(new KeyValuePair<ulong, string>(id, Sqlite.Text(s, 1)));
                });
            }
            finally { if (db != IntPtr.Zero) Sqlite.sqlite3_close(db); }
        }

        private static void Query(IntPtr db, string sql, Action<IntPtr> row)
        {
            IntPtr stmt;
            int rc = Sqlite.sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero);
            if (rc != Sqlite.Ok) return;
            try
            {
                while ((rc = Sqlite.sqlite3_step(stmt)) == Sqlite.Row) row(stmt);
            }
            finally { Sqlite.sqlite3_finalize(stmt); }
        }
    }
}
