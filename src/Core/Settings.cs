using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Launcher.Core
{
    /// <summary>
    /// User-tunable settings, read from (in this order of priority) the command line, the environment and
    /// <c>vetro-bar.conf</c> next to the dock's data. Everything is optional: with nothing set the dock
    /// follows the TV (menu language, region, time zone) and finds the weather location from the IP address.
    /// </summary>
    internal sealed class Settings
    {
        /// <summary>Language code ("es", "en", ...). Null: the TV's menu language.</summary>
        public string Language;
        /// <summary>Weather location. False: found from the IP address in auto mode.</summary>
        public bool HasLocation;
        public double Latitude, Longitude;
        /// <summary>Friendly city/place label for manual location (optional).</summary>
        public string LocationName;
        /// <summary>12 or 24. 0: by country.</summary>
        public int Clock;
        /// <summary>'c', 'f' or '\0' (by country).</summary>
        public char Units;

        public bool? ShowWeather;
        /// <summary>"auto" or "manual". Null defaults to "manual" when <see cref="HasLocation"/> is true, else "auto".</summary>
        public string WeatherMode;
        public bool? WeatherShowLocation;
        public bool? WeatherShowMinMax;
        public bool? ShowClock;
        public bool? ShowDate;
        /// <summary>"long", "short", "numeric", or "iso".</summary>
        public string DateFormat;
        public bool? SoundEnabled;
        /// <summary>Optional explicit app id for TV-Store.</summary>
        public string TvStoreApp;

        private bool _languageSet;
        private bool _clockSet;
        private bool _unitsSet;

        public bool EffectiveShowWeather { get { return ShowWeather ?? true; } }
        public string EffectiveWeatherMode { get { return WeatherMode ?? (HasLocation ? "manual" : "auto"); } }
        public bool EffectiveWeatherShowLocation { get { return WeatherShowLocation ?? false; } }
        public bool EffectiveWeatherShowMinMax { get { return WeatherShowMinMax ?? true; } }
        public bool EffectiveShowClock { get { return ShowClock ?? true; } }
        public bool EffectiveShowDate { get { return ShowDate ?? true; } }
        public string EffectiveDateFormat { get { return DateFormat ?? "long"; } }
        public bool EffectiveSoundEnabled { get { return SoundEnabled ?? true; } }

        /// <summary>Reads simple <c>key=value</c> lines; '#' starts a comment; unknown keys and bad values are ignored.</summary>
        public static Settings Parse(string text)
        {
            var s = new Settings();
            if (string.IsNullOrEmpty(text)) return s;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                s.Set(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1).Trim());
            }
            return s;
        }

        private static bool TryBool(string value, out bool result)
        {
            switch (value.ToLowerInvariant())
            {
                case "true":
                case "1":
                case "on":
                case "yes":
                case "si":
                case "sí":
                    result = true;
                    return true;
                case "false":
                case "0":
                case "off":
                case "no":
                    result = false;
                    return true;
                default:
                    result = false;
                    return false;
            }
        }

        private static string SanitizeText(string value, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var sb = new StringBuilder(Math.Min(value.Length, maxLen));
            foreach (char c in value.Trim())
            {
                if (c < ' ' || c == '#') continue;
                sb.Append(c);
                if (sb.Length >= maxLen) break;
            }
            string s = sb.ToString().Trim();
            return s.Length > 0 ? s : null;
        }

        /// <summary>Applies one setting; returns false when the key or the value is not understood.</summary>
        public bool Set(string key, string value)
        {
            if (key == null || value == null) return false;
            key = key.Trim().ToLowerInvariant();
            value = value.Trim();
            bool b;
            switch (key)
            {
                case "language":
                case "lang":
                    if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        Language = null;
                        _languageSet = true;
                        return true;
                    }
                    string lang = Localization.LanguageOf(value);
                    if (lang == null) return false;
                    Language = lang;
                    _languageSet = true;
                    return true;
                case "location":
                    string[] p = value.Split(',');
                    double la, lo;
                    if (p.Length != 2
                        || !double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out la)
                        || !double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lo)
                        || double.IsNaN(la) || double.IsInfinity(la)
                        || double.IsNaN(lo) || double.IsInfinity(lo)
                        || la < -90 || la > 90 || lo < -180 || lo > 180) return false;
                    Latitude = la; Longitude = lo; HasLocation = true;
                    return true;
                case "location_name":
                    LocationName = SanitizeText(value, 80);
                    return true;
                case "show_weather":
                    if (!TryBool(value, out b)) return false;
                    ShowWeather = b;
                    return true;
                case "weather_mode":
                    string wm = value.ToLowerInvariant();
                    if (wm != "auto" && wm != "manual") return false;
                    WeatherMode = wm;
                    return true;
                case "weather_show_location":
                    if (!TryBool(value, out b)) return false;
                    WeatherShowLocation = b;
                    return true;
                case "weather_show_minmax":
                    if (!TryBool(value, out b)) return false;
                    WeatherShowMinMax = b;
                    return true;
                case "show_clock":
                    if (!TryBool(value, out b)) return false;
                    ShowClock = b;
                    return true;
                case "clock":
                    if (value.Equals("auto", StringComparison.OrdinalIgnoreCase) || value == "0")
                    {
                        Clock = 0; _clockSet = true; return true;
                    }
                    if (value == "12") { Clock = 12; _clockSet = true; return true; }
                    if (value == "24") { Clock = 24; _clockSet = true; return true; }
                    return false;
                case "show_date":
                    if (!TryBool(value, out b)) return false;
                    ShowDate = b;
                    return true;
                case "date_format":
                    string df = value.ToLowerInvariant();
                    if (df != "long" && df != "short" && df != "numeric" && df != "iso") return false;
                    DateFormat = df;
                    return true;
                case "units":
                    string u = value.ToLowerInvariant();
                    if (u == "auto") { Units = '\0'; _unitsSet = true; return true; }
                    if (u == "c" || u == "celsius") { Units = 'c'; _unitsSet = true; return true; }
                    if (u == "f" || u == "fahrenheit") { Units = 'f'; _unitsSet = true; return true; }
                    return false;
                case "sound":
                    if (!TryBool(value, out b)) return false;
                    SoundEnabled = b;
                    return true;
                case "tv_store_app":
                    if (!Catalog.ValidAppId(value)) return false;
                    TvStoreApp = value;
                    return true;
            }
            return false;
        }

        /// <summary>Fills what is still unset from <paramref name="lower"/> (the lower-priority source).</summary>
        public void Fill(Settings lower)
        {
            if (lower == null) return;
            if (!_languageSet && Language == null && lower.Language != null) Language = lower.Language;
            if (!HasLocation && lower.HasLocation) { HasLocation = true; Latitude = lower.Latitude; Longitude = lower.Longitude; }
            if (LocationName == null && lower.LocationName != null) LocationName = lower.LocationName;
            if (!_clockSet && Clock == 0 && (lower.Clock != 0 || lower._clockSet)) Clock = lower.Clock;
            if (!_unitsSet && Units == '\0' && (lower.Units != '\0' || lower._unitsSet)) Units = lower.Units;
            if (!ShowWeather.HasValue) ShowWeather = lower.ShowWeather;
            if (WeatherMode == null) WeatherMode = lower.WeatherMode;
            if (!WeatherShowLocation.HasValue) WeatherShowLocation = lower.WeatherShowLocation;
            if (!WeatherShowMinMax.HasValue) WeatherShowMinMax = lower.WeatherShowMinMax;
            if (!ShowClock.HasValue) ShowClock = lower.ShowClock;
            if (!ShowDate.HasValue) ShowDate = lower.ShowDate;
            if (DateFormat == null) DateFormat = lower.DateFormat;
            if (!SoundEnabled.HasValue) SoundEnabled = lower.SoundEnabled;
            if (TvStoreApp == null) TvStoreApp = lower.TvStoreApp;
        }

        public Settings Clone()
        {
            return new Settings
            {
                Language = Language,
                HasLocation = HasLocation,
                Latitude = Latitude,
                Longitude = Longitude,
                LocationName = LocationName,
                Clock = Clock,
                Units = Units,
                ShowWeather = ShowWeather,
                WeatherMode = WeatherMode,
                WeatherShowLocation = WeatherShowLocation,
                WeatherShowMinMax = WeatherShowMinMax,
                ShowClock = ShowClock,
                ShowDate = ShowDate,
                DateFormat = DateFormat,
                SoundEnabled = SoundEnabled,
                TvStoreApp = TvStoreApp,
                _languageSet = _languageSet,
                _clockSet = _clockSet,
                _unitsSet = _unitsSet,
            };
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# vetro-bar settings\n");
            if (Language != null) sb.Append("language=").Append(Language).Append('\n');
            if (ShowWeather.HasValue) sb.Append("show_weather=").Append(ShowWeather.Value ? "true" : "false").Append('\n');
            if (WeatherMode != null) sb.Append("weather_mode=").Append(WeatherMode).Append('\n');
            if (HasLocation)
                sb.Append("location=")
                  .Append(Latitude.ToString("G", CultureInfo.InvariantCulture))
                  .Append(',')
                  .Append(Longitude.ToString("G", CultureInfo.InvariantCulture))
                  .Append('\n');
            if (!string.IsNullOrEmpty(LocationName)) sb.Append("location_name=").Append(LocationName).Append('\n');
            if (WeatherShowLocation.HasValue) sb.Append("weather_show_location=").Append(WeatherShowLocation.Value ? "true" : "false").Append('\n');
            if (WeatherShowMinMax.HasValue) sb.Append("weather_show_minmax=").Append(WeatherShowMinMax.Value ? "true" : "false").Append('\n');
            if (Units != '\0') sb.Append("units=").Append(Units).Append('\n');
            if (ShowClock.HasValue) sb.Append("show_clock=").Append(ShowClock.Value ? "true" : "false").Append('\n');
            if (Clock != 0) sb.Append("clock=").Append(Clock.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (ShowDate.HasValue) sb.Append("show_date=").Append(ShowDate.Value ? "true" : "false").Append('\n');
            if (DateFormat != null) sb.Append("date_format=").Append(DateFormat).Append('\n');
            if (SoundEnabled.HasValue) sb.Append("sound=").Append(SoundEnabled.Value ? "true" : "false").Append('\n');
            if (!string.IsNullOrEmpty(TvStoreApp)) sb.Append("tv_store_app=").Append(TvStoreApp).Append('\n');
            return sb.ToString();
        }

        public bool SaveAtomic(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string tmp = path + ".tmp";
            string text = Serialize();
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, true);
                return true;
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                try
                {
                    File.WriteAllText(path, text);
                    return true;
                }
                catch (Exception) { return false; }
            }
        }
    }
}
