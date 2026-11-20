using System;
using System.Collections.Generic;
using System.Globalization;

namespace Launcher.Core
{
    /// <summary>
    /// Everything that depends on the viewer's language and region: day and month names, the date line,
    /// the weather range label, 12 or 24 hour clock, the temperature unit, and UI strings.
    /// Pure code, so it is unit-tested on a desktop.
    /// </summary>
    internal static class Localization
    {
        private sealed class Lang
        {
            public string[] Days, ShortDays, Months, ShortMonths;
            public string Low, High;
            /// <summary>Tokens: {D} weekday, {d} day number, {M} month name.</summary>
            public string Pattern;
            /// <summary>Same, for regions that put the month first (US, Canada, Philippines).</summary>
            public string PatternMonthFirst;
            public string ShortPattern;
            public string ShortPatternMonthFirst;
            public bool CapitalizeDay = true;
        }

        private static readonly Dictionary<string, Lang> Table = new Dictionary<string, Lang>
        {
            { "en", new Lang {
                Days = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" },
                ShortDays = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" },
                Months = new[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" },
                ShortMonths = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" },
                Low = "Low", High = "High",
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D}, {M} {d}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D}, {M} {d}" } },
            { "es", new Lang {
                Days = new[] { "domingo", "lunes", "martes", "mi\u00e9rcoles", "jueves", "viernes", "s\u00e1bado" },
                ShortDays = new[] { "dom", "lun", "mar", "mi\u00e9", "jue", "vie", "s\u00e1b" },
                Months = new[] { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" },
                ShortMonths = new[] { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic" },
                Low = "M\u00edn", High = "M\u00e1x",
                Pattern = "{D} {d} de {M}", PatternMonthFirst = "{D} {d} de {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}" } },
            { "fr", new Lang {
                Days = new[] { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" },
                ShortDays = new[] { "dim", "lun", "mar", "mer", "jeu", "ven", "sam" },
                Months = new[] { "janvier", "f\u00e9vrier", "mars", "avril", "mai", "juin", "juillet", "ao\u00fbt", "septembre", "octobre", "novembre", "d\u00e9cembre" },
                ShortMonths = new[] { "janv", "f\u00e9vr", "mars", "avr", "mai", "juin", "juil", "ao\u00fbt", "sept", "oct", "nov", "d\u00e9c" },
                Low = "Min", High = "Max",
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}" } },
            { "de", new Lang {
                Days = new[] { "Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag" },
                ShortDays = new[] { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" },
                Months = new[] { "Januar", "Februar", "M\u00e4rz", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" },
                ShortMonths = new[] { "Jan", "Feb", "M\u00e4r", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez" },
                Low = "Min", High = "Max",
                Pattern = "{D}, {d}. {M}", PatternMonthFirst = "{D}, {d}. {M}",
                ShortPattern = "{D}, {d}. {M}", ShortPatternMonthFirst = "{D}, {d}. {M}" } },
            { "it", new Lang {
                Days = new[] { "domenica", "luned\u00ec", "marted\u00ec", "mercoled\u00ec", "gioved\u00ec", "venerd\u00ec", "sabato" },
                ShortDays = new[] { "dom", "lun", "mar", "mer", "gio", "ven", "sab" },
                Months = new[] { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" },
                ShortMonths = new[] { "gen", "feb", "mar", "apr", "mag", "giu", "lug", "ago", "set", "ott", "nov", "dic" },
                Low = "Min", High = "Max",
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}" } },
            { "pt", new Lang {
                Days = new[] { "domingo", "segunda-feira", "ter\u00e7a-feira", "quarta-feira", "quinta-feira", "sexta-feira", "s\u00e1bado" },
                ShortDays = new[] { "dom", "seg", "ter", "qua", "qui", "sex", "s\u00e1b" },
                Months = new[] { "janeiro", "fevereiro", "mar\u00e7o", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" },
                ShortMonths = new[] { "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez" },
                Low = "M\u00edn", High = "M\u00e1x",
                Pattern = "{D}, {d} de {M}", PatternMonthFirst = "{D}, {d} de {M}",
                ShortPattern = "{D}, {d} {M}", ShortPatternMonthFirst = "{D}, {d} {M}" } },
            { "nl", new Lang {
                Days = new[] { "zondag", "maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag" },
                ShortDays = new[] { "zo", "ma", "di", "wo", "do", "vr", "za" },
                Months = new[] { "januari", "februari", "maart", "april", "mei", "juni", "juli", "augustus", "september", "oktober", "november", "december" },
                ShortMonths = new[] { "jan", "feb", "mrt", "apr", "mei", "jun", "jul", "aug", "sep", "okt", "nov", "dec" },
                Low = "Min", High = "Max",
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}" } },
        };

        private static readonly Dictionary<string, string> UiEs = new Dictionary<string, string>
        {
            { "settings.title", "Ajustes del Launcher" },
            { "hint.main", "\u25b2\u25bc Navegar  \u00b7  \u25c0\u25b6 Cambiar  \u00b7  OK Seleccionar  \u00b7  Atr\u00e1s Cerrar" },
            { "hint.inputs", "\u25b2\u25bc Elegir entrada  \u00b7  OK Cambiar  \u00b7  Atr\u00e1s Volver" },
            { "hint.city", "Cruceta Teclado  \u00b7  OK Pulsar  \u00b7  Atr\u00e1s Volver" },
            { "hint.city_results", "\u25b2\u25bc Elegir ciudad  \u00b7  OK Guardar  \u00b7  Atr\u00e1s Teclado" },

            { "sec.shortcuts", "Accesos r\u00e1pidos" },
            { "sec.weather", "Meteorolog\u00eda" },
            { "sec.clock", "Reloj y Fecha" },
            { "sec.sound", "Sonido y Audio" },

            { "item.inputs", "Entradas HDMI" },
            { "item.apps", "Apps" },
            { "item.vetro", "Vetro" },
            { "item.tv_store", "TV-Store" },
            { "item.tv_settings", "Ajustes nativos" },
            { "item.show_weather", "Mostrar tiempo" },
            { "item.weather_mode", "Ubicaci\u00f3n" },
            { "item.city_search", "Buscar ciudad" },
            { "item.weather_show_location", "Mostrar nombre de ciudad" },
            { "item.weather_show_minmax", "Mostrar m\u00edn / m\u00e1x" },
            { "item.units", "Unidades" },
            { "item.show_clock", "Mostrar reloj" },
            { "item.clock_format", "Formato de hora" },
            { "item.show_date", "Mostrar fecha" },
            { "item.date_format", "Formato de fecha" },
            { "item.sound", "Sonidos de interfaz" },

            { "val.on", "Activado" },
            { "val.off", "Desactivado" },
            { "val.yes", "S\u00ed" },
            { "val.no", "No" },
            { "val.auto", "Auto" },
            { "val.auto_ip", "Auto (IP)" },
            { "val.manual", "Manual" },
            { "val.12h", "12 h" },
            { "val.24h", "24 h" },
            { "val.df_long", "Largo" },
            { "val.df_short", "Corto" },
            { "val.df_numeric", "Num\u00e9rico" },
            { "val.df_iso", "ISO" },
            { "val.muted", "Silenciado" },
            { "val.choose", "Elegir..." },
            { "val.active", "Activa" },

            { "sub.inputs.title", "Entradas HDMI" },
            { "sub.city.title", "Buscar ciudad (Open-Meteo)" },
            { "sub.city.prompt", "Escribe el nombre de tu ciudad y pulsa Buscar" },
            { "sub.city.empty", "Escribe al menos 2 letras para buscar" },
            { "sub.city.searching", "Buscando en Open-Meteo..." },
            { "sub.city.not_found", "No se encontr\u00f3 \"{0}\". Revisa el nombre e int\u00e9ntalo de nuevo." },
            { "sub.city.error", "Error de red al buscar ({0}). Revisa tu conexi\u00f3n e int\u00e9ntalo de nuevo." },
            { "sub.city.select", "Selecciona tu ciudad:" },
            { "kb.space", "ESPACIO" },
            { "kb.backspace", "BORRAR" },
            { "kb.clear", "LIMPIAR" },
            { "kb.search", "BUSCAR" },
        };

        private static readonly Dictionary<string, string> UiEn = new Dictionary<string, string>
        {
            { "settings.title", "Launcher Settings" },
            { "hint.main", "\u25b2\u25bc Navigate  \u00b7  \u25c0\u25b6 Change  \u00b7  OK Select  \u00b7  Back Close" },
            { "hint.inputs", "\u25b2\u25bc Choose input  \u00b7  OK Switch  \u00b7  Back Return" },
            { "hint.city", "D-Pad Keyboard  \u00b7  OK Press  \u00b7  Back Return" },
            { "hint.city_results", "\u25b2\u25bc Choose city  \u00b7  OK Save  \u00b7  Back Keyboard" },

            { "sec.shortcuts", "Shortcuts" },
            { "sec.weather", "Weather" },
            { "sec.clock", "Clock & Date" },
            { "sec.sound", "Sound & Audio" },

            { "item.inputs", "HDMI Inputs" },
            { "item.apps", "Apps" },
            { "item.vetro", "Vetro" },
            { "item.tv_store", "TV-Store" },
            { "item.tv_settings", "Native Settings" },
            { "item.show_weather", "Show weather" },
            { "item.weather_mode", "Location" },
            { "item.city_search", "Search city" },
            { "item.weather_show_location", "Show city name" },
            { "item.weather_show_minmax", "Show low / high" },
            { "item.units", "Units" },
            { "item.show_clock", "Show clock" },
            { "item.clock_format", "Time format" },
            { "item.show_date", "Show date" },
            { "item.date_format", "Date format" },
            { "item.sound", "UI sound effects" },

            { "val.on", "On" },
            { "val.off", "Off" },
            { "val.yes", "Yes" },
            { "val.no", "No" },
            { "val.auto", "Auto" },
            { "val.auto_ip", "Auto (IP)" },
            { "val.manual", "Manual" },
            { "val.12h", "12 h" },
            { "val.24h", "24 h" },
            { "val.df_long", "Long" },
            { "val.df_short", "Short" },
            { "val.df_numeric", "Numeric" },
            { "val.df_iso", "ISO" },
            { "val.muted", "Muted" },
            { "val.choose", "Choose..." },
            { "val.active", "Active" },

            { "sub.inputs.title", "HDMI Inputs" },
            { "sub.city.title", "Search city (Open-Meteo)" },
            { "sub.city.prompt", "Type your city name and press Search" },
            { "sub.city.empty", "Enter at least 2 characters to search" },
            { "sub.city.searching", "Searching Open-Meteo..." },
            { "sub.city.not_found", "Could not find \"{0}\". Please check the spelling and try again." },
            { "sub.city.error", "Network error while searching ({0}). Check your connection and try again." },
            { "sub.city.select", "Select your city:" },
            { "kb.space", "SPACE" },
            { "kb.backspace", "DEL" },
            { "kb.clear", "CLEAR" },
            { "kb.search", "SEARCH" },
        };

        private static readonly string[] MonthFirstCountries = { "US", "CA", "PH" };
        private static readonly string[] Clock12Countries = { "US", "CA", "AU", "NZ", "PH" };
        // Countries that use Fahrenheit for the weather.
        private static readonly string[] FahrenheitCountries = { "US", "BS", "BZ", "KY", "LR", "PW", "FM", "MH" };

        private static Lang _lang = Table["en"];
        private static string _language = "en";
        private static string _country;
        private static int _clockOverride;
        private static char _unitsOverride;

        public static string Language { get { return _language; } }
        public static string Country { get { return _country; } }
        public static bool ShowClock { get; private set; } = true;
        public static bool ShowDate { get; private set; } = true;
        public static string DateFormat { get; private set; } = "long";

        public static string Ui(string key)
        {
            if (key == null) return "";
            var dict = _language == "es" ? UiEs : UiEn;
            string value;
            if (dict.TryGetValue(key, out value)) return value;
            if (UiEn.TryGetValue(key, out value)) return value;
            return key;
        }

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
            ShowClock = overrides != null ? overrides.EffectiveShowClock : true;
            ShowDate = overrides != null ? overrides.EffectiveShowDate : true;
            DateFormat = overrides != null ? overrides.EffectiveDateFormat : "long";
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
            if (!ShowDate) return "";
            bool monthFirst = _language == "en" && _country != null && Array.IndexOf(MonthFirstCountries, _country) >= 0;
            switch (DateFormat)
            {
                case "iso":
                    return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "numeric":
                    return t.ToString(monthFirst ? "MM/dd/yyyy" : "dd/MM/yyyy", CultureInfo.InvariantCulture);
                case "short":
                    string sDay = _lang.ShortDays[(int)t.DayOfWeek];
                    if (_lang.CapitalizeDay && sDay.Length > 0) sDay = char.ToUpperInvariant(sDay[0]) + sDay.Substring(1);
                    string sMonth = _lang.ShortMonths[t.Month - 1];
                    if (_lang.CapitalizeDay && sMonth.Length > 0) sMonth = char.ToUpperInvariant(sMonth[0]) + sMonth.Substring(1);
                    string sPattern = monthFirst ? _lang.ShortPatternMonthFirst : _lang.ShortPattern;
                    return sPattern
                        .Replace("{D}", sDay)
                        .Replace("{d}", t.Day.ToString(CultureInfo.InvariantCulture))
                        .Replace("{M}", sMonth);
                default:
                    string pattern = monthFirst ? _lang.PatternMonthFirst : _lang.Pattern;
                    string day = _lang.Days[(int)t.DayOfWeek];
                    if (_lang.CapitalizeDay && day.Length > 0) day = char.ToUpperInvariant(day[0]) + day.Substring(1);
                    string month = _lang.Months[t.Month - 1];
                    if (_lang.CapitalizeDay && month.Length > 0) month = char.ToUpperInvariant(month[0]) + month.Substring(1);
                    return pattern
                        .Replace("{D}", day)
                        .Replace("{d}", t.Day.ToString(CultureInfo.InvariantCulture))
                        .Replace("{M}", month);
            }
        }

        public static string RangeText(int low, int high)
        {
            return _lang.Low + ": " + low.ToString(CultureInfo.InvariantCulture) + "\u00b0   " + _lang.High + ": " + high.ToString(CultureInfo.InvariantCulture) + "\u00b0";
        }

        public static string CompactRangeText(int low, int high)
        {
            return low.ToString(CultureInfo.InvariantCulture) + "\u00b0 / " + high.ToString(CultureInfo.InvariantCulture) + "\u00b0";
        }
    }
}
