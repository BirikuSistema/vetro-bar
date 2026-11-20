using System;
using System.Collections.Generic;

namespace Launcher.Core
{
    /// <summary>
    /// Pure state machine for the bottom-anchored Settings drawer (5 top quick-access tiles + settings list),
    /// HDMI Inputs subview, and Virtual Keyboard + Open-Meteo city search.
    /// Zero EFL dependencies so every transition is unit-tested on a desktop.
    /// </summary>
    internal sealed class SettingsController
    {
        public const double ShowSeconds = 0.24;
        public const double HideSeconds = 0.16;
        public const int SlideDistance = 36;
        public const int MaxVisibleRows = 6;
        public const int MaxQueryLength = 40;
        public const int QuickTileCount = 5;

        private readonly Settings _settings;
        private readonly AppEntry[] _inputs;
        private readonly List<SettingsRow> _rows = new List<SettingsRow>();

        public Phase Phase { get; private set; }
        public float Presence { get; private set; }
        public bool IsActive { get { return Phase == Phase.Shown || Phase == Phase.Showing; } }
        public int Slide { get { return Controller.Instant ? 0 : (int)Math.Round((1f - Controller.Ease(Presence)) * SlideDistance); } }

        public SettingsView View { get; private set; }
        public IReadOnlyList<SettingsRow> Rows { get { return _rows; } }
        public AppEntry[] HdmiInputs { get { return _inputs; } }
        public bool FocusInTiles { get; private set; } = true;
        public int TileIndex { get; private set; }
        public int FocusIndex { get; private set; }
        public int ScrollOffset { get; private set; }
        public int SubIndex { get; private set; }
        public int ActiveSourceType { get; set; } = -1;

        public float TileFocus { get { return _tileFocus.Value; } }
        public float TileActive { get { return _tileActive.Value; } }
        public float ListFocus { get { return _listFocus.Value; } }
        public float ListScroll { get { return _listScroll.Value; } }
        public float ListActive { get { return _listActive.Value; } }

        private AnimVal _tileFocus;
        private AnimVal _tileActive;
        private AnimVal _listFocus;
        private AnimVal _listScroll;
        private AnimVal _listActive;

        private struct AnimVal
        {
            public float Value;
            public float Target;
            private float _from;
            private double _t;
            private double _dur;

            public void Snap(float v) { Value = v; Target = v; _from = v; _t = 0; _dur = 0; }

            public void To(float target, double dur)
            {
                if (Controller.Instant || dur <= 0) { Snap(target); return; }
                if (Math.Abs(target - Target) < 1e-4f) return;
                _from = Value;
                Target = target;
                _t = 0;
                _dur = dur;
            }

            public bool Tick(double dt)
            {
                if (Math.Abs(Value - Target) < 1e-4f) { Value = Target; return false; }
                if (Controller.Instant || _dur <= 0) { Value = Target; return false; }
                _t += dt;
                float u = (float)(_t / _dur);
                if (u >= 1f) { Value = Target; return false; }
                float k = Design.LinearMotion ? u : Controller.Ease(u);
                Value = _from + (Target - _from) * k;
                return true;
            }
        }

        // City search state
        public string CityQuery { get; private set; } = "";
        public string CityStatus { get; private set; } = "";
        public bool CityIsError { get; private set; }
        public bool CityBusy { get; private set; }
        public GeoResult[] CityResults { get; private set; } = new GeoResult[0];
        public bool CityInResults { get; private set; }
        public int KeyRow { get; private set; }
        public int KeyCol { get; private set; }
        public int CityResultIndex { get; private set; }

        public SettingsController(Settings settings, AppEntry[] inputs)
        {
            _settings = settings ?? new Settings();
            _inputs = FilterHdmiOnly(inputs);
            Phase = Phase.Hidden;
            RebuildMainRows(SettingsItemId.None);
        }

