using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Launcher.Core
{
    /// <summary>
    /// Current weather via Open-Meteo for wherever the TV is. The place comes from, in order: a location the user
    /// set, the last one found, or a lookup of the public IP address (about city accuracy, refreshed daily).
    /// The dock only reads <see cref="Text"/> / <see cref="Icon"/>.
    /// </summary>
    internal static class Weather
    {
        const string DefaultDir = "/opt/usr/home/owner/share";
        const double LocationMaxAgeHours = 24;
        const string Forecast = "https://api.open-meteo.com/v1/forecast?current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto";
        // Both answer with the same field names, so one parser serves them.
        static readonly string[] GeoServices = { "https://ipwho.is/", "https://ipapi.co/json/" };

        /// <summary>Where the weather and location caches live; <see cref="Init"/> moves it when the default is missing.</summary>
        public static string CacheDir = DefaultDir;
        static string CachePath { get { return Path.Combine(CacheDir, "vetro-weather.json"); } }
        static string LocationPath { get { return Path.Combine(CacheDir, "vetro-location.txt"); } }

        static bool _manual;
        static double _manualLat, _manualLon;

        public static string Text;
        /// <summary>Today's low and high, for the small line under the reading. Null until both arrive.</summary>
        public static string Span;
        public static string Icon = "cloud";
        public static string LastError;
        public static DateTime UpdatedUtc;

        public static void Init(string fallbackDir)
        {
            if (!Directory.Exists(CacheDir)) CacheDir = fallbackDir;
        }

        /// <summary>Fixes the place; no IP lookup is made.</summary>
        public static void UseLocation(double latitude, double longitude)
        {
            _manual = true; _manualLat = latitude; _manualLon = longitude;
        }

        public static void LoadCache()
        {
            try
            {
                if (File.Exists(CachePath)) Apply(File.ReadAllText(CachePath));
            }
            catch { /* keep previous */ }
        }

        public static Task Refresh()
        {
            return Task.Run(() =>
            {
                try
                {
                    string url = ForecastUrl();
                    if (url == null) { LastError = "no location"; return; }
                    string json = Fetch(url);
                    if (Apply(json))
                    {
                        try { File.WriteAllText(CachePath, json); } catch { /* ignore */ }
                    }
                }
                catch (Exception ex) { LastError = ex.Message; }
            });
        }

        internal static string BuildUrl(double lat, double lon, bool fahrenheit)
        {
            return Forecast + "&latitude=" + lat.ToString("0.###", CultureInfo.InvariantCulture)
                + "&longitude=" + lon.ToString("0.###", CultureInfo.InvariantCulture)
                + (fahrenheit ? "&temperature_unit=fahrenheit" : "");
        }

        static string ForecastUrl()
        {
            double lat, lon;
            if (_manual) { lat = _manualLat; lon = _manualLon; }
            else if (!ResolveLocation(out lat, out lon)) return null;
            return BuildUrl(lat, lon, Localization.Fahrenheit);
        }

        static bool ResolveLocation(out double lat, out double lon)
        {
            double cLat = 0, cLon = 0; string cCountry = null; DateTime cTime = DateTime.MinValue;
            bool have = ReadLocation(out cLat, out cLon, out cCountry, out cTime);
            if (have && cCountry != null) Localization.SetCountry(cCountry);
            if (have && (DateTime.UtcNow - cTime).TotalHours < LocationMaxAgeHours) { lat = cLat; lon = cLon; return true; }
            foreach (string service in GeoServices)
            {
                string body = Fetch(service);
                double la, lo;
                if (!ParseGeo(body, out la, out lo)) continue;
                string country = TryString(body, "country_code");
                if (country != null) Localization.SetCountry(country);
                try { File.WriteAllText(LocationPath, la.ToString("R", CultureInfo.InvariantCulture) + " " + lo.ToString("R", CultureInfo.InvariantCulture) + " " + (country ?? "-") + " " + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)); }
                catch { /* ignore */ }
                lat = la; lon = lo;
                return true;
            }
            lat = cLat; lon = cLon;
            return have;   // offline: the last known place is better than none
        }

        static bool ReadLocation(out double lat, out double lon, out string country, out DateTime stamp)
        {
            lat = lon = 0; country = null; stamp = DateTime.MinValue;
            try
            {
                if (!File.Exists(LocationPath)) return false;
                string[] p = File.ReadAllText(LocationPath).Trim().Split(' ');
                long unix;
                if (p.Length < 4
                    || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                    || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
                    || !long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out unix)) return false;
                country = p[2] == "-" ? null : p[2];
                stamp = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
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

        internal static string TryString(string json, string key)
        {
            if (json == null) return null;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            int a = json.IndexOf('"', i + 1);
            if (a < 0) return null;
            int b = json.IndexOf('"', a + 1);
            return b > a ? json.Substring(a + 1, b - a - 1) : null;
        }

        static string Fetch(string url) { return FetchCurl(url) ?? FetchHttp(url); }

        static string FetchCurl(string url)
        {
            try
            {
                var start = new ProcessStartInfo("/usr/bin/curl")
                {
                    Arguments = "-sS -m 6 -- " + url,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using (Process p = Process.Start(start))
                {
                    if (p == null) return null;
                    string json = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(7000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0 || string.IsNullOrEmpty(json)) return null;
                    return json;
                }
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        static string FetchHttp(string url)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(6);
                    return http.GetStringAsync(url).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        internal static bool Apply(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            double temp;
            int code;
            if (!TryNumber(json, "temperature_2m", out temp)) return false;
            if (!TryInt(json, "weather_code", out code)) code = 1;
            int rounded = (int)Math.Round(temp);
            Text = rounded.ToString(CultureInfo.InvariantCulture) + "\u00b0";
            Icon = IconFor(code);
            double lo, hi;
            if (TryNumber(json, "temperature_2m_min", out lo) && TryNumber(json, "temperature_2m_max", out hi))
                Span = Localization.RangeText((int)Math.Round(lo), (int)Math.Round(hi));
            else Span = null;
            UpdatedUtc = DateTime.UtcNow;
            LastError = null;
            return true;
        }

        static string IconFor(int code)
        {
            if (code == 0) return "sun";
            if (code <= 2) return "cloud-sun";
            if (code <= 48) return "cloud";
            if (code <= 67 || (code >= 80 && code <= 82)) return "rain";
            if (code <= 77 || code == 85 || code == 86) return "snow";
            if (code >= 95) return "rain";
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
