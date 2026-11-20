using System;
using System.IO;
using Launcher.Core;

namespace Launcher.Tests
{
    internal static class SettingsAndFeaturesTests
    {
        private static void CheckPng(string path)
        {
            T.True(File.Exists(path), "asset exists: " + path);
            if (!File.Exists(path)) return;
            byte[] b = File.ReadAllBytes(path);
            T.True(b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47, "valid PNG signature: " + path);
        }

        private static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "res")) && File.Exists(Path.Combine(dir, "Launcher.csproj")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "res")))
                return Directory.GetCurrentDirectory();
            return null;
        }

        public static void Run()
        {
            T.Run("settings: parse, serialize round-trip, boolean aliases, precedence and atomic save", () =>
            {
                string raw =
                    "language=es\n" +
                    "show_weather=false\n" +
                    "weather_mode=manual\n" +
                    "location=41.6523,-4.7245\n" +
                    "location_name=Valladolid # city\n" +
                    "weather_show_location=yes\n" +
                    "weather_show_minmax=0\n" +
                    "units=c\n" +
                    "show_clock=on\n" +
                    "clock=24\n" +
                    "show_date=off\n" +
                    "date_format=iso\n" +
                    "sound=1\n" +
                    "tv_store_app=com.samsung.tv.store\n";
                Settings s = Settings.Parse(raw);
                T.Eq(s.Language, "es", "language parsed");
                T.Eq(s.EffectiveShowWeather, false, "show_weather parsed");
                T.Eq(s.EffectiveWeatherMode, "manual", "weather_mode parsed");
                T.True(s.HasLocation && Math.Abs(s.Latitude - 41.6523) < 1e-6 && Math.Abs(s.Longitude + 4.7245) < 1e-6, "location parsed");
                T.Eq(s.LocationName, "Valladolid", "location_name strips comments");
                T.Eq(s.EffectiveWeatherShowLocation, true, "boolean alias yes");
                T.Eq(s.EffectiveWeatherShowMinMax, false, "boolean alias 0");
                T.Eq(s.Units, 'c', "units parsed");
                T.Eq(s.EffectiveShowClock, true, "boolean alias on");
                T.Eq(s.Clock, 24, "clock parsed");
                T.Eq(s.EffectiveShowDate, false, "boolean alias off");
                T.Eq(s.EffectiveDateFormat, "iso", "date_format parsed");
                T.Eq(s.EffectiveSoundEnabled, true, "boolean alias 1");
                T.Eq(s.TvStoreApp, "com.samsung.tv.store", "tv_store_app parsed");

                Settings roundTrip = Settings.Parse(s.Serialize());
                T.Eq(roundTrip.Language, s.Language, "round-trip language");
                T.Eq(roundTrip.EffectiveShowWeather, s.EffectiveShowWeather, "round-trip show_weather");
                T.Eq(roundTrip.EffectiveWeatherMode, s.EffectiveWeatherMode, "round-trip weather_mode");
                T.True(Math.Abs(roundTrip.Latitude - s.Latitude) < 1e-6 && Math.Abs(roundTrip.Longitude - s.Longitude) < 1e-6, "round-trip coords");
                T.Eq(roundTrip.LocationName, s.LocationName, "round-trip location_name");
                T.Eq(roundTrip.EffectiveWeatherShowLocation, s.EffectiveWeatherShowLocation, "round-trip weather_show_location");
                T.Eq(roundTrip.EffectiveWeatherShowMinMax, s.EffectiveWeatherShowMinMax, "round-trip weather_show_minmax");
                T.Eq(roundTrip.Units, s.Units, "round-trip units");
                T.Eq(roundTrip.EffectiveShowClock, s.EffectiveShowClock, "round-trip show_clock");
                T.Eq(roundTrip.Clock, s.Clock, "round-trip clock");
                T.Eq(roundTrip.EffectiveShowDate, s.EffectiveShowDate, "round-trip show_date");
                T.Eq(roundTrip.EffectiveDateFormat, s.EffectiveDateFormat, "round-trip date_format");
                T.Eq(roundTrip.EffectiveSoundEnabled, s.EffectiveSoundEnabled, "round-trip sound");
                T.Eq(roundTrip.TvStoreApp, s.TvStoreApp, "round-trip tv_store_app");

                // clock=auto and units=auto override lower-priority explicit values
                var high = new Settings();
                T.True(high.Set("clock", "auto"), "clock=auto accepted");
                T.True(high.Set("units", "auto"), "units=auto accepted");
                high.Fill(Settings.Parse("clock=12\nunits=f\nshow_date=false"));
                T.Eq(high.Clock, 0, "explicit clock=auto beats lower clock=12");
                T.Eq(high.Units, '\0', "explicit units=auto beats lower units=f");
                T.Eq(high.EffectiveShowDate, false, "unset field still filled from lower");

                // invalid values rejected
                var inv = new Settings();
                T.True(!inv.Set("weather_mode", "gps"), "bad weather_mode rejected");
                T.True(!inv.Set("date_format", "rfc822"), "bad date_format rejected");
                T.True(!inv.Set("show_weather", "maybe"), "bad bool rejected");
                T.True(!inv.Set("tv_store_app", "bad;cmd"), "bad app id rejected");

                // atomic save
                string tmpDir = Path.Combine(Path.GetTempPath(), "vetro-settings-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);
                try
                {
                    string conf = Path.Combine(tmpDir, "vetro-bar.conf");
                    T.True(s.SaveAtomic(conf), "first SaveAtomic succeeds");
                    s.LocationName = "Madrid";
                    T.True(s.SaveAtomic(conf), "second SaveAtomic overwrites");
                    Settings reloaded = Settings.Parse(File.ReadAllText(conf));
                    T.Eq(reloaded.LocationName, "Madrid", "atomic save persisted updated name");
                }
                finally { Directory.Delete(tmpDir, true); }
            });

            T.Run("localization: clock/date visibility, all 4 date formats, UI ES/EN", () =>
            {
                try
                {
                    var d = new DateTime(2026, 11, 18, 18, 5, 0);
                    Localization.Configure("es_ES.utf8", "ES", Settings.Parse("date_format=long"));
                    T.Eq(Localization.ShowClock, true, "default ShowClock");
                    T.Eq(Localization.ShowDate, true, "default ShowDate");
                    T.Eq(Localization.DayText(d), "Mi\u00e9rcoles 18 de Noviembre", "es long");

                    Localization.Configure("es_ES.utf8", "ES", Settings.Parse("date_format=short"));
                    T.Eq(Localization.DayText(d), "Mi\u00e9 18 Nov", "es short");
                    Localization.Configure("es_ES.utf8", "ES", Settings.Parse("date_format=numeric"));
                    T.Eq(Localization.DayText(d), "18/11/2026", "es numeric");
                    Localization.Configure("es_ES.utf8", "ES", Settings.Parse("date_format=iso"));
                    T.Eq(Localization.DayText(d), "2026-11-18", "es iso");

                    Localization.Configure("en_US.utf8", "US", Settings.Parse("date_format=short"));
                    T.Eq(Localization.DayText(d), "Wed, Nov 18", "US short month-first");
                    Localization.Configure("en_US.utf8", "US", Settings.Parse("date_format=numeric"));
                    T.Eq(Localization.DayText(d), "11/18/2026", "US numeric month-first");

                    foreach (string loc in new[] { "en_GB.utf8", "de_DE.utf8", "fr_FR.utf8", "it_IT.utf8", "pt_PT.utf8", "nl_NL.utf8" })
                    {
                        Localization.Configure(loc, null, Settings.Parse("date_format=short"));
                        T.True(Localization.DayText(d).Length > 0, "short date non-empty for " + loc);
                    }

                    Localization.Configure("es_ES.utf8", "ES", Settings.Parse("show_date=false\nshow_clock=false"));
                    T.Eq(Localization.ShowClock, false, "show_clock=false");
                    T.Eq(Localization.ShowDate, false, "show_date=false");
                    T.Eq(Localization.DayText(d), "", "hidden date returns empty");
                    T.Eq(Localization.CompactRangeText(8, 19), "8\u00b0 / 19\u00b0", "compact range");

                    Localization.Configure("es_ES.utf8", "ES", null);
                    T.Eq(Localization.Ui("settings.title"), "Ajustes del Launcher", "Spanish UI string");
                    Localization.Configure("en_GB.utf8", "GB", null);
                    T.Eq(Localization.Ui("settings.title"), "Launcher Settings", "English UI fallback");
                }
                finally
                {
                    Localization.Configure("es_ES.utf8", "ES", null);
                }
            });

            T.Run("weather: day/night WMO icons, SubText combinations, disable privacy, geocoding", () =>
            {
                try
                {
                    Localization.Configure("es_ES.utf8", "ES", null);
                    T.Eq(Weather.IconFor(0, true), "sun", "WMO 0 day -> sun");
                    T.Eq(Weather.IconFor(0, false), "moon-stars", "WMO 0 night -> moon-stars");
                    T.Eq(Weather.IconFor(1, true), "cloud-sun", "WMO 1 day -> cloud-sun");
                    T.Eq(Weather.IconFor(2, false), "cloud-moon", "WMO 2 night -> cloud-moon");
                    T.Eq(Weather.IconFor(3, false), "cloud", "WMO 3 -> cloud");
                    T.Eq(Weather.IconFor(45, true), "cloud-fog", "WMO 45 -> cloud-fog");
                    T.Eq(Weather.IconFor(48, false), "cloud-fog", "WMO 48 -> cloud-fog");
                    T.Eq(Weather.IconFor(61, true), "cloud-rain", "WMO 61 -> cloud-rain");
                    T.Eq(Weather.IconFor(80, false), "cloud-rain", "WMO 80 -> cloud-rain");
                    T.Eq(Weather.IconFor(71, true), "cloud-snow", "WMO 71 -> cloud-snow");
                    T.Eq(Weather.IconFor(85, false), "cloud-snow", "WMO 85 -> cloud-snow");
                    T.Eq(Weather.IconFor(95, true), "cloud-lightning", "WMO 95 -> cloud-lightning");

                    T.True(Weather.Apply("{\"current\":{\"temperature_2m\":9.4,\"weather_code\":0,\"is_day\":0},\"daily\":{\"temperature_2m_min\":[5.1],\"temperature_2m_max\":[14.9]}}"), "night clear apply");
                    T.Eq(Weather.Icon, "moon-stars", "night icon applied from JSON");
                    T.True(Weather.Apply("{\"current\":{\"temperature_2m\":20,\"weather_code\":2}}"), "omitted is_day defaults to day");
                    T.Eq(Weather.Icon, "cloud-sun", "default day icon");

                    // SubText 2x2 combinations
                    Weather.Apply("{\"current\":{\"temperature_2m\":15,\"weather_code\":1,\"is_day\":1},\"daily\":{\"temperature_2m_min\":[8],\"temperature_2m_max\":[19]}}");
                    Weather.LocationName = "Valladolid";
                    Weather.ShowLocation = false; Weather.ShowMinMax = true;
                    T.Eq(Weather.SubText, "M\u00edn: 8\u00b0   M\u00e1x: 19\u00b0", "SubText: minmax only");
                    Weather.ShowLocation = true; Weather.ShowMinMax = false;
                    T.Eq(Weather.SubText, "Valladolid", "SubText: city only");
                    Weather.ShowLocation = true; Weather.ShowMinMax = true;
                    T.Eq(Weather.SubText, "Valladolid  \u00b7  8\u00b0 / 19\u00b0", "SubText: city + minmax");
                    Weather.LocationName = "Villanueva de los Caballeros del Monte";
                    T.True(Weather.SubText.Contains("\u2026  \u00b7  8\u00b0 / 19\u00b0") && Weather.SubText.Length < 35, "long city name truncated while preserving min/max");
                    Weather.ShowLocation = false; Weather.ShowMinMax = false;
                    T.Eq(Weather.SubText, null, "SubText: neither");

                    // Disabled weather clears readings and completes Refresh immediately
                    Weather.Configure(Settings.Parse("show_weather=false"));
                    T.Eq(Weather.Enabled, false, "Weather.Enabled false");
                    T.Eq(Weather.Text, null, "Weather.Text cleared when disabled");
                    T.Eq(Weather.SubText, null, "Weather.SubText cleared when disabled");
                    T.True(Weather.Refresh().IsCompleted, "Refresh skips network when disabled");

                    // Geocoding URL & parser
                    string gUrl = Weather.BuildGeocodingUrl("San Sebasti\u00e1n", "es");
                    T.True(gUrl.Contains("geocoding-api.open-meteo.com/v1/search") && gUrl.Contains("San%20Sebasti%C3%A1n") && gUrl.Contains("language=es"), "geocoding url escaped");

                    string geoJson =
                        "{\"results\":[" +
                        "{\"id\":1,\"name\":\"Valladolid\",\"latitude\":41.65518,\"longitude\":-4.72372,\"country_code\":\"ES\",\"admin1\":\"Castilla y Le\\u00f3n\",\"country\":\"Espa\\u00f1a\",\"postcodes\":[\"47001\",\"47002\"]}," +
                        "{\"id\":2,\"name\":\"Valladolid\",\"latitude\":20.6881,\"longitude\":-88.1994,\"country_code\":\"MX\",\"country\":\"Mexico\"}" +
                        "],\"generationtime_ms\":1.2}";
                    GeoResult[] res = Weather.ParseGeocoding(geoJson);
                    T.Eq(res.Length, 2, "parsed 2 geocoding results");
                    T.Eq(res[0].Name, "Valladolid", "city name");
                    T.Eq(res[0].Admin1, "Castilla y Le\u00f3n", "unicode unescape admin1");
                    T.Eq(res[0].Country, "Espa\u00f1a", "unicode unescape country");
                    T.Eq(res[0].DisplayLabel, "Valladolid, Castilla y Le\u00f3n, Espa\u00f1a", "display label with admin1");
                    T.Eq(res[1].DisplayLabel, "Valladolid, Mexico", "display label without admin1");
                    T.Eq(Weather.ParseGeocoding("{\"generationtime_ms\":0.4}").Length, 0, "no results property -> empty");
                    T.Eq(Weather.ParseGeocoding("<html>bad</html>").Length, 0, "malformed geocoding -> empty");
                    T.Eq(Weather.TryString("{\"city\":\"S\\u00e3o Paulo\"}", "city"), "S\u00e3o Paulo", "TryString parses city with unicode");
                }
                finally
                {
                    Localization.Configure("es_ES.utf8", "ES", null);
                    Weather.Configure(new Settings());
                }
            });

            T.Run("settings controller: animations, navigation, toggles, virtual keyboard, subviews", () =>
            {
                bool prevInstant = Controller.Instant;
                try
                {
                    Localization.Configure("es_ES.utf8", "ES", null);
                    Controller.Instant = false;
                    var s = new Settings();
                    var inputs = Catalog.BuiltInInputs();
                    var ctl = new SettingsController(s, inputs);
                    T.Eq(ctl.HdmiInputs.Length, 4, "TV filtered out of HDMI inputs list");
                    ctl.ActiveSourceType = 13;
                    ctl.RefreshLabels();

                    T.Eq(ctl.Phase, Phase.Hidden, "starts hidden");
                    T.True(ctl.Show(), "Show starts transition");
                    T.Eq(ctl.Phase, Phase.Showing, "phase is Showing");
                    while (ctl.Tick(1.0 / 60.0)) { }
                    T.Eq(ctl.Phase, Phase.Shown, "settles Shown");
                    T.Eq(ctl.Presence, 1f, "presence 1");
                    T.Eq(ctl.Slide, 0, "slide 0 at rest");

                    // Starts focused on top quick-access tile bar
                    T.Eq(ctl.FocusInTiles, true, "starts focused in top tiles");
                    T.Eq(ctl.TileIndex, 0, "starts on tile 0 (HDMI inputs)");
                    T.Eq(ctl.Key("Right").Action, SettingsActionKind.Moved, "Right moves to tile 1 (Apps)");
                    T.Eq(ctl.TileIndex, 1, "tile 1 focused");
                    T.Eq(ctl.Key("Return").Action, SettingsActionKind.LaunchApps, "OK on tile 1 emits LaunchApps");
                    ctl.Key("Right"); // tile 2 (Vetro)
                    T.Eq(ctl.Key("Return").Action, SettingsActionKind.LaunchVetro, "OK on tile 2 emits LaunchVetro");
                    ctl.Key("Right"); // tile 3 (TV-Store)
                    T.Eq(ctl.Key("Return").Action, SettingsActionKind.LaunchTvStore, "OK on tile 3 emits LaunchTvStore");
                    ctl.Key("Right"); // tile 4 (Native settings)
                    T.Eq(ctl.Key("Return").Action, SettingsActionKind.LaunchTvSettings, "OK on tile 4 emits LaunchTvSettings");
                    ctl.Key("Left"); ctl.Key("Left"); ctl.Key("Left"); ctl.Key("Left");
                    T.Eq(ctl.TileIndex, 0, "back on tile 0");

                    // Down from tiles moves into settings list (ShowWeather)
                    T.Eq(ctl.Key("Down").Action, SettingsActionKind.Moved, "Down enters settings list");
                    T.Eq(ctl.FocusInTiles, false, "FocusInTiles is false");
                    T.Eq(ctl.Rows[ctl.FocusIndex].Id, SettingsItemId.ShowWeather, "first list focus skips section header straight to ShowWeather");
                    T.Eq(ctl.Rows[ctl.FocusIndex].SwitchOn, true, "ShowWeather switch starts ON");
                    SettingsKeyResult r = ctl.Key("Return");
                    T.Eq(r.Action, SettingsActionKind.Changed, "toggling ShowWeather emits Changed");
                    T.Eq(s.EffectiveShowWeather, false, "ShowWeather became false");
                    T.Eq(ctl.Rows[ctl.FocusIndex].SwitchOn, false, "ShowWeather switch reflects OFF");

                    // Up from first row returns to top tiles
                    T.Eq(ctl.Key("Up").Action, SettingsActionKind.Moved, "Up returns to top tiles");
                    T.Eq(ctl.FocusInTiles, true, "FocusInTiles restored");
                    ctl.Key("Down");

                    // Dependent weather rows are disabled and skipped on Down
                    SettingsKeyResult nextMove = ctl.Key("Down");
                    T.Eq(nextMove.Action, SettingsActionKind.Moved, "moved past disabled weather rows");
                    T.Eq(ctl.Rows[ctl.FocusIndex].Id, SettingsItemId.ShowClock, "skipped disabled weather items straight to ShowClock");

                    // Turn ShowWeather back on and move to WeatherMode -> switch to manual with no coords -> opens CitySearch
                    ctl.Key("Up");
                    ctl.Key("Return"); // ShowWeather -> true
                    T.Eq(s.EffectiveShowWeather, true, "ShowWeather restored true");
                    ctl.Key("Down"); // WeatherMode
                    T.Eq(ctl.Rows[ctl.FocusIndex].Id, SettingsItemId.WeatherMode, "focused WeatherMode");
                    SettingsKeyResult modeRes = ctl.Key("Right");
                    T.Eq(modeRes.Action, SettingsActionKind.Changed, "mode switch emits Changed");
                    T.Eq(ctl.View, SettingsView.CitySearch, "auto->manual without coords opens CitySearch");

                    // Virtual keyboard typing, validation, not-found prompt, and selection
                    ctl.Key("Down"); ctl.Key("Down"); ctl.Key("Down"); // row 3 col 1..2 -> BACKSPACE / SEARCH
                    // First test empty search validation (<2 chars): move to SEARCH (row 3, col 3)
                    ctl.Key("Right"); ctl.Key("Right"); ctl.Key("Right");
                    T.Eq(ctl.KeyRow, 3, "row 3");
                    T.Eq(ctl.KeyCol, 3, "col 3 (SEARCH)");
                    SettingsKeyResult emptySearch = ctl.Key("Select");
                    T.Eq(emptySearch.Action, SettingsActionKind.Moved, "empty search stays local");
                    T.True(ctl.CityIsError, "empty query shows error prompt");

                    // Back out and re-open CitySearch cleanly via Main -> CitySearch
                    ctl.Key("XF86Back");
                    T.Eq(ctl.View, SettingsView.Main, "Back returns to Main");
                    T.Eq(ctl.FocusInTiles, false, "stays in settings list");
                    T.Eq(ctl.Rows[ctl.FocusIndex].Id, SettingsItemId.CitySearch, "CitySearch row visible and focused in manual mode");
                    ctl.Key("Select");
                    T.Eq(ctl.View, SettingsView.CitySearch, "opened CitySearch");

                    // Type "QA" (Q = row 0 col 0, A = row 1 col 0)
                    ctl.Key("Select"); // Q
                    ctl.Key("Down");
                    ctl.Key("Select"); // A
                    T.Eq(ctl.CityQuery, "QA", "typed QA");
                    ctl.Key("Down"); ctl.Key("Down"); // row 3 col 0 (SPACE)
                    ctl.Key("Right"); ctl.Key("Right"); ctl.Key("Right"); // col 3 (SEARCH)
                    SettingsKeyResult searchAct = ctl.Key("Select");
                    T.Eq(searchAct.Action, SettingsActionKind.SearchCity, "SEARCH emits SearchCity");
                    T.Eq(searchAct.Query, "QA", "SearchCity carries query");
                    T.Eq(ctl.CityBusy, true, "CityBusy while searching");

                    // Cancelling during CityBusy with Back returns immediately
                    T.Eq(ctl.Key("XF86Back").Action, SettingsActionKind.Moved, "Back cancels busy city search");
                    T.Eq(ctl.CityBusy, false, "CityBusy cleared on Back");
                    T.Eq(ctl.View, SettingsView.Main, "returned to Main on cancel");

                    // Re-open CitySearch and test results flow
                    ctl.Key("Select");
                    ctl.Key("Select"); ctl.Key("Down"); ctl.Key("Select");
                    ctl.Key("Down"); ctl.Key("Down"); ctl.Key("Right"); ctl.Key("Right"); ctl.Key("Right");
                    ctl.Key("Select");

                    // Empty results prompt user to check spelling
                    ctl.SetCitySearchResults(new GeoResult[0], null);
                    T.Eq(ctl.CityBusy, false, "CityBusy cleared");
                    T.Eq(ctl.CityIsError, true, "empty results set CityIsError");
                    T.True(ctl.CityStatus.Contains("QA"), "not-found message mentions query");

                    // Populated results let user pick city -> updates settings and triggers refresh
                    ctl.SetCitySearchResults(new[] {
                        new GeoResult("Madrid", "Comunidad de Madrid", "Spain", "ES", 40.4165, -3.70256),
                        new GeoResult("Madrid", "Cundinamarca", "Colombia", "CO", 4.73245, -74.26419)
                    }, null);
                    T.Eq(ctl.CityIsError, false, "valid results clear error");
                    T.Eq(ctl.CityInResults, true, "focus moved into results list");
                    SettingsKeyResult pick = ctl.Key("Return");
                    T.Eq(pick.Action, SettingsActionKind.Changed, "selecting city emits Changed");
                    T.Eq(pick.NeedsWeatherRefresh, true, "selecting city triggers weather refresh");
                    T.Eq(ctl.View, SettingsView.Main, "returned to Main");
                    T.Eq(s.LocationName, "Madrid", "saved city name");
                    T.True(s.HasLocation && Math.Abs(s.Latitude - 40.4165) < 1e-6, "saved city latitude");

                    // Inputs subview via tile 0
                    while (ctl.Key("Up").Action == SettingsActionKind.Moved) { }
                    T.Eq(ctl.FocusInTiles, true, "returned to top tiles");
                    T.Eq(ctl.TileIndex, 0, "at HDMI tile");
                    ctl.Key("Return");
                    T.Eq(ctl.View, SettingsView.Inputs, "Inputs subview opened");
                    T.Eq(ctl.SubIndex, 0, "focused active HDMI 1 (SourceType 13, index 0 of HdmiInputs)");
                    ctl.Key("Down"); // HDMI 2
                    SettingsKeyResult sw = ctl.Key("Return");
                    T.Eq(sw.Action, SettingsActionKind.SwitchInput, "OK emits SwitchInput");
                    T.Eq(sw.Index, 1, "selected HDMI 2");
                    ctl.Key("XF86Back");
                    T.Eq(ctl.View, SettingsView.Main, "Back from Inputs returns to Main");
                    T.Eq(ctl.FocusInTiles, true, "returns to HDMI tile");

                    // Back on Main closes modal; Home closes all
                    T.Eq(ctl.Key("XF86Back").Action, SettingsActionKind.Close, "Back on Main closes modal");
                    T.Eq(ctl.Key("XF86Home").Action, SettingsActionKind.CloseAll, "Home closes all");

                    ctl.Hide();
                    while (ctl.Tick(1.0 / 60.0)) { }
                    T.Eq(ctl.Phase, Phase.Hidden, "hide animation finishes at Hidden");
                }
                finally
                {
                    Controller.Instant = prevInstant;
                    Localization.Configure("es_ES.utf8", "ES", null);
                    Weather.Configure(new Settings());
                }
            });

            T.Run("phosphor assets: all dock, input, weather and settings UI PNGs exist", () =>
            {
                string root = FindRepoRoot();
                T.True(root != null, "located repository root");
                if (root == null) return;

                CheckPng(Path.Combine(root, "res", "chev_up.png"));
                CheckPng(Path.Combine(root, "res", "chev_down.png"));
                foreach (string icon in new[] {
                    "livetv", "tvplus", "settings", "samsunghome", "in-tv", "in-hdmi1", "in-hdmi2", "in-hdmi3", "in-hdmi4",
                    "qs-hdmi", "qs-apps", "qs-vetro", "qs-store", "qs-settings"
                })
                    CheckPng(Path.Combine(root, "res", "icons", icon + ".png"));
                foreach (string w in new[] { "sun", "moon-stars", "cloud-sun", "cloud-moon", "cloud", "cloud-fog", "cloud-rain", "cloud-snow", "cloud-lightning" })
                    CheckPng(Path.Combine(root, "res", "weather", w + ".png"));
                T.True(!File.Exists(Path.Combine(root, "res", "weather", "rain.png")), "obsolete rain.png removed");
                T.True(!File.Exists(Path.Combine(root, "res", "weather", "snow.png")), "obsolete snow.png removed");
                foreach (string ui in new[] {
                    "gear", "plug", "squares-four", "app-window", "wrench", "storefront", "sliders-horizontal",
                    "cloud-sun", "compass", "arrows-down-up", "thermometer", "clock", "timer", "calendar", "calendar-dots",
                    "speaker-high", "speaker-slash", "magnifying-glass",
                    "caret-left", "caret-right", "caret-up", "caret-down", "backspace", "check", "x",
                    "map-pin", "television-simple", "house", "switch_on", "switch_off"
                })
                    CheckPng(Path.Combine(root, "res", "ui", ui + ".png"));
            });

            T.Run("dock focus: TDT is index 0 and resetting focus on open starts on TDT", () =>
            {
                AppEntry[] bar = Catalog.ArrangeBar(Catalog.BuiltIn());
                T.Eq(bar[0].Slug, "livetv", "TDT/Live TV is always first tile");
                var layout = new DockLayout(bar.Length, 1920, 1080, 0, false, Catalog.GroupStart(bar), Catalog.LeftPinCount(bar), true);
                var ctl = new Controller(layout);
                ctl.Show();
                int idx;
                ctl.Key("Right", out idx);
                ctl.Key("Right", out idx);
                T.True(ctl.FocusTarget > 0, "moved focus away from TDT");
                ctl.Hide();
                ctl.SetFocus(0);
                ctl.Show();
                T.Eq(ctl.FocusTarget, 0, "focus resets to TDT (index 0) on open");
                T.Eq(ctl.FocusPos, 0f, "focus position is 0 immediately");
            });
        }
    }
}
