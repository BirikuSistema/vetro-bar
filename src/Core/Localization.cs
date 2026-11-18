using System;
using System.Collections.Generic;
using System.Globalization;

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
        /// <summary>Weather location. Null: found from the IP address.</summary>
        public bool HasLocation;
        public double Latitude, Longitude;
        /// <summary>12 or 24. 0: by country.</summary>
        public int Clock;
        /// <summary>'c', 'f' or '\0' (by country).</summary>
        public char Units;

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

        /// <summary>Applies one setting; returns false when the key or the value is not understood.</summary>
        public bool Set(string key, string value)
        {
            if (value == null) return false;
            value = value.Trim();
            switch (key)
            {
                case "language":
                case "lang":
                    string lang = Localization.LanguageOf(value);
                    if (lang == null) return false;
                    Language = lang;
                    return true;
                case "location":
                    string[] p = value.Split(',');
                    double la, lo;
                    if (p.Length != 2
                        || !double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out la)
                        || !double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lo)
                        || la < -90 || la > 90 || lo < -180 || lo > 180) return false;
                    Latitude = la; Longitude = lo; HasLocation = true;
                    return true;
                case "clock":
                    if (value == "12") { Clock = 12; return true; }
                    if (value == "24") { Clock = 24; return true; }
                    return false;
                case "units":
                    string u = value.ToLowerInvariant();
                    if (u == "c" || u == "celsius") { Units = 'c'; return true; }
                    if (u == "f" || u == "fahrenheit") { Units = 'f'; return true; }
                    return false;
            }
            return false;
        }

        /// <summary>Fills what is still unset from <paramref name="lower"/> (the lower-priority source).</summary>
        public void Fill(Settings lower)
        {
            if (Language == null) Language = lower.Language;
            if (!HasLocation && lower.HasLocation) { HasLocation = true; Latitude = lower.Latitude; Longitude = lower.Longitude; }
            if (Clock == 0) Clock = lower.Clock;
            if (Units == '\0') Units = lower.Units;
        }
    }

    /// <summary>
    /// Everything that depends on the viewer's language and region: day and month names, the date line,
    /// the weather range label, 12 or 24 hour clock and the temperature unit. Pure code, so it is unit-tested on a desktop.
    /// </summary>
    internal static class Localization
    {
        private sealed class Lang
        {
            public string[] Days, Months;
            public string Low, High;
            /// <summary>Tokens: {D} weekday, {d} day number, {M} month name.</summary>
            public string Pattern;
            /// <summary>Same, for regions that put the month first (US, Canada, Philippines).</summary>
            public string PatternMonthFirst;
            public bool CapitalizeDay = true;
        }

        private static readonly Dictionary<string, Lang> Table = new Dictionary<string, Lang>
        {
            { "en", new Lang {
                Days = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" },
                Months = new[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" },
                Low = "Low", High = "High", Pattern = "{D} {d} {M}", PatternMonthFirst = "{D}, {M} {d}" } },
            { "es", new Lang {
                Days = new[] { "domingo", "lunes", "martes", "mi\u00e9rcoles", "jueves", "viernes", "s\u00e1bado" },
                Months = new[] { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" },
                Low = "M\u00edn", High = "M\u00e1x", Pattern = "{D} {d} de {M}", PatternMonthFirst = "{D} {d} de {M}" } },
            { "fr", new Lang {
                Days = new[] { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" },
                Months = new[] { "janvier", "f\u00e9vrier", "mars", "avril", "mai", "juin", "juillet", "ao\u00fbt", "septembre", "octobre", "novembre", "d\u00e9cembre" },
                Low = "Min", High = "Max", Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}" } },
            { "de", new Lang {
                Days = new[] { "Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag" },
                Months = new[] { "Januar", "Februar", "M\u00e4rz", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" },
                Low = "Min", High = "Max", Pattern = "{D}, {d}. {M}", PatternMonthFirst = "{D}, {d}. {M}" } },
            { "it", new Lang {
                Days = new[] { "domenica", "luned\u00ec", "marted\u00ec", "mercoled\u00ec", "gioved\u00ec", "venerd\u00ec", "sabato" },
                Months = new[] { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" },
                Low = "Min", High = "Max", Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}" } },
            { "pt", new Lang {
                Days = new[] { "domingo", "segunda-feira", "ter\u00e7a-feira", "quarta-feira", "quinta-feira", "sexta-feira", "s\u00e1bado" },
                Months = new[] { "janeiro", "fevereiro", "mar\u00e7o", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" },
                Low = "M\u00edn", High = "M\u00e1x", Pattern = "{D}, {d} de {M}", PatternMonthFirst = "{D}, {d} de {M}" } },
            { "nl", new Lang {
                Days = new[] { "zondag", "maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag" },
                Months = new[] { "januari", "februari", "maart", "april", "mei", "juni", "juli", "augustus", "september", "oktober", "november", "december" },
                Low = "Min", High = "Max", Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}" } },
        };

        private static string[] MonthFirstCountries = { "US", "CA", "PH" };
        private static string[] Clock12Countries = { "US", "CA", "AU", "NZ", "PH" };
        // Countries that use Fahrenheit for the weather.
        private static string[] FahrenheitCountries = { "US", "BS", "BZ", "KY", "LR", "PW", "FM", "MH" };

        private static Lang _lang = Table["en"];
        private static string _language = "en";
        private static string _country;
        private static int _clockOverride;
        private static char _unitsOverride;

        public static string Language { get { return _language; } }
        public static string Country { get { return _country; } }

        /// <summary>"es_ES.utf8", "pt-BR", "fr" and the like become "es", "pt", "fr"; null for "C", "POSIX" or junk.</summary>
        public static string LanguageOf(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return null;
            string s = locale.Trim().ToLowerInvariant();
            int cut = s.IndexOfAny(new[] { '_', '-', '.', '@' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (s.Length < 2 || s.Length > 3 || s == "c" || s == "posix") return null;
            foreach (char c in s) if (c < 'a' || c > 'z') return null;
            return s;
        }

        /// <summary>Chooses language and formats. Unknown languages fall back to English.</summary>
        public static void Configure(string language, string country, Settings overrides)
        {
            string l = LanguageOf(language) ?? "en";
            if (overrides != null && overrides.Language != null) l = overrides.Language;
            Lang lang;
            if (!Table.TryGetValue(l, out lang)) { lang = Table["en"]; l = "en"; }
            _lang = lang;
            _language = l;
            _country = Normalise(country);
            _clockOverride = overrides != null ? overrides.Clock : 0;
            _unitsOverride = overrides != null ? overrides.Units : '\0';
        }

        /// <summary>Country is learned late (from the IP address); formats follow it unless the user chose them.</summary>
        public static void SetCountry(string country)
        {
            string c = Normalise(country);
            if (c != null) _country = c;
        }

        private static string Normalise(string country)
        {
            if (string.IsNullOrWhiteSpace(country)) return null;
            string c = country.Trim().ToUpperInvariant();
            return c.Length == 2 ? c : null;
        }

        public static bool Is12Hour
        {
            get
            {
                if (_clockOverride != 0) return _clockOverride == 12;
                return _country != null && Array.IndexOf(Clock12Countries, _country) >= 0;
            }
        }

        public static bool Fahrenheit
        {
            get
            {
                if (_unitsOverride != '\0') return _unitsOverride == 'f';
                return _country != null && Array.IndexOf(FahrenheitCountries, _country) >= 0;
            }
        }

        public static string ClockText(DateTime t)
        {
            return Is12Hour ? t.ToString("h:mm tt", CultureInfo.InvariantCulture) : t.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        public static string DayText(DateTime t)
        {
            bool monthFirst = _language == "en" && _country != null && Array.IndexOf(MonthFirstCountries, _country) >= 0;
            string pattern = monthFirst ? _lang.PatternMonthFirst : _lang.Pattern;
            string day = _lang.Days[(int)t.DayOfWeek];
            if (_lang.CapitalizeDay) day = char.ToUpperInvariant(day[0]) + day.Substring(1);
            return pattern
                .Replace("{D}", day)
                .Replace("{d}", t.Day.ToString(CultureInfo.InvariantCulture))
                .Replace("{M}", _lang.Months[t.Month - 1]);
        }

        public static string RangeText(int low, int high)
        {
            return _lang.Low + ": " + low.ToString(CultureInfo.InvariantCulture) + "\u00b0   " + _lang.High + ": " + high.ToString(CultureInfo.InvariantCulture) + "\u00b0";
        }
    }
}