        private static AppEntry[] FilterHdmiOnly(AppEntry[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return new AppEntry[0];
            var list = new List<AppEntry>(inputs.Length);
            foreach (AppEntry e in inputs)
            {
                if (e != null && e.SourceType != 0)
                    list.Add(e);
            }
            return list.ToArray();
        }

        public bool Show()
        {
            if (Phase == Phase.Shown || Phase == Phase.Showing) return false;
            View = SettingsView.Main;
            FocusInTiles = true;
            TileIndex = 0;
            RebuildMainRows(SettingsItemId.None);
            FocusIndex = FirstSelectableRow();
            ScrollOffset = 0;
            SnapAnimations();
            if (Controller.Instant)
            {
                Phase = Phase.Shown;
                Presence = 1f;
                return true;
            }
            Phase = Phase.Showing;
            return true;
        }

        public bool Hide()
        {
            if (Phase == Phase.Hidden || Phase == Phase.Hiding) return false;
            CityBusy = false;
            if (Controller.Instant)
            {
                Phase = Phase.Hidden;
                Presence = 0f;
                return true;
            }
            Phase = Phase.Hiding;
            return true;
        }

        public void Reset()
        {
            Phase = Phase.Hidden;
            Presence = 0f;
            View = SettingsView.Main;
            FocusInTiles = true;
            TileIndex = 0;
            FocusIndex = FirstSelectableRow();
            ScrollOffset = 0;
            SnapAnimations();
            CityBusy = false;
        }

        public bool Tick(double dt)
        {
            if (dt < 0) dt = 0;
            if (dt > 0.1) dt = 0.1;

            switch (Phase)
            {
                case Phase.Showing:
                    Presence += (float)(dt / ShowSeconds);
                    if (Presence >= 1f) { Presence = 1f; Phase = Phase.Shown; }
                    break;
                case Phase.Hiding:
                    Presence -= (float)(dt / HideSeconds);
                    if (Presence <= 0f) { Presence = 0f; Phase = Phase.Hidden; }
                    break;
            }
            bool more = Phase == Phase.Showing || Phase == Phase.Hiding;
            if (_tileFocus.Tick(dt)) more = true;
            if (_tileActive.Tick(dt)) more = true;
            if (_listFocus.Tick(dt)) more = true;
            if (_listScroll.Tick(dt)) more = true;
            if (_listActive.Tick(dt)) more = true;
            return more;
        }

        private void SnapAnimations()
        {
            _tileFocus.Snap(TileIndex);
            _tileActive.Snap(View == SettingsView.Main && FocusInTiles ? 1f : 0f);
            _listFocus.Snap(View == SettingsView.Main ? FocusIndex : SubIndex);
            _listScroll.Snap(View == SettingsView.Main ? ScrollOffset : Math.Max(0, SubIndex - MaxVisibleRows + 1));
            _listActive.Snap(View != SettingsView.Main || !FocusInTiles ? 1f : 0f);
        }

        private void SyncTargets(bool snapList)
        {
            _tileFocus.To(TileIndex, Design.FocusSeconds);
            _tileActive.To(View == SettingsView.Main && FocusInTiles ? 1f : 0f, 0.22);
            _listActive.To(View != SettingsView.Main || !FocusInTiles ? 1f : 0f, 0.20);
            int focusTgt = View == SettingsView.Main ? FocusIndex : SubIndex;
            int scrollTgt = View == SettingsView.Main ? ScrollOffset : Math.Max(0, SubIndex - MaxVisibleRows + 1);
            if (snapList)
            {
                _listFocus.Snap(focusTgt);
                _listScroll.Snap(scrollTgt);
            }
            else
            {
                _listFocus.To(focusTgt, 0.18);
                _listScroll.To(scrollTgt, 0.22);
            }
        }

        public void RefreshLabels()
        {
            if (View == SettingsView.Main)
            {
                SettingsItemId keep = FocusIndex >= 0 && FocusIndex < _rows.Count ? _rows[FocusIndex].Id : SettingsItemId.None;
                RebuildMainRows(keep);
            }
        }

        public string ActiveTileTitle()
        {
            int idx = TileIndex < 0 ? 0 : (TileIndex >= QuickTiles.All.Length ? QuickTiles.All.Length - 1 : TileIndex);
            QuickTileSpec spec = QuickTiles.All[idx];
            string label = Localization.Ui(spec.LabelKey);
            if (spec.Id == SettingsItemId.Inputs)
            {
                string cur = ActiveHdmiLabel();
                if (!string.IsNullOrEmpty(cur)) return label + "  \u00b7  " + cur;
            }
            return label;
        }

        public void SetCitySearchResults(GeoResult[] results, string error)
        {
            CityBusy = false;
            if (!string.IsNullOrEmpty(error))
            {
                CityResults = new GeoResult[0];
                CityStatus = string.Format(Localization.Ui("sub.city.error"), error);
                CityIsError = true;
                CityInResults = false;
                return;
            }
            if (results == null || results.Length == 0)
            {
                CityResults = new GeoResult[0];
                CityStatus = string.Format(Localization.Ui("sub.city.not_found"), CityQuery.Trim());
                CityIsError = true;
                CityInResults = false;
                return;
            }
            CityResults = results;
            CityStatus = Localization.Ui("sub.city.select");
            CityIsError = false;
            CityInResults = true;
            CityResultIndex = 0;
        }

        public SettingsKeyResult Key(string key)
        {
            if (key == null || !IsActive) return SettingsKeyResult.Of(SettingsActionKind.None);
            if (key == "XF86Home" || key == "XF86Exit") return SettingsKeyResult.Of(SettingsActionKind.CloseAll);

            SettingsView prevView = View;
            SettingsKeyResult res;
            switch (View)
            {
                case SettingsView.Main: res = HandleMainKey(key); break;
                case SettingsView.CitySearch: res = HandleCityKey(key); break;
                case SettingsView.Inputs: res = HandleInputsKey(key); break;
                default: res = SettingsKeyResult.Of(SettingsActionKind.None); break;
            }
            if (res.Action != SettingsActionKind.None || View != prevView)
                SyncTargets(View != prevView);
            return res;
        }

        private SettingsKeyResult HandleMainKey(string key)
        {
            if (key == "XF86Back" || key == "Escape")
                return SettingsKeyResult.Of(SettingsActionKind.Close);

            if (FocusInTiles)
            {
                switch (key)
                {
                    case "Left":
                        if (TileIndex > 0) { TileIndex--; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                        return SettingsKeyResult.Of(SettingsActionKind.None);
                    case "Right":
                        if (TileIndex + 1 < QuickTileCount) { TileIndex++; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                        return SettingsKeyResult.Of(SettingsActionKind.None);
                    case "Down":
                        int first = FirstSelectableRow();
                        if (_rows.Count > 0 && _rows[first].Enabled)
                        {
                            FocusInTiles = false;
                            FocusIndex = first;
                            ScrollOffset = 0;
                            EnsureVisible();
                            return SettingsKeyResult.Of(SettingsActionKind.Moved);
                        }
                        return SettingsKeyResult.Of(SettingsActionKind.None);
                    case "Return":
                    case "KP_Enter":
                    case "Select":
                        return ActivateTile(TileIndex);
                    default:
                        return SettingsKeyResult.Of(SettingsActionKind.None);
                }
            }

            switch (key)
            {
                case "Up":
                    return MoveMainUp();
                case "Down":
                    return MoveMainDown();
                case "Left":
                    return AdjustFocusedRow(-1);
                case "Right":
                    return AdjustFocusedRow(1);
                case "Return":
                case "KP_Enter":
                case "Select":
                    return ActivateFocusedRow();
                default:
                    return SettingsKeyResult.Of(SettingsActionKind.None);
            }
        }

        private SettingsKeyResult ActivateTile(int index)
        {
            if (index < 0 || index >= QuickTiles.All.Length) return SettingsKeyResult.Of(SettingsActionKind.None);
            switch (QuickTiles.All[index].Id)
            {
                case SettingsItemId.Inputs:
                    View = SettingsView.Inputs;
                    SubIndex = FindCurrentInputIndex();
                    return SettingsKeyResult.Of(SettingsActionKind.Moved);
                case SettingsItemId.Apps:
                    return SettingsKeyResult.Of(SettingsActionKind.LaunchApps);
                case SettingsItemId.Vetro:
                    return SettingsKeyResult.Of(SettingsActionKind.LaunchVetro);
                case SettingsItemId.TvStore:
                    return SettingsKeyResult.Of(SettingsActionKind.LaunchTvStore);
                case SettingsItemId.TvSettings:
                    return SettingsKeyResult.Of(SettingsActionKind.LaunchTvSettings);
                default:
                    return SettingsKeyResult.Of(SettingsActionKind.None);
            }
        }

        private SettingsKeyResult MoveMainUp()
        {
            int i = FocusIndex - 1;
            while (i >= 0)
            {
                if (_rows[i].Kind != SettingsItemKind.Header && _rows[i].Enabled)
                {
                    FocusIndex = i;
                    EnsureVisible();
                    return SettingsKeyResult.Of(SettingsActionKind.Moved);
                }
                i--;
            }
            FocusInTiles = true;
            ScrollOffset = 0;
            return SettingsKeyResult.Of(SettingsActionKind.Moved);
        }

        private SettingsKeyResult MoveMainDown()
        {
            int i = FocusIndex + 1;
            while (i < _rows.Count)
            {
                if (_rows[i].Kind != SettingsItemKind.Header && _rows[i].Enabled)
                {
                    FocusIndex = i;
                    EnsureVisible();
                    return SettingsKeyResult.Of(SettingsActionKind.Moved);
                }
                i++;
            }
            return SettingsKeyResult.Of(SettingsActionKind.None);
        }

        private SettingsKeyResult AdjustFocusedRow(int dir)
        {
            if (FocusIndex < 0 || FocusIndex >= _rows.Count) return SettingsKeyResult.Of(SettingsActionKind.None);
            SettingsRow row = _rows[FocusIndex];
            if (!row.Enabled) return SettingsKeyResult.Of(SettingsActionKind.None);

            switch (row.Id)
            {
                case SettingsItemId.ShowWeather:
                    return ToggleShowWeather();
                case SettingsItemId.WeatherMode:
                    return ToggleWeatherMode();
                case SettingsItemId.WeatherShowLocation:
                    bool nextLoc = !_settings.EffectiveWeatherShowLocation;
                    _settings.WeatherShowLocation = nextLoc;
                    RebuildMainRows(row.Id);
                    bool needCity = nextLoc && _settings.EffectiveWeatherMode == "auto" && string.IsNullOrWhiteSpace(Weather.LocationName);
                    return new SettingsKeyResult { Action = SettingsActionKind.Changed, NeedsWeatherRefresh = needCity };
                case SettingsItemId.WeatherShowMinMax:
                    _settings.WeatherShowMinMax = !_settings.EffectiveWeatherShowMinMax;
                    RebuildMainRows(row.Id);
                    return SettingsKeyResult.Of(SettingsActionKind.Changed);
                case SettingsItemId.Units:
                    return CycleUnits(dir);
                case SettingsItemId.ShowClock:
                    _settings.ShowClock = !_settings.EffectiveShowClock;
                    RebuildMainRows(row.Id);
                    return SettingsKeyResult.Of(SettingsActionKind.Changed);
                case SettingsItemId.ClockFormat:
                    return CycleClockFormat(dir);
                case SettingsItemId.ShowDate:
                    _settings.ShowDate = !_settings.EffectiveShowDate;
                    RebuildMainRows(row.Id);
                    return SettingsKeyResult.Of(SettingsActionKind.Changed);
                case SettingsItemId.DateFormat:
                    return CycleDateFormat(dir);
                case SettingsItemId.SoundEnabled:
                    _settings.SoundEnabled = !_settings.EffectiveSoundEnabled;
                    RebuildMainRows(row.Id);
                    return SettingsKeyResult.Of(SettingsActionKind.Changed);
                default:
                    return SettingsKeyResult.Of(SettingsActionKind.None);
            }
        }

        private SettingsKeyResult ActivateFocusedRow()
        {
            if (FocusIndex < 0 || FocusIndex >= _rows.Count) return SettingsKeyResult.Of(SettingsActionKind.None);
            SettingsRow row = _rows[FocusIndex];
            if (!row.Enabled) return SettingsKeyResult.Of(SettingsActionKind.None);

            switch (row.Id)
            {
                case SettingsItemId.CitySearch:
                    OpenCitySearch();
                    return SettingsKeyResult.Of(SettingsActionKind.Moved);
                default:
                    return AdjustFocusedRow(1);
            }
        }

        private SettingsKeyResult ToggleShowWeather()
        {
            bool next = !_settings.EffectiveShowWeather;
            _settings.ShowWeather = next;
            RebuildMainRows(SettingsItemId.ShowWeather);
            return new SettingsKeyResult { Action = SettingsActionKind.Changed, NeedsWeatherRefresh = next };
        }

        private SettingsKeyResult ToggleWeatherMode()
        {
            string cur = _settings.EffectiveWeatherMode;
            bool toManual = cur == "auto";
            _settings.WeatherMode = toManual ? "manual" : "auto";
            if (toManual && !_settings.HasLocation)
            {
                FocusInTiles = false;
                RebuildMainRows(SettingsItemId.CitySearch);
                OpenCitySearch();
                return new SettingsKeyResult { Action = SettingsActionKind.Changed, NeedsWeatherRefresh = false };
            }
            RebuildMainRows(SettingsItemId.WeatherMode);
            return new SettingsKeyResult { Action = SettingsActionKind.Changed, NeedsWeatherRefresh = true };
        }

        private SettingsKeyResult CycleUnits(int dir)
        {
            char[] order = { '\0', 'c', 'f' };
            int idx = Array.IndexOf(order, _settings.Units);
            if (idx < 0) idx = 0;
            idx = (idx + dir + order.Length) % order.Length;
            _settings.Units = order[idx];
            RebuildMainRows(SettingsItemId.Units);
            return new SettingsKeyResult { Action = SettingsActionKind.Changed, NeedsWeatherRefresh = true };
        }

        private SettingsKeyResult CycleClockFormat(int dir)
        {
            int[] order = { 0, 24, 12 };
            int idx = Array.IndexOf(order, _settings.Clock);
            if (idx < 0) idx = 0;
            idx = (idx + dir + order.Length) % order.Length;
            _settings.Clock = order[idx];
            RebuildMainRows(SettingsItemId.ClockFormat);
            return SettingsKeyResult.Of(SettingsActionKind.Changed);
        }

        private SettingsKeyResult CycleDateFormat(int dir)
        {
            string[] order = { "long", "short", "numeric", "iso" };
            int idx = Array.IndexOf(order, _settings.EffectiveDateFormat);
            if (idx < 0) idx = 0;
            idx = (idx + dir + order.Length) % order.Length;
            _settings.DateFormat = order[idx];
            RebuildMainRows(SettingsItemId.DateFormat);
            return SettingsKeyResult.Of(SettingsActionKind.Changed);
        }

        private void OpenCitySearch()
        {
            View = SettingsView.CitySearch;
            CityQuery = _settings.LocationName ?? "";
            CityStatus = Localization.Ui("sub.city.prompt");
            CityIsError = false;
            CityBusy = false;
            CityResults = new GeoResult[0];
            CityInResults = false;
            KeyRow = 0;
            KeyCol = 0;
            CityResultIndex = 0;
        }

        private SettingsKeyResult HandleCityKey(string key)
        {
            if (CityInResults)
            {
                switch (key)
                {
                    case "Up":
                        if (CityResultIndex > 0) { CityResultIndex--; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                        CityInResults = false; KeyRow = 3; KeyCol = 3;
                        return SettingsKeyResult.Of(SettingsActionKind.Moved);
                    case "Down":
                        if (CityResultIndex + 1 < CityResults.Length) { CityResultIndex++; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                        return SettingsKeyResult.Of(SettingsActionKind.None);
                    case "Return":
                    case "KP_Enter":
                    case "Select":
                        if (CityResultIndex >= 0 && CityResultIndex < CityResults.Length)
                        {
                            GeoResult res = CityResults[CityResultIndex];
                            _settings.HasLocation = true;
                            _settings.Latitude = res.Latitude;
                            _settings.Longitude = res.Longitude;
                            _settings.LocationName = res.Name;
                            _settings.WeatherMode = "manual";
                            View = SettingsView.Main;
                            FocusInTiles = false;
                            RebuildMainRows(SettingsItemId.CitySearch);
                            return new SettingsKeyResult { Action = SettingsActionKind.Changed, NeedsWeatherRefresh = true };
                        }
                        return SettingsKeyResult.Of(SettingsActionKind.None);
                    case "XF86Back":
                    case "Escape":
                        CityInResults = false; KeyRow = 3; KeyCol = 3;
                        return SettingsKeyResult.Of(SettingsActionKind.Moved);
                    default:
                        return SettingsKeyResult.Of(SettingsActionKind.None);
                }
            }

            switch (key)
            {
                case "Left":
                    if (KeyCol > 0) { KeyCol--; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "Right":
                    if (KeyCol + 1 < VirtualKeyboard.Rows[KeyRow].Length) { KeyCol++; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "Up":
                    if (KeyRow == 0) return SettingsKeyResult.Of(SettingsActionKind.None);
                    if (KeyRow == 3) { KeyRow = 2; KeyCol = VirtualKeyboard.MapRow3ToRow2(KeyCol); }
                    else KeyRow--;
                    return SettingsKeyResult.Of(SettingsActionKind.Moved);
                case "Down":
                    if (KeyRow < 2) { KeyRow++; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                    if (KeyRow == 2) { KeyRow = 3; KeyCol = VirtualKeyboard.MapRow2ToRow3(KeyCol); return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                    if (CityResults.Length > 0) { CityInResults = true; CityResultIndex = 0; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "Return":
                case "KP_Enter":
                case "Select":
                    return PressKeyboardKey();
                case "XF86Back":
                case "Escape":
                    CityBusy = false;
                    View = SettingsView.Main;
                    FocusInTiles = false;
                    RebuildMainRows(SettingsItemId.CitySearch);
                    return SettingsKeyResult.Of(SettingsActionKind.Moved);
                default:
                    return SettingsKeyResult.Of(SettingsActionKind.None);
            }
        }

        private SettingsKeyResult PressKeyboardKey()
        {
            if (CityBusy) return SettingsKeyResult.Of(SettingsActionKind.None);
            KeyboardKey k = VirtualKeyboard.Rows[KeyRow][KeyCol];
            switch (k.Code)
            {
                case "SPACE":
                    if (CityQuery.Length > 0 && CityQuery[CityQuery.Length - 1] != ' ' && CityQuery.Length < MaxQueryLength)
                    {
                        CityQuery += " ";
                        ClearCityErrorPrompt();
                        return SettingsKeyResult.Of(SettingsActionKind.Moved);
                    }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "BACKSPACE":
                    if (CityQuery.Length > 0)
                    {
                        CityQuery = CityQuery.Substring(0, CityQuery.Length - 1);
                        ClearCityErrorPrompt();
                        return SettingsKeyResult.Of(SettingsActionKind.Moved);
                    }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "CLEAR":
                    if (CityQuery.Length > 0 || CityResults.Length > 0)
                    {
                        CityQuery = "";
                        CityResults = new GeoResult[0];
                        ClearCityErrorPrompt();
                        return SettingsKeyResult.Of(SettingsActionKind.Moved);
                    }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "SEARCH":
                    string q = CityQuery.Trim();
                    if (q.Length < 2)
                    {
                        CityStatus = Localization.Ui("sub.city.empty");
                        CityIsError = true;
                        return SettingsKeyResult.Of(SettingsActionKind.Moved);
                    }
                    CityBusy = true;
                    CityStatus = Localization.Ui("sub.city.searching");
                    CityIsError = false;
                    CityResults = new GeoResult[0];
                    return new SettingsKeyResult { Action = SettingsActionKind.SearchCity, Query = q };
                default:
                    if (CityQuery.Length < MaxQueryLength)
                    {
                        CityQuery += k.Code;
                        ClearCityErrorPrompt();
                        return SettingsKeyResult.Of(SettingsActionKind.Moved);
                    }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
            }
        }

        private void ClearCityErrorPrompt()
        {
            if (CityIsError)
            {
                CityIsError = false;
                CityStatus = Localization.Ui("sub.city.prompt");
            }
        }

        private SettingsKeyResult HandleInputsKey(string key)
        {
            switch (key)
            {
                case "Up":
                    if (SubIndex > 0) { SubIndex--; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "Down":
                    if (SubIndex + 1 < _inputs.Length) { SubIndex++; return SettingsKeyResult.Of(SettingsActionKind.Moved); }
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "Return":
                case "KP_Enter":
                case "Select":
                    if (SubIndex >= 0 && SubIndex < _inputs.Length)
                        return new SettingsKeyResult { Action = SettingsActionKind.SwitchInput, Index = SubIndex };
                    return SettingsKeyResult.Of(SettingsActionKind.None);
                case "XF86Back":
                case "Escape":
                    View = SettingsView.Main;
                    FocusInTiles = true;
                    TileIndex = 0;
                    RebuildMainRows(SettingsItemId.None);
                    return SettingsKeyResult.Of(SettingsActionKind.Moved);
                default:
                    return SettingsKeyResult.Of(SettingsActionKind.None);
            }
        }

        private int FindCurrentInputIndex()
        {
            for (int i = 0; i < _inputs.Length; i++)
                if (_inputs[i].SourceType == ActiveSourceType) return i;
            return 0;
        }

        private string ActiveHdmiLabel()
        {
            for (int i = 0; i < _inputs.Length; i++)
                if (_inputs[i].SourceType == ActiveSourceType) return _inputs[i].Label;
            return null;
        }

        public void RebuildMainRows(SettingsItemId focusId)
        {
            _rows.Clear();
            bool wOn = _settings.EffectiveShowWeather;
            bool wManual = _settings.EffectiveWeatherMode == "manual";
            bool cOn = _settings.EffectiveShowClock;
            bool dOn = cOn && _settings.EffectiveShowDate;

            _rows.Add(new SettingsRow(SettingsItemId.None, SettingsItemKind.Header, null, Localization.Ui("sec.weather"), "", false));
            _rows.Add(new SettingsRow(SettingsItemId.ShowWeather, SettingsItemKind.Toggle, "cloud-sun", Localization.Ui("item.show_weather"), "", true, false, wOn));
            _rows.Add(new SettingsRow(SettingsItemId.WeatherMode, SettingsItemKind.Cycle, "compass", Localization.Ui("item.weather_mode"), Localization.Ui(wManual ? "val.manual" : "val.auto_ip"), wOn));
            if (wManual)
            {
                string cityVal = !string.IsNullOrWhiteSpace(_settings.LocationName)
                    ? Weather.TruncateName(_settings.LocationName.Trim(), 16)
                    : Localization.Ui("val.choose");
                _rows.Add(new SettingsRow(SettingsItemId.CitySearch, SettingsItemKind.Action, "magnifying-glass", Localization.Ui("item.city_search"), cityVal, wOn, true));
            }
            _rows.Add(new SettingsRow(SettingsItemId.WeatherShowLocation, SettingsItemKind.Toggle, "map-pin", Localization.Ui("item.weather_show_location"), "", wOn, false, _settings.EffectiveWeatherShowLocation));
            _rows.Add(new SettingsRow(SettingsItemId.WeatherShowMinMax, SettingsItemKind.Toggle, "arrows-down-up", Localization.Ui("item.weather_show_minmax"), "", wOn, false, _settings.EffectiveWeatherShowMinMax));
            string unitVal = _settings.Units == 'c' ? "\u00b0C" : (_settings.Units == 'f' ? "\u00b0F" : Localization.Ui("val.auto"));
            _rows.Add(new SettingsRow(SettingsItemId.Units, SettingsItemKind.Cycle, "thermometer", Localization.Ui("item.units"), unitVal, wOn));

            _rows.Add(new SettingsRow(SettingsItemId.None, SettingsItemKind.Header, null, Localization.Ui("sec.clock"), "", false));
            _rows.Add(new SettingsRow(SettingsItemId.ShowClock, SettingsItemKind.Toggle, "clock", Localization.Ui("item.show_clock"), "", true, false, cOn));
            string clockVal = _settings.Clock == 12 ? Localization.Ui("val.12h") : (_settings.Clock == 24 ? Localization.Ui("val.24h") : Localization.Ui("val.auto"));
            _rows.Add(new SettingsRow(SettingsItemId.ClockFormat, SettingsItemKind.Cycle, "timer", Localization.Ui("item.clock_format"), clockVal, cOn));
            _rows.Add(new SettingsRow(SettingsItemId.ShowDate, SettingsItemKind.Toggle, "calendar", Localization.Ui("item.show_date"), "", cOn, false, _settings.EffectiveShowDate));
            _rows.Add(new SettingsRow(SettingsItemId.DateFormat, SettingsItemKind.Cycle, "calendar-dots", Localization.Ui("item.date_format"), DateFormatLabel(_settings.EffectiveDateFormat), dOn));

            _rows.Add(new SettingsRow(SettingsItemId.None, SettingsItemKind.Header, null, Localization.Ui("sec.sound"), "", false));
            bool snd = _settings.EffectiveSoundEnabled;
            _rows.Add(new SettingsRow(SettingsItemId.SoundEnabled, SettingsItemKind.Toggle, snd ? "speaker-high" : "speaker-slash", Localization.Ui("item.sound"), "", true, false, snd));

            if (focusId != SettingsItemId.None)
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_rows[i].Id == focusId && _rows[i].Enabled) { FocusIndex = i; EnsureVisible(); return; }
                }
            }
            if (FocusIndex < 0 || FocusIndex >= _rows.Count || _rows[FocusIndex].Kind == SettingsItemKind.Header || !_rows[FocusIndex].Enabled)
                FocusIndex = FirstSelectableRow();
            EnsureVisible();
        }

        private static string DateFormatLabel(string fmt)
        {
            switch (fmt)
            {
                case "short": return Localization.Ui("val.df_short");
                case "numeric": return Localization.Ui("val.df_numeric");
                case "iso": return Localization.Ui("val.df_iso");
                default: return Localization.Ui("val.df_long");
            }
        }

        private int FirstSelectableRow()
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Kind != SettingsItemKind.Header && _rows[i].Enabled) return i;
            return 0;
        }

        private void EnsureVisible()
        {
            if (FocusIndex < ScrollOffset) ScrollOffset = FocusIndex;
            if (ScrollOffset > 0 && FocusIndex > 0 && FocusIndex == ScrollOffset && _rows[FocusIndex - 1].Kind == SettingsItemKind.Header)
                ScrollOffset--;
            if (FocusIndex >= ScrollOffset + MaxVisibleRows)
                ScrollOffset = FocusIndex - MaxVisibleRows + 1;
            int maxScroll = Math.Max(0, _rows.Count - MaxVisibleRows);
            if (ScrollOffset > maxScroll) ScrollOffset = maxScroll;
            if (ScrollOffset < 0) ScrollOffset = 0;
        }
    }
}
