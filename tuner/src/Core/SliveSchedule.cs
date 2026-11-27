using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tuner.Core
{
    /// <summary>
    /// Maps a broadcast channel to Samsung's slive-daemon <c>program_source_id</c> (used in its local
    /// <c>meta_schedule/*.gz</c> cache) by TCServiceId first, DVB triplet second, and normalized name third.
    /// Pure code so it is unit-tested on desktop.
    /// </summary>
    internal sealed class SliveSourceIndex
    {
        private readonly Dictionary<ulong, LogoRow> _byService = new Dictionary<ulong, LogoRow>();
        private readonly Dictionary<long, LogoRow> _byTriplet = new Dictionary<long, LogoRow>();
        private readonly Dictionary<string, string> _byName = new Dictionary<string, string>();

        private static readonly Dictionary<string, string> NameAliases = new Dictionary<string, string>
        {
            { "fdf", "factoriadeficcion" },
            { "24h", "canal24h" },
            { "tdp", "teledeporte" },
            { "a3", "antena3" },
            { "t5", "telecinco" },
            { "la8valladolid", "la8" },
        };

        public int Rows { get; private set; }

        public SliveSourceIndex(IEnumerable<LogoRow> gallery, IEnumerable<KeyValuePair<ulong, string>> tunable)
        {
            var bySource = new Dictionary<string, LogoRow>();
            if (gallery != null)
            {
                foreach (LogoRow r in gallery)
                {
                    if (r == null || string.IsNullOrEmpty(r.SourceId)) continue;
                    Rows++;
                    if (!bySource.ContainsKey(r.SourceId)) bySource[r.SourceId] = r;
                    int n = Math.Min(r.Onid.Length, Math.Min(r.Tsid.Length, r.Sid.Length));
                    for (int i = 0; i < n; i++)
                    {
                        if (r.Onid[i] < 0 || r.Tsid[i] < 0 || r.Sid[i] < 0) continue;
                        long key = Triplet(r.Onid[i], r.Tsid[i], r.Sid[i]);
                        if (!_byTriplet.ContainsKey(key)) _byTriplet[key] = r;
                    }
                    string norm = NormalizeName(r.Name);
                    if (norm.Length > 0 && !_byName.ContainsKey(norm)) _byName[norm] = r.SourceId;
                }
            }
            if (tunable != null)
            {
                foreach (var t in tunable)
                {
                    LogoRow r;
                    if (!string.IsNullOrEmpty(t.Value) && bySource.TryGetValue(t.Value, out r) && !_byService.ContainsKey(t.Key))
                        _byService[t.Key] = r;
                }
            }
        }

        private static long Triplet(int onid, int tsid, int sid)
        {
            return ((long)(onid & 0xFFFF) << 32) | ((long)(tsid & 0xFFFF) << 16) | (long)(sid & 0xFFFF);
        }

        public static string NormalizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string formD = raw.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (char ch in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                char c = char.ToLowerInvariant(ch);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            string s = sb.ToString();
            // Strip common trailing broadcast suffixes ("uhd", "hd", "tve", "tv") repeatedly when a stem remains.
            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                foreach (string suf in new[] { "uhd", "hd", "tve", "tv" })
                {
                    if (s.Length > suf.Length + 1 && s.EndsWith(suf, StringComparison.Ordinal))
                    {
                        s = s.Substring(0, s.Length - suf.Length);
                        stripped = true;
                        break;
                    }
                }
            }
            string alias;
            if (NameAliases.TryGetValue(s, out alias)) return alias;
            return s;
        }

        public string SourceIdFor(Channel c)
        {
            if (c == null) return null;
            string norm = NormalizeName(c.Name);
            string sid;
            if (norm.Length > 0 && _byName.TryGetValue(norm, out sid)) return sid;
            LogoRow r;
            if (_byService.TryGetValue(c.Id, out r) && NameCompatible(norm, r.Name)) return r.SourceId;
            if (c.Onid >= 0 && c.Tsid >= 0 && c.Sid >= 0 && _byTriplet.TryGetValue(Triplet(c.Onid, c.Tsid, c.Sid), out r) && NameCompatible(norm, r.Name)) return r.SourceId;
            return null;
        }

        private static bool NameCompatible(string chNorm, string galleryName)
        {
            if (string.IsNullOrEmpty(chNorm)) return true;
            string gNorm = NormalizeName(galleryName);
            if (string.IsNullOrEmpty(gNorm)) return true;
            return chNorm == gNorm || chNorm.StartsWith(gNorm, StringComparison.Ordinal) || gNorm.StartsWith(chNorm, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Parses Samsung slive-daemon's cached <c>meta_schedule</c> JSON payloads into <see cref="GuideEvent"/> lists
    /// keyed by <c>program_source_id</c>.
    /// </summary>
    internal static class SliveScheduleParser
    {
        public static void ParseInto(string json, Dictionary<string, List<GuideEvent>> into)
        {
            if (string.IsNullOrWhiteSpace(json) || into == null) return;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement rsp, channels;
                    if (!doc.RootElement.TryGetProperty("rsp", out rsp) || rsp.ValueKind != JsonValueKind.Object) return;
                    if (!rsp.TryGetProperty("channel", out channels) || channels.ValueKind != JsonValueKind.Array) return;
                    foreach (JsonElement ch in channels.EnumerateArray())
                    {
                        if (ch.ValueKind != JsonValueKind.Object) continue;
                        string source = ReadString(ch, "source");
                        if (string.IsNullOrEmpty(source)) continue;
                        JsonElement content;
                        if (!ch.TryGetProperty("content", out content) || content.ValueKind != JsonValueKind.Array) continue;
                        List<GuideEvent> list;
                        if (!into.TryGetValue(source, out list))
                        {
                            list = new List<GuideEvent>();
                            into[source] = list;
                        }
                        foreach (JsonElement item in content.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            string startStr = ReadString(item, "starttime");
                            DateTimeOffset dto;
                            if (string.IsNullOrEmpty(startStr)
                                || !DateTimeOffset.TryParse(startStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                                continue;
                            long dur = ReadLong(item, "duration");
                            if (dur <= 0) continue;
                            string title = GuideText.Clean(ReadString(item, "title"));
                            if (title.Length == 0) continue;
                            string desc = GuideText.Clean(ReadString(item, "desc"));
                            string idStr = ReadString(item, "id");
                            uint idNum;
                            ushort evId = uint.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out idNum)
                                ? (ushort)(idNum & 0xFFFF)
                                : (ushort)(dto.ToUnixTimeSeconds() & 0xFFFF);
                            list.Add(new GuideEvent
                            {
                                EventId = evId,
                                Start = dto.ToUnixTimeSeconds(),
                                Duration = dur,
                                Title = title,
                                Description = desc
                            });
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        public static void FinalizeAll(Dictionary<string, List<GuideEvent>> map)
        {
            if (map == null) return;
            foreach (var kv in map)
            {
                List<GuideEvent> list = kv.Value;
                if (list.Count <= 1) continue;
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
                var dedup = new List<GuideEvent>(list.Count);
                foreach (GuideEvent e in list)
                {
                    if (dedup.Count > 0)
                    {
                        GuideEvent prev = dedup[dedup.Count - 1];
                        if (prev.Start == e.Start)
                        {
                            if (prev.Description.Length == 0 && e.Description.Length > 0) prev.Description = e.Description;
                            continue;
                        }
                    }
                    dedup.Add(e);
                }
                list.Clear();
                list.AddRange(dedup);
            }
        }

        private static string ReadString(JsonElement obj, string prop)
        {
            JsonElement el;
            if (!obj.TryGetProperty(prop, out el)) return "";
            if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
            if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
            return "";
        }

        private static long ReadLong(JsonElement obj, string prop)
        {
            JsonElement el;
            if (!obj.TryGetProperty(prop, out el)) return 0;
            if (el.ValueKind == JsonValueKind.Number) { long n; return el.TryGetInt64(out n) ? n : 0; }
            if (el.ValueKind == JsonValueKind.String)
            {
                long n;
                return long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
            }
            return 0;
        }
    }
}
