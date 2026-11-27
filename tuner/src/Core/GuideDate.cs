using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Tuner.Core
{
    /// <summary>
    /// Formats the TV Guide header date using the exact same locale and format rules as vetro-bar's
    /// clock pill (Launcher.Core.Localization.DayText), reading the TV's language/country and vetro-bar.conf.
    /// </summary>
    internal static class GuideDate
    {
        private sealed class Lang
        {
            public string[] Days, ShortDays, Months, ShortMonths;
            public string Pattern, PatternMonthFirst, ShortPattern, ShortPatternMonthFirst;
            public string Loading, NoInfo;
        }

        private static readonly Dictionary<string, Lang> Table = new Dictionary<string, Lang>
        {
            { "en", new Lang {
                Days = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" },
                ShortDays = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" },
                Months = new[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" },
                ShortMonths = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" },
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D}, {M} {d}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D}, {M} {d}",
                Loading = "Loading\u2026", NoInfo = "No information" } },
            { "es", new Lang {
                Days = new[] { "domingo", "lunes", "martes", "mi\u00e9rcoles", "jueves", "viernes", "s\u00e1bado" },
                ShortDays = new[] { "dom", "lun", "mar", "mi\u00e9", "jue", "vie", "s\u00e1b" },
                Months = new[] { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" },
                ShortMonths = new[] { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic" },
                Pattern = "{D} {d} de {M}", PatternMonthFirst = "{D} {d} de {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}",
                Loading = "Cargando\u2026", NoInfo = "Sin informaci\u00f3n" } },
            { "fr", new Lang {
                Days = new[] { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" },
                ShortDays = new[] { "dim", "lun", "mar", "mer", "jeu", "ven", "sam" },
                Months = new[] { "janvier", "f\u00e9vrier", "mars", "avril", "mai", "juin", "juillet", "ao\u00fbt", "septembre", "octobre", "novembre", "d\u00e9cembre" },
                ShortMonths = new[] { "janv", "f\u00e9vr", "mars", "avr", "mai", "juin", "juil", "ao\u00fbt", "sept", "oct", "nov", "d\u00e9c" },
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}",
                Loading = "Chargement\u2026", NoInfo = "Aucune information" } },
            { "de", new Lang {
                Days = new[] { "Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag" },
                ShortDays = new[] { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" },
                Months = new[] { "Januar", "Februar", "M\u00e4rz", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" },
                ShortMonths = new[] { "Jan", "Feb", "M\u00e4r", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez" },
                Pattern = "{D}, {d}. {M}", PatternMonthFirst = "{D}, {d}. {M}",
                ShortPattern = "{D}, {d}. {M}", ShortPatternMonthFirst = "{D}, {d}. {M}",
                Loading = "Laden\u2026", NoInfo = "Keine Information" } },
            { "it", new Lang {
                Days = new[] { "domenica", "luned\u00ec", "marted\u00ec", "mercoled\u00ec", "gioved\u00ec", "venerd\u00ec", "sabato" },
                ShortDays = new[] { "dom", "lun", "mar", "mer", "gio", "ven", "sab" },
                Months = new[] { "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre" },
                ShortMonths = new[] { "gen", "feb", "mar", "apr", "mag", "giu", "lug", "ago", "set", "ott", "nov", "dic" },
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}",
                Loading = "Caricamento\u2026", NoInfo = "Nessuna informazione" } },
            { "pt", new Lang {
                Days = new[] { "domingo", "segunda-feira", "ter\u00e7a-feira", "quarta-feira", "quinta-feira", "sexta-feira", "s\u00e1bado" },
                ShortDays = new[] { "dom", "seg", "ter", "qua", "qui", "sex", "s\u00e1b" },
                Months = new[] { "janeiro", "fevereiro", "mar\u00e7o", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" },
                ShortMonths = new[] { "jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez" },
                Pattern = "{D}, {d} de {M}", PatternMonthFirst = "{D}, {d} de {M}",
                ShortPattern = "{D}, {d} {M}", ShortPatternMonthFirst = "{D}, {d} {M}",
                Loading = "Carregando\u2026", NoInfo = "Sem informa\u00e7\u00e3o" } },
            { "nl", new Lang {
                Days = new[] { "zondag", "maandag", "dinsdag", "woensdag", "donderdag", "vrijdag", "zaterdag" },
                ShortDays = new[] { "zo", "ma", "di", "wo", "do", "vr", "za" },
                Months = new[] { "januari", "februari", "maart", "april", "mei", "juni", "juli", "augustus", "september", "oktober", "november", "december" },
                ShortMonths = new[] { "jan", "feb", "mrt", "apr", "mei", "jun", "jul", "aug", "sep", "okt", "nov", "dec" },
                Pattern = "{D} {d} {M}", PatternMonthFirst = "{D} {d} {M}",
                ShortPattern = "{D} {d} {M}", ShortPatternMonthFirst = "{D} {d} {M}",
                Loading = "Laden\u2026", NoInfo = "Geen informatie" } },
        };

        private static readonly string[] MonthFirstCountries = { "US", "CA", "PH" };
        private static Lang _lang = Table["en"];
        private static string _language = "en";
        private static string _country;
        private static string _dateFormat = "long";

        public static string LoadingText { get { return _lang.Loading; } }
        public static string NoInfoText { get { return _lang.NoInfo; } }

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

        public static void Configure(string language, string country, string dateFormat)
        {
            string l = LanguageOf(language) ?? "en";
            Lang lang;
            if (!Table.TryGetValue(l, out lang)) { lang = Table["en"]; l = "en"; }
            _lang = lang;
            _language = l;
            string c = string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
            _country = c != null && c.Length == 2 ? c : null;
            string df = (dateFormat ?? "long").Trim().ToLowerInvariant();
            _dateFormat = (df == "long" || df == "short" || df == "numeric" || df == "iso") ? df : "long";
        }

        public static void ParseConf(string text, out string language, out string dateFormat)
        {
            language = null;
            dateFormat = null;
            if (string.IsNullOrEmpty(text)) return;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (k == "language" || k == "lang")
                {
                    if (!v.Equals("auto", StringComparison.OrdinalIgnoreCase))
                        language = LanguageOf(v) ?? language;
                }
                else if (k == "date_format")
                {
                    string df = v.ToLowerInvariant();
                    if (df == "long" || df == "short" || df == "numeric" || df == "iso") dateFormat = df;
                }
            }
        }

        public static void LoadFromConf(string tvLanguage, string tvCountry, params string[] confPaths)
        {
            string confLang = null, confFmt = null;
            if (confPaths != null)
            {
                foreach (string p in confPaths)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        {
                            ParseConf(File.ReadAllText(p), out confLang, out confFmt);
                            break;
                        }
                    }
                    catch (Exception) { }
                }
            }
            Configure(confLang ?? tvLanguage, tvCountry, confFmt ?? "long");
        }

        public static string Format(long unix, TimeSpan offset)
        {
            DateTime t = DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(offset).DateTime;
            bool monthFirst = _language == "en" && _country != null && Array.IndexOf(MonthFirstCountries, _country) >= 0;
            switch (_dateFormat)
            {
                case "iso":
                    return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case "numeric":
                    return t.ToString(monthFirst ? "MM/dd/yyyy" : "dd/MM/yyyy", CultureInfo.InvariantCulture);
                case "short":
                {
                    string d = Capitalize(_lang.ShortDays[(int)t.DayOfWeek]);
                    string m = Capitalize(_lang.ShortMonths[t.Month - 1]);
                    string pat = monthFirst ? _lang.ShortPatternMonthFirst : _lang.ShortPattern;
                    return pat.Replace("{D}", d).Replace("{d}", t.Day.ToString(CultureInfo.InvariantCulture)).Replace("{M}", m);
                }
                default:
                {
                    string d = Capitalize(_lang.Days[(int)t.DayOfWeek]);
                    string m = Capitalize(_lang.Months[t.Month - 1]);
                    string pat = monthFirst ? _lang.PatternMonthFirst : _lang.Pattern;
                    return pat.Replace("{D}", d).Replace("{d}", t.Day.ToString(CultureInfo.InvariantCulture)).Replace("{M}", m);
                }
            }
        }

        private static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
