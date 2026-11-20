using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher.Core
{
    internal sealed class GeoResult
    {
        public readonly string Name;
        public readonly string Admin1;
        public readonly string Country;
        public readonly string CountryCode;
        public readonly double Latitude;
        public readonly double Longitude;

        public GeoResult(string name, string admin1, string country, string countryCode, double latitude, double longitude)
        {
            Name = name;
            Admin1 = admin1;
            Country = country;
            CountryCode = countryCode;
            Latitude = latitude;
            Longitude = longitude;
        }

        public string DisplayLabel
        {
            get
            {
                var sb = new StringBuilder(Name ?? "");
                if (!string.IsNullOrEmpty(Admin1) && !string.Equals(Admin1, Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(Admin1);
                }
                if (!string.IsNullOrEmpty(Country))
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(Country);
                }
                return sb.ToString();
            }
        }
    }

    internal sealed class GeoSearchOutcome
    {
        public readonly GeoResult[] Results;
        public readonly string Error;

        public GeoSearchOutcome(GeoResult[] results, string error)
        {
            Results = results ?? new GeoResult[0];
            Error = error;
        }
    }

    /// <summary>
    /// Current weather via Open-Meteo for wherever the TV is. The place comes from, in order: a location the user
    /// set, the last one found, or a lookup of the public IP address (about city accuracy, refreshed daily).
    /// When disabled (<see cref="Enabled"/> == false), no network requests are made.
    /// </summary>
    internal static class Weather
    {
        const string DefaultDir = "/opt/usr/home/owner/share";
        const double LocationMaxAgeHours = 24;
        const string Forecast = "https://api.open-meteo.com/v1/forecast?current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto";
        const string Geocoding = "https://geocoding-api.open-meteo.com/v1/search?count=6&format=json";
        // Both answer with the same field names, so one parser serves them.
        static readonly string[] GeoServices = { "https://ipwho.is/", "https://ipapi.co/json/" };

        /// <summary>Where the weather and location caches live; <see cref="Init"/> moves it when the default is missing.</summary>
        public static string CacheDir = DefaultDir;
        static string CachePath { get { return Path.Combine(CacheDir, "vetro-weather.json"); } }
        static string LocationPath { get { return Path.Combine(CacheDir, "vetro-location.txt"); } }

        static bool _manual;
        static double _manualLat, _manualLon;
        static string _manualName;
        static bool _hasRange;
        static int _low, _high;
        static int _refreshBusy;
        static volatile bool _refreshAgain;

        public static bool Enabled = true;
        public static bool ShowLocation = false;
        public static bool ShowMinMax = true;
        public static string LocationName;
        public static bool IsRefreshing { get { return _refreshBusy != 0; } }

        public static string Text;
        /// <summary>Today's low and high, for the small line under the reading. Null until both arrive.</summary>
        public static string Span;
        public static string Icon = "cloud";
        public static string LastError;
        public static DateTime UpdatedUtc;

        public static string TruncateName(string name, int maxChars)
        {
            if (string.IsNullOrEmpty(name) || maxChars < 2 || name.Length <= maxChars) return name;
            return name.Substring(0, maxChars - 1).TrimEnd() + "\u2026";
        }

        /// <summary>Secondary line shown under temperature according to <see cref="ShowLocation"/> and <see cref="ShowMinMax"/>.</summary>
        public static string SubText
        {
            get
            {
                bool hasCity = !string.IsNullOrWhiteSpace(LocationName);
                if (ShowLocation && ShowMinMax)
                {
                    if (hasCity && _hasRange) return TruncateName(LocationName.Trim(), 18) + "  \u00b7  " + Localization.CompactRangeText(_low, _high);
                    if (_hasRange) return Span;
                    if (hasCity) return TruncateName(LocationName.Trim(), 26);
                    return null;
                }
                if (ShowLocation) return hasCity ? TruncateName(LocationName.Trim(), 26) : null;
                if (ShowMinMax) return _hasRange ? Span : null;
                return null;
            }
        }

        public static void Init(string fallbackDir)
        {
            if (!Directory.Exists(CacheDir)) CacheDir = fallbackDir;
        }

        /// <summary>Fixes the place; no IP lookup is made.</summary>
        public static void UseLocation(double latitude, double longitude)
        {
            UseLocation(latitude, longitude, null);
        }

        public static void UseLocation(double latitude, double longitude, string name)
        {
            _manual = true;
            _manualLat = latitude;
            _manualLon = longitude;
            _manualName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (_manualName != null) LocationName = _manualName;
        }

        public static void Configure(Settings s)
        {
            if (s == null) return;
            Enabled = s.EffectiveShowWeather;
            ShowLocation = s.EffectiveWeatherShowLocation;
            ShowMinMax = s.EffectiveWeatherShowMinMax;

            if (s.EffectiveWeatherMode == "manual" && s.HasLocation)
            {
                _manual = true;
                _manualLat = s.Latitude;
                _manualLon = s.Longitude;
                _manualName = string.IsNullOrWhiteSpace(s.LocationName) ? null : s.LocationName.Trim();
                LocationName = _manualName;
            }
            else
            {
                _manual = false;
                _manualName = null;
                double la, lo; string c, city; DateTime t;
                if (ReadLocation(out la, out lo, out c, out t, out city)) LocationName = city;
                else LocationName = null;
            }

            if (!Enabled)
            {
                Text = null;
                Span = null;
                _hasRange = false;
                LastError = null;
            }
        }

        public static void RebuildLabels()
        {
            Span = _hasRange ? Localization.RangeText(_low, _high) : null;
        }

        public static void LoadCache()
        {
            if (!Enabled) return;
            try
            {
                if (File.Exists(CachePath)) Apply(File.ReadAllText(CachePath));
            }
            catch { /* keep previous */ }
        }

        public static Task Refresh()
        {
            if (!Enabled) return Task.CompletedTask;
            if (Interlocked.CompareExchange(ref _refreshBusy, 1, 0) != 0)
            {
                _refreshAgain = true;
                return Task.CompletedTask;
            }
            return Task.Run(() =>
            {
                try
                {
                    do
                    {
                        _refreshAgain = false;
                        if (!Enabled) break;
                        bool manual = _manual;
                        double mLat = _manualLat, mLon = _manualLon;
                        string url = ForecastUrl(manual, mLat, mLon);
                        if (url == null) { LastError = "no location"; continue; }
                        string err;
                        string json = FetchWithError(url, out err);
                        if (json == null) { if (err != null) LastError = err; continue; }
                        if (!Enabled) break;
                        if (Apply(json))
                        {
                            try { File.WriteAllText(CachePath, json); } catch { /* ignore */ }
                        }
                    } while (_refreshAgain && Enabled);
                }
                catch (Exception ex) { LastError = ex.Message; }
                finally { Interlocked.Exchange(ref _refreshBusy, 0); }
            });
        }

        internal static string BuildUrl(double lat, double lon, bool fahrenheit)
        {
            return Forecast + "&latitude=" + lat.ToString("0.###", CultureInfo.InvariantCulture)
                + "&longitude=" + lon.ToString("0.###", CultureInfo.InvariantCulture)
                + (fahrenheit ? "&temperature_unit=fahrenheit" : "");
        }

        internal static string BuildGeocodingUrl(string query, string language)
        {
            string q = Uri.EscapeDataString((query ?? "").Trim());
            string lang = string.IsNullOrWhiteSpace(language) ? "en" : Uri.EscapeDataString(language.Trim());
            return Geocoding + "&name=" + q + "&language=" + lang;
        }

        public static Task<GeoSearchOutcome> SearchCityAsync(string query, string language)
        {
            string trimmed = (query ?? "").Trim();
            if (trimmed.Length < 2)
                return Task.FromResult(new GeoSearchOutcome(new GeoResult[0], null));

            string url = BuildGeocodingUrl(trimmed, language);
            return Task.Run(() =>
            {
                string err;
                string json = FetchWithError(url, out err);
                if (json == null) return new GeoSearchOutcome(new GeoResult[0], err ?? "request failed");
                return new GeoSearchOutcome(ParseGeocoding(json), null);
            });
        }

        static string ForecastUrl(bool manual, double mLat, double mLon)
        {
            double lat, lon;
            if (manual) { lat = mLat; lon = mLon; }
            else if (!ResolveLocation(out lat, out lon)) return null;
            return BuildUrl(lat, lon, Localization.Fahrenheit);
        }

        static bool ResolveLocation(out double lat, out double lon)
        {
            double cLat = 0, cLon = 0; string cCountry = null, cCity = null; DateTime cTime = DateTime.MinValue;
            bool have = ReadLocation(out cLat, out cLon, out cCountry, out cTime, out cCity);
            if (have && cCountry != null) Localization.SetCountry(cCountry);
            if (have && cCity != null) LocationName = cCity;
            if (have && !string.IsNullOrEmpty(cCity) && (DateTime.UtcNow - cTime).TotalHours < LocationMaxAgeHours)
            {
                lat = cLat; lon = cLon; return true;
            }
            foreach (string service in GeoServices)
            {
                string err;
                string body = FetchWithError(service, out err);
                double la, lo;
                if (!ParseGeo(body, out la, out lo)) continue;
                string country = TryString(body, "country_code");
                string city = TryString(body, "city");
                if (country != null) Localization.SetCountry(country);
                if (!string.IsNullOrWhiteSpace(city)) LocationName = city.Trim();
                try
                {
                    string line = la.ToString("R", CultureInfo.InvariantCulture)
                        + " " + lo.ToString("R", CultureInfo.InvariantCulture)
                        + " " + (country ?? "-")
                        + " " + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                        + (string.IsNullOrWhiteSpace(city) ? "" : " " + city.Trim().Replace('\n', ' ').Replace('\r', ' '));
                    File.WriteAllText(LocationPath, line);
                }
                catch { /* ignore */ }
                lat = la; lon = lo;
                return true;
            }
            lat = cLat; lon = cLon;
            return have;   // offline: the last known place is better than none
        }

        static bool ReadLocation(out double lat, out double lon, out string country, out DateTime stamp, out string city)
        {
            lat = lon = 0; country = null; stamp = DateTime.MinValue; city = null;
            try
            {
                if (!File.Exists(LocationPath)) return false;
                string[] p = File.ReadAllText(LocationPath).Trim().Split(new[] { ' ' }, 5, StringSplitOptions.RemoveEmptyEntries);
                long unix;
                if (p.Length < 4
                    || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                    || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
                    || !long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out unix)) return false;
                country = p[2] == "-" ? null : p[2];
                stamp = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                city = p.Length >= 5 && !string.IsNullOrWhiteSpace(p[4]) ? p[4].Trim() : null;
                return true;
            }
            catch { return false; }
        }

        internal static bool ParseGeo(string json, out double lat, out double lon)
        {
            lat = lon = 0;
            if (string.IsNullOrEmpty(json) || json.IndexOf("\"success\":false", StringComparison.Ordinal) >= 0 || json.IndexOf("\"error\":true", StringComparison.Ordinal) >= 0) return false;
            if (!TryNumber(json, "latitude", out lat) || !TryNumber(json, "longitude", out lon)) return false;
            return lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180 && !(lat == 0 && lon == 0);
        }

        internal static GeoResult[] ParseGeocoding(string json)
        {
            if (string.IsNullOrEmpty(json)) return new GeoResult[0];
            int keyIdx = json.IndexOf("\"results\"", StringComparison.Ordinal);
            if (keyIdx < 0) return new GeoResult[0];
            int colon = json.IndexOf(':', keyIdx + 9);
            if (colon < 0) return new GeoResult[0];
            int arrStart = json.IndexOf('[', colon + 1);
            if (arrStart < 0) return new GeoResult[0];

            var list = new List<GeoResult>();
            int i = arrStart + 1;
            int len = json.Length;
            while (i < len)
            {
                char ch = json[i];
                if (ch == ']') break;
                if (ch != '{') { i++; continue; }

                int objStart = i;
                int depth = 0;
                bool inStr = false;
                bool esc = false;
                int objEnd = -1;
                for (; i < len; i++)
                {
                    char c = json[i];
                    if (inStr)
                    {
                        if (esc) esc = false;
                        else if (c == '\\') esc = true;
                        else if (c == '"') inStr = false;
                        continue;
                    }
                    if (c == '"') { inStr = true; continue; }
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) { objEnd = i; i++; break; }
                    }
                }
                if (objEnd < 0) break;

                string obj = json.Substring(objStart, objEnd - objStart + 1);
                string name = TryString(obj, "name");
                double lat, lon;
                if (!string.IsNullOrWhiteSpace(name) && TryNumber(obj, "latitude", out lat) && TryNumber(obj, "longitude", out lon)
                    && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180)
                {
                    string admin1 = TryString(obj, "admin1");
                    string country = TryString(obj, "country");
                    string cc = TryString(obj, "country_code");
                    list.Add(new GeoResult(name, admin1, country, cc, lat, lon));
                }
            }
            return list.ToArray();
        }

        internal static string TryString(string json, string key)
        {
            if (json == null) return null;
            int from = 0;
            string needle = "\"" + key + "\"";
            while (from < json.Length)
            {
                int i = json.IndexOf(needle, from, StringComparison.Ordinal);
                if (i < 0) return null;
                int colon = i + needle.Length;
                while (colon < json.Length && json[colon] == ' ') colon++;
                if (colon >= json.Length || json[colon] != ':') { from = i + 1; continue; }
                int p = colon + 1;
                while (p < json.Length && (json[p] == ' ' || json[p] == '\t' || json[p] == '\r' || json[p] == '\n')) p++;
                if (p >= json.Length) return null;
                if (json[p] != '"') return null;
                p++;
                var sb = new StringBuilder();
                while (p < json.Length)
                {
                    char c = json[p++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\' && p < json.Length)
                    {
                        char e = json[p++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (p + 4 <= json.Length)
                                {
                                    int code;
                                    if (int.TryParse(json.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                        sb.Append((char)code);
                                    p += 4;
                                }
                                break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(c);
                }
                return null;
            }
            return null;
        }

        static string FetchWithError(string url, out string err)
        {
            if (File.Exists("/usr/bin/curl")) return FetchCurl(url, out err);
            return FetchHttp(url, out err);
        }

        static string FetchCurl(string url, out string err)
        {
            err = null;
            try
            {
                var start = new ProcessStartInfo("/usr/bin/curl")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                start.ArgumentList.Add("-sS");
                start.ArgumentList.Add("--connect-timeout");
                start.ArgumentList.Add("3");
                start.ArgumentList.Add("-m");
                start.ArgumentList.Add("4");
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(url);
                using (Process p = Process.Start(start))
                {
                    if (p == null) { err = "curl start failed"; return null; }
                    Task<string> errTask = p.StandardError.ReadToEndAsync();
                    string json = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } err = "curl timeout"; return null; }
                    string stderr = errTask.GetAwaiter().GetResult();
                    if (p.ExitCode != 0 || string.IsNullOrEmpty(json))
                    {
                        err = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : ("curl exit " + p.ExitCode);
                        return null;
                    }
                    return json;
                }
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        static string FetchHttp(string url, out string err)
        {
            err = null;
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(6);
                    return http.GetStringAsync(url).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        internal static bool Apply(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            double temp;
            int code;
            int isDay;
            if (!TryNumber(json, "temperature_2m", out temp)) return false;
            if (!TryInt(json, "weather_code", out code)) code = 1;
            if (!TryInt(json, "is_day", out isDay)) isDay = 1;
            int rounded = (int)Math.Round(temp);
            Text = rounded.ToString(CultureInfo.InvariantCulture) + "\u00b0";
            Icon = IconFor(code, isDay != 0);
            double lo, hi;
            if (TryNumber(json, "temperature_2m_min", out lo) && TryNumber(json, "temperature_2m_max", out hi))
            {
                _low = (int)Math.Round(lo);
                _high = (int)Math.Round(hi);
                _hasRange = true;
                Span = Localization.RangeText(_low, _high);
            }
            else
            {
                _hasRange = false;
                Span = null;
            }
            UpdatedUtc = DateTime.UtcNow;
            LastError = null;
            return true;
        }

        internal static string IconFor(int code, bool isDay = true)
        {
            if (code == 0) return isDay ? "sun" : "moon-stars";
            if (code <= 2) return isDay ? "cloud-sun" : "cloud-moon";
            if (code == 3) return "cloud";
            if (code == 45 || code == 48) return "cloud-fog";
            if (code <= 48) return "cloud";
            if (code <= 67 || (code >= 80 && code <= 82)) return "cloud-rain";
            if (code <= 77 || code == 85 || code == 86) return "cloud-snow";
            if (code >= 95) return "cloud-lightning";
            return "cloud";
        }

        static bool TryNumber(string json, string key, out double value)
        {
            value = 0;
            int from = 0;
            string needle = "\"" + key + "\"";
            while (from < json.Length)
            {
                int i = json.IndexOf(needle, from, StringComparison.Ordinal);
                if (i < 0) return false;
                i = json.IndexOf(':', i);
                if (i < 0) return false;
                i++;
                while (i < json.Length && (json[i] == ' ' || json[i] == '[')) i++;
                int j = i;
                if (j < json.Length && (json[j] == '-' || json[j] == '+')) j++;
                while (j < json.Length && ((json[j] >= '0' && json[j] <= '9') || json[j] == '.')) j++;
                if (j > i && double.TryParse(json.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return true;
                from = i + 1;
            }
            return false;
        }

        static bool TryInt(string json, string key, out int value)
        {
            double d;
            if (!TryNumber(json, key, out d)) { value = 0; return false; }
            value = (int)Math.Round(d);
            return true;
        }
    }
}
