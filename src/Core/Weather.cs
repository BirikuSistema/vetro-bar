using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Launcher.Core
{
    /// <summary>Valladolid current weather via Open-Meteo. The dock only reads <see cref="Text"/> / <see cref="Icon"/>.</summary>
    internal static class Weather
    {
        public const string CachePath = "/opt/usr/home/owner/share/vetro-weather.json";
        const string Url = "https://api.open-meteo.com/v1/forecast?latitude=41.652&longitude=-4.728&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=Europe%2FMadrid";

        public static string Text;
        /// <summary>Today's low and high, for the small line under the reading. Null until both arrive.</summary>
        public static string Span;
        public static string Icon = "cloud";
        public static string LastError;
        public static DateTime UpdatedUtc;

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
                    string json = FetchCurl() ?? FetchHttp();
                    if (Apply(json))
                    {
                        try { File.WriteAllText(CachePath, json); } catch { /* ignore */ }
                    }
                }
                catch (Exception ex) { LastError = ex.Message; }
            });
        }

        static string FetchCurl()
        {
            try
            {
                var start = new ProcessStartInfo("/usr/bin/curl")
                {
                    Arguments = "-sS -m 6 -- " + Url,
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

        static string FetchHttp()
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(6);
                    return http.GetStringAsync(Url).GetAwaiter().GetResult();
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
                Span = "M\u00edn: " + ((int)Math.Round(lo)).ToString(CultureInfo.InvariantCulture) + "\u00b0   M\u00e1x: " + ((int)Math.Round(hi)).ToString(CultureInfo.InvariantCulture) + "\u00b0";
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
