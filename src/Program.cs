using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core;
using Launcher.Host;
using Launcher.Native;
using Launcher.Ui;

// Native Samsung TV launcher: a glass dock over the running app, drawn with EFL through
// P/Invoke and started by the system's signed `dotnet` (the TV refuses unsigned ELF binaries).
namespace Launcher
{
    internal static class Program
    {
        private const int NotificationLevelTop = 40;
        private const int WindowLayer = 900;
        private const int DefaultScreenW = 1920, DefaultScreenH = 1080;
        private const double HomeEchoSeconds = 1.5;
        private const double SettingsIdleSeconds = 60.0;

        // Delegates handed to native code live in static fields so the GC can never collect them.
        private static readonly Efl.EvasCb KeyCb = OnKey;
        private static readonly Efl.EvasCb KeyUpCb = OnKeyUp;
        private static readonly Efl.TaskCb AnimCb = OnFrame;
        private static readonly Efl.TaskCb IdleCb = OnIdle;
        private static readonly Efl.FdCb FifoCb = OnFifo;
        private static readonly Efl.TaskCb ClockCb = OnClock;
        private static readonly Efl.TaskCb WeatherCb = OnWeather;
        private static readonly Efl.TaskCb WeatherSoonCb = OnWeatherSoon;
        private static readonly Efl.TaskCb FpsCb = OnFps;
        private static readonly Efl.TaskCb PremapCb = OnPremap;
        private static readonly Efl.TaskCb ReactivateCb = OnReactivate;
        private static readonly Efl.TaskCb PollAsyncCb = OnPollAsync;
        private static readonly Efl.TaskCb BackCb = OnBackTick;
        private static readonly Efl.EventCb EcoreKeyCb = OnEcoreKey;

        private static readonly CommandBuffer Commands = new CommandBuffer();
        private static readonly NavigationInput Navigation = new NavigationInput();
        private static readonly BackRelease BackUp = new BackRelease();
        private static readonly AppLauncher LauncherSvc = new AppLauncher();
        private static readonly SonosClient Sonos = new SonosClient();
        private static readonly byte[] ReadBuf = new byte[128];
        private static readonly byte[] SmackStar = { (byte)'*' };
        private const int KeygrabExclusive = 1 << 10;
        private const int RawBackCode = 1, Raw123Code = 140;
        private const string KeyTool = "/usr/bin/input_keyevent ";

        private static InputWatcher _input;
        private static string _dir, _pidPath, _fifoPath, _confPath, _claimPath, _resDir;
        private static IntPtr _win, _anim, _idle, _fifoHandler, _clockTimer, _weatherTimer, _weatherSoonTimer, _premapTimer, _reactivateTimer, _pollTimer, _backTimer;
        private static int _reactivateLeft;
        private static int _fifoFd = -1;
        private static double _lastFrame;
        private static int _screenW = DefaultScreenW, _screenH = DefaultScreenH;
        private static AppEntry[] _apps, _allApps, _inputs;
        private static Controller _ctl, _appsCtl, _allCtl, _inputsCtl;
        private static Dock _dock, _appsDock, _allDock, _inputsDock;
        private static AppEntry _pendingLaunch;
        private static AppEntry _homeLaunch;
        private static AppEntry _tvSettingsEntry, _tvStoreEntry, _samsungAppsEntry, _vetroEntry;
        private static Settings _cliEnvSettings, _fileSettings, _effectiveSettings;
        private static string _tvLanguage, _tvCountry;
        private static SettingsController _settingsCtl;
        private static SettingsModal _settingsModal;
        private static VolumeEdge _volumeEdge = VolumeEdge.Left;
        private static VolumePill _pill;
        private static NumberPad _pad;
        private static bool _padFromDock;
        private static int _trackedVolume = -1;
        private static int _levelBeforeMute;
        private static bool _virtualMuted;
        private static bool _nightMode;
        private static bool _voiceMode;
        private static double _lastFifoVolumeAt = -100;
        private static double _lastRawVolumeAt = -100;
        private static double _lastSonosReadAt = -100;
        private static bool _claimHome, _claimTried, _homeClaimed;
        private static bool _dockMapped;
        private static GeoSearchOutcome _pendingCityOutcome;
        private static int _citySearchGen;
        private static int _pendingCityGen;
        private static Task _weatherTask;

        // --perf: frame-time probe (off by default)
        private static bool _perf;
        private static string _perfWhy = "?";
        private static int _perfN;
        private static readonly double[] PerfDt = new double[2048], PerfWork = new double[2048];
        private static int _vblankFps;

        // ------------------------------------------------------------------ setup

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        private static bool Flag(string[] args, string name) { return Array.IndexOf(args, name) >= 0; }

        private static string ResolveDir(string[] args)
        {
            string d = Arg(args, "--dir") ?? Environment.GetEnvironmentVariable("TVLAUNCHER_DIR") ?? "/home/owner";
            return Directory.Exists(d) ? d : Path.GetTempPath();
        }

        /// <summary>
        /// Environment for EFL. Must go through libc setenv: on Unix .NET keeps its own copy of
        /// Environment.SetEnvironmentVariable and native getenv never sees it.
        /// </summary>
        private static string SetupEnvironment()
        {
            string want = (Environment.GetEnvironmentVariable("TVLAUNCHER_ENGINE") ?? "").ToLowerInvariant();
            bool egl = want == "egl" || (want != "shm" && Directory.Exists("/dev/dri"));

            Libc.setenv("ELM_ENGINE", "gl", 1);
            Libc.setenv("ECORE_EVAS_ENGINE", egl ? "wayland_egl" : "wayland_shm", 1);

            string uid = Libc.getuid().ToString();
            string display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
            string runtime = null;
            foreach (string c in new[] { Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), "/run/user/" + uid, "/run", "/run/user/5001" })
                if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, display))) { runtime = c; break; }
            if (runtime == null) runtime = Directory.Exists("/run/user/" + uid) ? "/run/user/" + uid : "/run";
            Libc.setenv("XDG_RUNTIME_DIR", runtime, 1);
            if (File.Exists("/run/pulse/native"))
            {
                Libc.setenv("PULSE_RUNTIME_PATH", "/run/pulse", 0);
                Libc.setenv("PULSE_PROP", "media.role=system", 0);
            }
            Libc.setenv("WAYLAND_DISPLAY", "wayland-0", 0);
            Libc.setenv("ELM_PROFILE", "tv", 0);
            Libc.setenv("ELM_DISPLAY", "wl", 0);
            Libc.setenv("EVAS_FONT_DPI", "72", 0);
            if (egl)
            {
                Libc.setenv("ELM_ACCEL", "gl", 0);
                Libc.setenv("EVAS_GL_NO_BLACKLIST", "1", 0);
                Libc.setenv("ECORE_EVAS_COMP_NOSYNC", "1", 0);
                Libc.setenv("ECORE_VSYNC_DRM_ALL", "1", 0);
                Libc.setenv("ECORE_EVAS_GL_SYNC_DRAW_DONE", "1", 0);
                Libc.setenv("EVAS_GL_PARTIAL_DISABLE", "1", 0);
            }
            return egl ? "wayland_egl" : "wayland_shm";
        }

        private static bool ProcessLooksLikeUs(int pid)
        {
            try { return File.ReadAllText("/proc/" + pid + "/cmdline").Contains("tvlauncher-dotnet"); }
            catch (Exception) { return false; }
        }

        private static bool AcquireInstance()
        {
            try
            {
                int other;
                if (File.Exists(_pidPath) && int.TryParse(File.ReadAllText(_pidPath).Trim(), out other)
                    && other != Libc.getpid() && Libc.kill(other, 0) == 0 && ProcessLooksLikeUs(other))
                {
                    SendCommand("show");
                    return false;
                }
                File.WriteAllText(_pidPath, Libc.getpid().ToString());
            }
            catch (Exception ex) { Logger.Write("pidfile: " + ex.Message); }
            return true;
        }

        private static void SendCommand(string word)
        {
            int fd = Libc.open(_fifoPath, Libc.O_WRONLY | Libc.O_NONBLOCK);
            if (fd < 0) return;
            byte[] b = System.Text.Encoding.ASCII.GetBytes(word + "\n");
            Libc.write(fd, b, (UIntPtr)b.Length);
            Libc.close(fd);
        }

        private static void CleanupFiles()
        {
            try
            {
                OverlayFlag.Mark(OverlayFlag.DockPath, false);
                OverlayFlag.Mark(OverlayFlag.PadPath, false);
                OverlayFlag.Mark(OverlayFlag.VolumePath, false);
                int pid;
                if (File.Exists(_pidPath) && int.TryParse(File.ReadAllText(_pidPath).Trim(), out pid) && pid == Libc.getpid())
                {
                    File.Delete(_pidPath);
                    Libc.unlink(_fifoPath);
                    if (_claimPath != null) Libc.unlink(_claimPath);
                }
            }
            catch (Exception) { }
        }

        private static void SetupFifo()
        {
            Libc.unlink(_fifoPath);
            if (Libc.mkfifo(_fifoPath, 438u) != 0) { Logger.Write("mkfifo failed: " + Marshal.GetLastWin32Error()); return; }
            Libc.chmod(_fifoPath, 438u);
            try { Libc.setxattr(_fifoPath, "security.SMACK64", SmackStar, (UIntPtr)SmackStar.Length, 0); }
            catch (Exception) { }
            _fifoFd = Libc.open(_fifoPath, Libc.O_RDWR | Libc.O_NONBLOCK);
            if (_fifoFd < 0) { Logger.Write("open fifo failed: " + Marshal.GetLastWin32Error()); return; }
            _fifoHandler = Efl.ecore_main_fd_handler_add(_fifoFd, Efl.EcoreFdRead, FifoCb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        private static AppEntry[] LoadApps(string path, out AppEntry[] raw)
        {
            raw = LoadList(path, Catalog.BuiltIn);
            return Catalog.ArrangeBar(raw);
        }

        private static AppEntry[] LoadAllApps(string path, AppEntry[] mainBarApps)
        {
            AppEntry[] rawAll = File.Exists(path) ? LoadList(path, Catalog.BuiltInAllApps) : null;
            return Catalog.ArrangeAllApps(rawAll, mainBarApps);
        }

        private static string FindInstalledApp(params string[] candidates)
        {
            foreach (string id in candidates)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (Directory.Exists("/usr/apps/" + id) || Directory.Exists("/opt/usr/apps/" + id))
                    return id;
                int dot = id.IndexOf('.');
                if (dot > 0 && id.IndexOf('.', dot + 1) < 0)
                {
                    string pkg = id.Substring(0, dot);
                    if (Directory.Exists("/usr/apps/" + pkg) || Directory.Exists("/opt/usr/apps/" + pkg))
                        return id;
                }
            }
            return null;
        }

        private static void ConfigureShortcuts(AppEntry[] rawApps)
        {
            AppEntry origSettings = null;
            if (rawApps != null)
            {
                foreach (AppEntry e in rawApps)
                    if (e != null && e.Slug == "settings") { origSettings = e; break; }
            }
            if (origSettings != null)
            {
                _tvSettingsEntry = new AppEntry(
                    "tvsettings", origSettings.Label, origSettings.AppId, origSettings.FallbackAppId,
                    origSettings.Key, origSettings.SourceType, origSettings.Panel,
                    origSettings.SystemApp, origSettings.IconPath, origSettings.KillFirst);
            }
            else
            {
                _tvSettingsEntry = new AppEntry("tvsettings", "TV Settings", "org.tizen.menu", "com.samsung.tv.setting", "XF86SysMenu");
            }

            string storeId = FindInstalledApp(
                _effectiveSettings != null ? _effectiveSettings.TvStoreApp : null,
                "A2STVStore.Store", "com.samsung.tv.store", "org.tizen.samsung-apps", "com.samsung.tv.apps", "com.samsung.tv.webappstore");
            _tvStoreEntry = new AppEntry("tvstore", "TV-Store", storeId ?? "A2STVStore.Store", "com.samsung.tv.store");

            string appsId = FindInstalledApp("com.samsung.tv.store", "com.samsung.tv.coba.apps", "org.volt.apps", "org.tizen.samsung-apps");
            _samsungAppsEntry = new AppEntry("samsungapps", "Apps", appsId ?? "com.samsung.tv.store", "com.samsung.tv.coba.apps");

            string vetroId = FindInstalledApp("vetrotvapp.app");
            _vetroEntry = new AppEntry("vetro", "Vetro", vetroId ?? "vetrotvapp.app", null);
        }

        private static AppEntry[] LoadList(string path, Func<AppEntry[]> builtIn)
        {
            try
            {
                if (File.Exists(path))
                {
                    CatalogResult r = Catalog.Parse(File.ReadAllText(path));
                    foreach (string p in r.Problems) Logger.Write("apps: " + p);
                    if (r.Entries.Length > 0) return r.Entries;
                    Logger.Write("apps: " + path + " has no usable entries");
                }
                else Logger.Write("apps: " + path + " not found, using the built-in list");
            }
            catch (Exception ex) { Logger.Write("apps: " + ex.Message); }
            return builtIn();
        }

        // ------------------------------------------------------------------ show / hide

        private static void ShowDock()
        {
            if (_homeLaunch != null) return;
            StopBackWait();
            ClosePad();
            if (_pill != null) _pill.ReleaseModeKeys();
            _pendingLaunch = null;
            LauncherSvc.CancelPendingKey();
            if (_ctl != _appsCtl && !_ctl.IsActive) ActivatePage(_appsCtl, _appsDock);
            if (!_ctl.IsActive) _ctl.SetFocus(0);
            if (!_ctl.Show()) { ResetIdle(); return; }

            _dockMapped = true;
            OverlayFlag.Mark(OverlayFlag.DockPath, true, Logger.Write);
            SoundPlayer.Play(SoundEffect.Open);
            _dock.Apply(_ctl);
            Efl.evas_object_show(_win);
            Efl.elm_win_raise(_win);
            Efl.elm_win_activate(_win);
            TryClaimHome();
            ScheduleReactivate();
            StartClock();
            StartAnimator();
            ResetIdle();
        }

        private static void ScheduleReactivate()
        {
            _reactivateLeft = 4;
            if (_reactivateTimer == IntPtr.Zero)
                _reactivateTimer = Efl.ecore_timer_add(0.16, ReactivateCb, IntPtr.Zero);
        }

        private static byte OnReactivate(IntPtr data)
        {
            try
            {
                if (_settingsCtl != null && _settingsCtl.IsActive && _reactivateLeft > 0)
                {
                    _reactivateLeft--;
                    Efl.elm_win_activate(_settingsModal.Win);
                    if (_reactivateLeft > 0) return 1;
                }
                else if (_ctl != null && _ctl.IsActive && _reactivateLeft > 0)
                {
                    _reactivateLeft--;
                    Efl.elm_win_activate(_win);
                    if (_reactivateLeft > 0) return 1;
                }
            }
            catch (Exception) { }
            _reactivateTimer = IntPtr.Zero;
            return 0;
        }

        private static void HideDock(bool playSound = true)
        {
            StopBackWait();
            _pendingLaunch = null;
            _reactivateLeft = 0;
            if (_reactivateTimer != IntPtr.Zero)
            {
                Efl.ecore_timer_del(_reactivateTimer);
                _reactivateTimer = IntPtr.Zero;
            }
            CancelIdle();
            StopClock();
            if (_settingsCtl != null && (_settingsCtl.IsActive || _settingsCtl.Phase == Phase.Showing))
            {
                _settingsCtl.Hide();
                StartAnimator();
            }
            if (_ctl.Hide())
            {
                if (playSound) SoundPlayer.Play(SoundEffect.Close);
                StartAnimator();
            }
        }

        private static void OpenSettings()
        {
            if (_settingsCtl == null || _settingsModal == null) return;
            int sourceType = -1;
            Efl.vconf_get_int("memory/system/source_type", out sourceType);
            _settingsCtl.ActiveSourceType = sourceType;

            if (_settingsCtl.Show())
            {
                SoundPlayer.Play(SoundEffect.Open);
                _settingsModal.ShowWindow();
                _settingsModal.Apply(_settingsCtl, _inputs);
                StartAnimator();
                ResetIdle(SettingsIdleSeconds);
            }
        }

        private static void CloseSettings(bool playSound = true)
        {
            if (_settingsCtl == null || !_settingsCtl.IsActive) return;
            if (_settingsCtl.Hide())
            {
                if (playSound) SoundPlayer.Play(SoundEffect.Close);
                StartAnimator();
            }
            if (_ctl != null && _ctl.Phase == Phase.Shown)
            {
                Efl.elm_win_activate(_win);
                ResetIdle(Controller.IdleHideSeconds);
            }
        }

        // ------------------------------------------------------------------ pages

        private static void ActivatePage(Controller ctl, Dock dock)
        {
            if (ctl == _ctl) return;
            _dock.SetVisible(false);
            dock.SetVisible(true);
            _ctl = ctl;
            _dock = dock;
        }

        private static void SelectPage(Controller ctl, Dock dock)
        {
            if (ctl == _ctl) return;
            ctl.AdoptVisibility(_ctl, 0.55f);
            ActivatePage(ctl, dock);
            _dock.Apply(_ctl);
            StartAnimator();
            ResetIdle();
        }

        private static void OpenAllApps()
        {
            if (_allCtl == null || _allDock == null) return;
            SoundPlayer.Play(SoundEffect.Move);
            _allCtl.SetFocus(0);
            Logger.Write("all apps page, " + _allApps.Length + " entries");
            SelectPage(_allCtl, _allDock);
        }

        private static void OpenInputs()
        {
            int current = -1, type;
            if (Efl.vconf_get_int("memory/system/source_type", out type) == 0)
                for (int i = 0; i < _inputs.Length; i++) if (_inputs[i].SourceType == type) { current = i; break; }
            _inputsDock.CurrentIndex = current;
            _inputsCtl.SetFocus(current >= 0 ? current : 0);
            Logger.Write("inputs panel, source_type=" + type + " current=" + current);
            SelectPage(_inputsCtl, _inputsDock);
        }

        // ------------------------------------------------------------------ clock / weather

        private static string ClockText() { return Localization.ClockText(DateTime.Now); }

        private static string DayText() { return Localization.DayText(DateTime.Now); }

        /// <summary>Language, region and clock format: command line, then environment, then vetro-bar.conf, then the TV's own settings.</summary>
        private static void SetupLocalization(string[] args)
        {
            _cliEnvSettings = new Settings();
            string[] keys = { "language", "location", "clock", "units", "sound" };
            string[] flags = { "--lang", "--location", "--clock", "--units", "--sound" };
            string[] envs = { "TVLAUNCHER_LANG", "TVLAUNCHER_LOCATION", "TVLAUNCHER_CLOCK", "TVLAUNCHER_UNITS", "TVLAUNCHER_SOUND" };
            for (int i = 0; i < keys.Length; i++)
            {
                string v = Arg(args, flags[i]) ?? Environment.GetEnvironmentVariable(envs[i]);
                if (v != null && !_cliEnvSettings.Set(keys[i], v)) Logger.Write("settings: ignored " + keys[i] + "=" + v);
            }

            _confPath = Path.Combine(Weather.CacheDir, "vetro-bar.conf");
            _fileSettings = new Settings();
            try
            {
                if (File.Exists(_confPath)) _fileSettings = Settings.Parse(File.ReadAllText(_confPath));
            }
            catch (Exception ex) { Logger.Write("settings: " + ex.Message); }

            _tvLanguage = TvString("db/menu_widget/language") ?? Environment.GetEnvironmentVariable("LANG");
            _tvCountry = TvString("db/comss/countrycode");
            ApplyEffectiveSettings();
            Logger.Write("locale: language=" + Localization.Language + " country=" + (Localization.Country ?? "?")
                + " clock=" + (Localization.Is12Hour ? "12h" : "24h") + " units=" + (Localization.Fahrenheit ? "F" : "C")
                + " weather=" + (_effectiveSettings.EffectiveShowWeather ? _effectiveSettings.EffectiveWeatherMode : "off"));
        }

        private static void ApplyEffectiveSettings()
        {
            _effectiveSettings = _cliEnvSettings.Clone();
            _effectiveSettings.Fill(_fileSettings);
            Localization.Configure(_tvLanguage, _tvCountry, _effectiveSettings);
            Weather.Configure(_effectiveSettings);
            Weather.RebuildLabels();
        }

        private static string TvString(string key)
        {
            try
            {
                IntPtr p = Efl.vconf_get_str(key);
                return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
            }
            catch (Exception) { return null; }
        }

        private static void StartClock()
        {
            TimeZoneInfo.ClearCachedData();   // the TV's time zone or daylight saving may have changed while the dock slept
            string t = ClockText();
            string day = DayText();
            _appsDock.SetClock(t);
            if (_allDock != null) _allDock.SetClock(t);
            _inputsDock.SetClock(t);
            _appsDock.SetDay(day);
            if (_allDock != null) _allDock.SetDay(day);
            _inputsDock.SetDay(day);
            if (_clockTimer != IntPtr.Zero) Efl.ecore_timer_del(_clockTimer);
            DateTime now = DateTime.Now;
            double wait = 60.0 - now.Second - now.Millisecond / 1000.0 + 0.05;
            _clockTimer = Efl.ecore_timer_add(wait, ClockCb, IntPtr.Zero);
            if (Design.SideChrome)
            {
                if (_weatherSoonTimer != IntPtr.Zero) Efl.ecore_timer_del(_weatherSoonTimer);
                _weatherSoonTimer = Efl.ecore_timer_add(2.0, WeatherSoonCb, IntPtr.Zero);
                if (_weatherTimer == IntPtr.Zero)
                    _weatherTimer = Efl.ecore_timer_add(Design.WeatherSeconds, WeatherCb, IntPtr.Zero);
            }
        }

        private static void StopClock()
        {
            if (_clockTimer != IntPtr.Zero)
            {
                Efl.ecore_timer_del(_clockTimer);
                _clockTimer = IntPtr.Zero;
            }
            if (_weatherSoonTimer != IntPtr.Zero)
            {
                Efl.ecore_timer_del(_weatherSoonTimer);
                _weatherSoonTimer = IntPtr.Zero;
            }
        }

        private static byte OnWeatherSoon(IntPtr data)
        {
            _weatherSoonTimer = IntPtr.Zero;
            try { if (_ctl != null && _ctl.IsActive && _dock != null) _dock.Apply(_ctl); }
            catch (Exception ex) { Logger.Write("weather: " + ex.Message); }
            return 0;
        }

        private static byte OnWeather(IntPtr data)
        {
            try
            {
                Weather.Refresh();
                if (_ctl != null && _ctl.IsActive && _dock != null) _dock.Apply(_ctl);
            }
            catch (Exception ex) { Logger.Write("weather: " + ex.Message); }
            return 1;
        }

        private static byte OnClock(IntPtr data)
        {
            _clockTimer = IntPtr.Zero;
            try { if (_ctl.IsActive) { StartClock(); _dock.Apply(_ctl); } }
            catch (Exception ex) { Logger.Write("clock: " + ex.Message); }
            return 0;
        }

        private static void StartAnimator()
        {
            if (_anim != IntPtr.Zero) return;
            _lastFrame = Efl.ecore_time_get() - 1.0 / 60.0;
            _anim = Efl.ecore_animator_add(AnimCb, IntPtr.Zero);
        }

        private static void ResetIdle()
        {
            ResetIdle(_settingsCtl != null && _settingsCtl.IsActive ? SettingsIdleSeconds : Controller.IdleHideSeconds);
        }

        private static void ResetIdle(double seconds)
        {
            if (_idle != IntPtr.Zero) Efl.ecore_timer_del(_idle);
            _idle = Efl.ecore_timer_add(seconds, IdleCb, IntPtr.Zero);
        }

        private static void CancelIdle()
        {
            if (_idle == IntPtr.Zero) return;
            Efl.ecore_timer_del(_idle);
            _idle = IntPtr.Zero;
        }

        // ------------------------------------------------------------------ callbacks

        private static byte OnFrame(IntPtr data)
        {
            try
            {
                long t0 = _perf ? Stopwatch.GetTimestamp() : 0;
                double now = Efl.ecore_time_get();
                double dt = now - _lastFrame;
                bool more = _ctl.Tick(dt);
                bool moreSet = _settingsCtl != null && _settingsCtl.Tick(dt);
                _lastFrame = now;
                _dock.Apply(_ctl);
                if (_settingsCtl != null && _settingsModal != null)
                    _settingsModal.Apply(_settingsCtl, _inputs);
                if (_ctl.Phase == Phase.Hidden)
                {
                    Efl.evas_object_hide(_win);
                    if (_dockMapped)
                    {
                        _dockMapped = false;
                        OverlayFlag.Mark(OverlayFlag.DockPath, false, Logger.Write);
                    }
                    AppEntry pending = _pendingLaunch;
                    _pendingLaunch = null;
                    if (pending != null) LauncherSvc.Execute(pending);
                    else if (_pad == null || !_pad.IsOnScreen) LauncherSvc.ResumeTuner("dock closed", false);
                }
                if (_perf) PerfSample(dt, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, !(more || moreSet));
                if (more || moreSet) return 1;
            }
            catch (Exception ex) { Logger.Write("frame: " + ex); }
            _anim = IntPtr.Zero;
            return 0;
        }

        private static void PerfSample(double dt, double workMs, bool last)
        {
            if (_perfN < PerfDt.Length) { PerfDt[_perfN] = dt * 1000.0; PerfWork[_perfN] = workMs; _perfN++; }
            if (!last) return;
            double min = double.MaxValue, max = 0, sum = 0, wmax = 0, wsum = 0; int over = 0, n = 0, worst = 0;
            for (int i = 0; i < _perfN; i++)
            {
                if (PerfWork[i] > wmax) wmax = PerfWork[i];
                wsum += PerfWork[i];
                if (i == 0) continue;
                double d = PerfDt[i];
                if (d < min) min = d;
                if (d > max) { max = d; worst = i; }
                if (d > 30.0) over++;
                sum += d; n++;
            }
            Logger.Write(string.Format(CultureInfo.InvariantCulture,
                "perf {0}: frames={1} interval ms min/avg/max={2:0.0}/{3:0.0}/{4:0.0} over30ms={5} (worst at frame {8}) work ms avg/max={6:0.00}/{7:0.00}",
                _perfWhy, _perfN, n > 0 ? min : 0, n > 0 ? sum / n : 0, max, over, _perfN > 0 ? wsum / _perfN : 0, wmax, worst));
            _perfN = 0;
        }

        private static byte OnFps(IntPtr data)
        {
            try
            {
                int rc = Libc.system("/usr/bin/tdm-monitor -vblank_fps " + Libc.getpid() + "@" + _vblankFps + " >/dev/null 2>&1");
                Logger.Write("vblank " + _vblankFps + " fps for this process, rc=" + rc);
            }
            catch (Exception ex) { Logger.Write("vblank fps: " + ex.Message); }
            return 0;
        }

        private static byte OnPremap(IntPtr data)
        {
            _premapTimer = IntPtr.Zero;
            try
            {
                // Claim XF86Home while the window surface is mapped during invisible startup pre-map (#282 fix).
                TryClaimHome();
                if (_ctl.Phase == Phase.Hidden)
                {
                    _ctl.Reset();
                    _dock.Apply(_ctl);
                    Efl.evas_object_hide(_win);
                }
                Logger.Write("window pre-mapped and released");
            }
            catch (Exception ex) { Logger.Write("premap: " + ex.Message); }
            return 0;
        }

        private static byte OnIdle(IntPtr data)
        {
            _idle = IntPtr.Zero;
            try
            {
                HideDock();
            }
            catch (Exception ex) { Logger.Write("idle: " + ex); }
            return 0;
        }

        private static void OnKey(IntPtr data, IntPtr evas, IntPtr obj, IntPtr eventInfo)
        {
            try
            {
                if (_pad != null && _pad.IsOnScreen) return;
                string key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(eventInfo));
                if (Navigation.IsEcho(key, Efl.ecore_time_get())) return;
                if (_settingsCtl != null && _settingsCtl.IsActive) HandleSettingsKey(key);
                else HandleKey(key);
            }
            catch (Exception ex) { Logger.Write("key: " + ex); }
        }

        private static void OnKeyUp(IntPtr data, IntPtr evas, IntPtr obj, IntPtr eventInfo)
        {
            try
            {
                if (_pad != null && _pad.IsOnScreen) return;
                string key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(eventInfo));
                if (BackUp.WindowRelease(key)) { StopBackWait(); HideDock(); }
            }
            catch (Exception ex) { Logger.Write("key up: " + ex); }
        }

        private static void OnRawRelease(int code)
        {
            try
            {
                if (_pad != null && _pad.IsOnScreen)
                {
                    _pad.HandleRawRelease(code);
                    return;
                }
                if (code == RawBackCode || code == 158 || code == 174)
                    BackUp.KernelRelease(Efl.ecore_time_get());
            }
            catch (Exception ex) { Logger.Write("raw release: " + ex); }
        }

        private static byte OnBackTick(IntPtr data)
        {
            if (!BackUp.Pending) { _backTimer = IntPtr.Zero; return 0; }
            if (!BackUp.Due(Efl.ecore_time_get())) return 1;
            _backTimer = IntPtr.Zero;
            HideDock();
            return 0;
        }

        private static void StopBackWait()
        {
            BackUp.Cancel();
            if (_backTimer != IntPtr.Zero) { Efl.ecore_timer_del(_backTimer); _backTimer = IntPtr.Zero; }
        }

        private static void ClosePad() { if (_pad != null && _pad.IsShown) _pad.Close(); }

        private static void TogglePad()
        {
            if (_pad == null)
                _pad = new NumberPad(_resDir, Logger.Write, _screenW, _screenH,
                    delegate (PadResult kind, string what)
                    {
                        string cmd = kind == PadResult.Send ? NumberPadModel.SendCommand(what, KeyTool) : NumberPadModel.SendKeyCommand(what, KeyTool);
                        Logger.Write("pad: sending " + what + " rc=" + Libc.system(cmd));
                    },
                    delegate (bool sent)
                    {
                        bool fromDock = _padFromDock;
                        _padFromDock = false;
                        if (fromDock && !sent && !_dockMapped) LauncherSvc.ResumeTuner("osd 123 closed after the dock");
                    });
            bool opening = !_pad.IsShown;
            if (opening)
            {
                _padFromDock = _dockMapped;
                if (_pill != null) _pill.ReleaseModeKeys();
            }
            _pad.Toggle();
        }

        private static void OnRawHome()
        {
            _perfWhy = "home";
            if (_homeLaunch != null) { LauncherSvc.LaunchApp(_homeLaunch); LauncherSvc.ArmGuard(); return; }
            if (_settingsCtl != null && _settingsCtl.IsActive)
            {
                CloseSettings(playSound: false);
                HideDock();
                LauncherSvc.ArmGuard();
                return;
            }
            if (_ctl.Phase == Phase.Showing || _ctl.Phase == Phase.Hiding)
            {
                OnRawHomeBurst();
                return;
            }
            if (_ctl.IsActive) HideDock(); else ShowDock();
            LauncherSvc.ArmGuard();
        }

        private static void OnRawHomeBurst()
        {
            if (_homeLaunch != null) { LauncherSvc.ArmGuard(); return; }
            if (_settingsCtl != null && _settingsCtl.IsActive)
            {
                _settingsModal.ShowWindow();
                ScheduleReactivate();
                ResetIdle(SettingsIdleSeconds);
            }
            else if (_ctl.IsActive)
            {
                Efl.evas_object_show(_win);
                Efl.elm_win_activate(_win);
                ScheduleReactivate();
                ResetIdle();
            }
            LauncherSvc.ArmGuard();
        }

        private static void OnRawNav(int code)
        {
            // Fallback for volume keys (68=VolUp, 67=VolDown, 66=VolMute) when a Bluetooth remote reconnects
            // on a new UHID evdev node (e.g. event17) that Vetro's input bridge (stuck on event5) is not grabbing.
            if (code == 68 || code == 67 || code == 66)
            {
                double now = Efl.ecore_time_get();
                if (now - _lastFifoVolumeAt > 0.35 && now - _lastRawVolumeAt > 0.08)
                {
                    _lastRawVolumeAt = now;
                    Command cmd = code == 68 ? Command.VolUp : (code == 67 ? Command.VolDown : Command.VolMute);
                    VolumeKey(cmd, fromFifo: false);
                }
                return;
            }
            if (_pad != null && _pad.IsOnScreen)
            {
                _pad.HandleRawInput(code);
                return;
            }
            bool anyActive = _ctl.IsActive || (_settingsCtl != null && _settingsCtl.IsActive);
            if (!anyActive && _pill != null && _pill.IsOnScreen && _pill.HasSonosModes && (code == 105 || code == 106))
            {
                _pill.HandleRawModeKey(code);
                return;
            }
            if (code == Raw123Code && anyActive)
            {
                Logger.Write("123 reached the TV: the dock steps aside");
                HideDock();
                return;
            }
            string key = Navigation.Press(code, anyActive, Efl.ecore_time_get());
            if (key != null)
            {
                if (_settingsCtl != null && _settingsCtl.IsActive) HandleSettingsKey(key);
                else HandleKey(key);
            }
        }

        private static AppEntry[] CurrentPageEntries()
        {
            if (_ctl == _allCtl) return _allApps;
            if (_ctl == _inputsCtl) return _inputs;
            return _apps;
        }

        private static void HandleKey(string key)
        {
            try
            {
                if (_dockMapped) OverlayFlag.Mark(OverlayFlag.DockPath, true);
                int index;
                switch (_ctl.Key(key, out index))
                {
                    case KeyAction.Moved:
                        SoundPlayer.Play(SoundEffect.Move);
                        _perfWhy = "move";
                        StartAnimator();
                        ResetIdle();
                        break;
                    case KeyAction.Launch:
                        AppEntry[] list = CurrentPageEntries();
                        if (index >= 0 && index < list.Length) Activate(list[index]);
                        break;
                    case KeyAction.OpenPanel:
                        AppEntry[] pList = CurrentPageEntries();
                        if (index >= 0 && index < pList.Length && pList[index].Panel == "allapps") OpenAllApps();
                        else OpenInputs();
                        break;
                    case KeyAction.Back:
                        SoundPlayer.Play(SoundEffect.Move);
                        SelectPage(_appsCtl, _appsDock);
                        break;
                    case KeyAction.Hide:
                        if (key == "XF86Home")
                        {
                            double now = Efl.ecore_time_get();
                            if (now - _input.LastRawHome < HomeEchoSeconds)
                            {
                                Logger.Write("home echo ignored " + ((now - _input.LastRawHome) * 1000).ToString("0") + " ms after the press");
                                if (_ctl.IsActive) Efl.elm_win_activate(_win);
                                break;
                            }
                            if (now - _input.LastHome < InputWatcher.HomeDebounceSeconds || _ctl.Phase == Phase.Showing || _ctl.Phase == Phase.Hiding)
                            {
                                _input.LastHome = now;
                                OnRawHomeBurst();
                                break;
                            }
                            _input.LastHome = now;
                            HideDock();
                            LauncherSvc.ArmGuard();
                            break;
                        }
                        if (BackUp.Press(key, Efl.ecore_time_get()))
                        {
                            if (_backTimer == IntPtr.Zero) _backTimer = Efl.ecore_timer_add(0.05, BackCb, IntPtr.Zero);
                        }
                        else HideDock();
                        break;
                }
            }
            catch (Exception ex) { Logger.Write("key: " + ex); }
        }

        private static byte OnFifo(IntPtr data, IntPtr handler)
        {
            try
            {
                for (int round = 0; round < 8; round++)
                {
                    long n = (long)Libc.read(_fifoFd, ReadBuf, (UIntPtr)ReadBuf.Length);
                    if (n <= 0) break;
                    foreach (Command c in Commands.Feed(ReadBuf, (int)n))
                    {
                        Logger.Write("command " + c);
                        switch (c)
                        {
                            case Command.Show: ClosePad(); _perfWhy = "fifo-show"; ShowDock(); break;
                            case Command.Hide: _perfWhy = "fifo-hide"; HideDock(); break;
                            case Command.Toggle: ClosePad(); _perfWhy = "fifo-toggle"; if (_ctl.IsActive) HideDock(); else ShowDock(); break;
                            case Command.VolUp:
                            case Command.VolDown:
                            case Command.VolMute:
                                _lastFifoVolumeAt = Efl.ecore_time_get();
                                VolumeKey(c, fromFifo: true);
                                break;
                            case Command.Pad: TogglePad(); break;
                            case Command.Rescan:
                                if (_input != null) _input.Rescan();
                                break;
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Write("fifo: " + ex); }
            return 1;
        }

        // ------------------------------------------------------------------ Volume bar & Home claim

        private const string VolumeAppIdKey = "db/volume/appid";
        private const string SamsungVolumeAppId = "org.tizen.tv-viewer";
        private const string SuppressedVolumeAppId = "org.vetro.noop";

        private static void ConfigureVolumeOsdSuppression()
        {
            try
            {
                IntPtr ptr = Efl.vconf_get_str(VolumeAppIdKey);
                string cur = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
                bool suppress = _volumeEdge != VolumeEdge.Hidden;
                string want = suppress ? SuppressedVolumeAppId : SamsungVolumeAppId;
                if (string.Equals(cur, want, StringComparison.Ordinal))
                {
                    Logger.Write("volume: CEC OSD appid already " + want);
                    return;
                }
                if (!suppress && !string.Equals(cur, SuppressedVolumeAppId, StringComparison.Ordinal))
                    return;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        int rc = Libc.system("/usr/bin/vconftool set -f -t string " + VolumeAppIdKey + " " + want
                            + " >/dev/null 2>&1 && /bin/systemctl restart hdmicec-service >/dev/null 2>&1");
                        Logger.Write("volume: CEC OSD appid " + (cur ?? "(null)") + " -> " + want + " rc=" + rc);
                    }
                    catch (Exception ex) { Logger.Write("volume: CEC OSD config error: " + ex.Message); }
                });
            }
            catch (Exception ex) { Logger.Write("volume: CEC OSD check unavailable (" + ex.GetType().Name + ")"); }
        }

        private static bool SamsungMuted()
        {
            int v;
            return Efl.vconf_get_int("memory/org.tizen.tv-viewer/mute_status", out v) == 0 && v != 0;
        }

        private static VolumePill EnsurePill()
        {
            if (_pill == null)
                _pill = new VolumePill(_resDir, Logger.Write, _screenW, _screenH, _volumeEdge, OnVolumeModeKey);
            return _pill;
        }

        private static bool AllowVolumeModeGrab()
        {
            bool dockOrSettings = (_ctl != null && _ctl.IsActive) || (_settingsCtl != null && _settingsCtl.IsActive);
            bool padOpen = _pad != null && _pad.IsOnScreen;
            return !dockOrSettings && !padOpen;
        }

        private static void OnVolumeModeKey(string key)
        {
            if (!Sonos.IsAvailable) return;
            if (key == "Left")
            {
                _nightMode = !_nightMode;
                Sonos.SetNightModeAsync(_nightMode);
                Logger.Write("sonos: NightMode=" + (_nightMode ? "on" : "off"));
            }
            else if (key == "Right")
            {
                _voiceMode = !_voiceMode;
                Sonos.SetDialogLevelAsync(_voiceMode);
                Logger.Write("sonos: DialogLevel=" + (_voiceMode ? "on" : "off"));
            }
            else return;

            SoundPlayer.Play(SoundEffect.Move);
            if (_volumeEdge == VolumeEdge.Hidden) return;
            EnsurePill().Show(_trackedVolume < 0 ? 20 : _trackedVolume, _virtualMuted, true, _nightMode, _voiceMode, AllowVolumeModeGrab());
        }

        private static void VolumeKey(Command key, bool fromFifo)
        {
            try
            {
                // Dismiss any open launcher overlay so focus moves straight to the volume bar & Sonos modes.
                if (_settingsCtl != null && _settingsCtl.IsActive) CloseSettings(playSound: false);
                if (_ctl != null && _ctl.IsActive) HideDock(playSound: false);
                ClosePad();

                int output;
                bool known = Sound.TryGetSpeakerSelection(out output);
                bool external = known && Sound.IsExternalOutput(output);

                if (external)
                {
                    if (Sonos.IsAvailable)
                    {
                        double now = Efl.ecore_time_get();
                        if (now - _lastSonosReadAt > 2.5 || _trackedVolume < 0)
                        {
                            int realVol;
                            if (Sonos.TryGetVolume(out realVol)) _trackedVolume = realVol;
                            else if (_trackedVolume < 0) _trackedVolume = 20;
                            bool realMute;
                            if (Sonos.TryGetMute(out realMute)) _virtualMuted = realMute;
                            bool realNight;
                            if (Sonos.TryGetNightMode(out realNight)) _nightMode = realNight;
                            bool realVoice;
                            if (Sonos.TryGetDialogLevel(out realVoice)) _voiceMode = realVoice;
                        }
                        _lastSonosReadAt = now;

                        if (key == Command.VolMute)
                        {
                            _virtualMuted = !_virtualMuted;
                            Sonos.SetMuteAsync(_virtualMuted);
                        }
                        else
                        {
                            if (_virtualMuted)
                            {
                                _virtualMuted = false;
                                Sonos.SetMuteAsync(false);
                            }
                            _trackedVolume = VolumeModel.Next(_trackedVolume, key == Command.VolUp ? 1 : -1, 1);
                            Sonos.SetVolumeAsync(_trackedVolume);
                        }

                        if (_volumeEdge == VolumeEdge.Hidden) return;
                        EnsurePill().Show(_trackedVolume, _virtualMuted, true, _nightMode, _voiceMode, AllowVolumeModeGrab());
                        return;
                    }

                    if (fromFifo)
                    {
                        string native = key == Command.VolUp ? "XF86AudioRaiseVolume" : (key == Command.VolDown ? "XF86AudioLowerVolume" : "XF86AudioMute");
                        int rc = Libc.system("/usr/bin/input_keyevent " + native + " >/dev/null 2>&1 &");
                        Logger.Write("volume: sound on output " + output + ", key passed to Samsung in the background rc=" + rc);
                    }
                    return;
                }

                int tvVolume;
                bool hasTvVol = Sound.TryGetVolume(out tvVolume);
                if (_trackedVolume < 0) _trackedVolume = hasTvVol && tvVolume > 0 ? tvVolume : 20;

                int volume = external ? _trackedVolume : (hasTvVol ? tvVolume : _trackedVolume);
                bool muted = external ? _virtualMuted : SamsungMuted();

                if (!external && muted)
                {
                    Libc.system("/usr/bin/input_keyevent XF86AudioMute >/dev/null 2>&1 &");
                    muted = false;
                }

                int target = volume;
                if (key == Command.VolMute)
                {
                    if (external)
                    {
                        _virtualMuted = !_virtualMuted;
                        if (_virtualMuted) { _levelBeforeMute = volume > 0 ? volume : 20; target = 0; }
                        else target = _levelBeforeMute > 0 ? _levelBeforeMute : 20;
                    }
                    else
                    {
                        if (volume > 0) { _levelBeforeMute = volume; target = 0; }
                        else target = _levelBeforeMute > 0 ? _levelBeforeMute : 10;
                    }
                }
                else
                {
                    _virtualMuted = false;
                    int from = volume == 0 && _levelBeforeMute > 0 ? _levelBeforeMute : volume;
                    target = VolumeModel.Next(from, key == Command.VolUp ? 1 : -1, 1);
                    _levelBeforeMute = 0;
                }

                _trackedVolume = target;
                if (!external && target != volume)
                {
                    if (Sound.TrySetVolume(target)) volume = target;
                    else Logger.Write("volume: level change refused");
                }
                else
                {
                    volume = target;
                }

                if (_volumeEdge == VolumeEdge.Hidden) return;
                bool showMuted = (external && _virtualMuted) || (volume == 0 && _levelBeforeMute > 0);
                EnsurePill().Show(volume, showMuted);
            }
            catch (Exception ex) { Logger.Write("volume: " + ex.Message); }
        }

        private static void TryClaimHome()
        {
            if (!_claimHome || _claimTried || _homeLaunch != null || _input == null || !_input.HasOpenNodes) return;
            _claimTried = true;
            try
            {
                _homeClaimed = Efl.elm_win_keygrab_set(_win, "XF86Home", 0, 0, 0, KeygrabExclusive);
                Logger.Write("home: claim " + (_homeClaimed ? "granted" : "refused"));
            }
            catch (Exception ex) { Logger.Write("home: claim unavailable (" + ex.GetType().Name + ")"); }
            if (_homeClaimed) WatchEcoreKeys();
            try
            {
                if (_claimPath != null)
                {
                    if (_homeClaimed) File.WriteAllText(_claimPath, Libc.getpid().ToString(CultureInfo.InvariantCulture));
                    else Libc.unlink(_claimPath);
                }
            }
            catch (Exception ex) { Logger.Write("home claim file: " + ex.Message); }
        }

        private static void WatchEcoreKeys()
        {
            try
            {
                IntPtr lib = NativeLibrary.Load("libecore_input.so.1");
                int code = Marshal.ReadInt32(NativeLibrary.GetExport(lib, "ECORE_EVENT_KEY_DOWN"));
                bool ok = Efl.ecore_event_handler_add(code, EcoreKeyCb, IntPtr.Zero) != IntPtr.Zero;
                Logger.Write("home: window keys " + (ok ? "on" : "refused"));
            }
            catch (Exception ex) { Logger.Write("home: window keys unavailable (" + ex.GetType().Name + ")"); }
        }

        private static byte OnEcoreKey(IntPtr data, int type, IntPtr ev)
        {
            try
            {
                string key = ev == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(ev));
                bool anyActive = _ctl.IsActive || (_settingsCtl != null && _settingsCtl.IsActive);
                if (key == "XF86Home" && !anyActive)
                {
                    double now = Efl.ecore_time_get();
                    if (now - _input.LastRawHome >= HomeEchoSeconds && now - _input.LastHome >= InputWatcher.HomeDebounceSeconds)
                    {
                        _input.LastHome = now;
                        Logger.Write("home: claimed press from the window");
                        ShowDock();
                        LauncherSvc.ArmGuard();
                    }
                }
            }
            catch (Exception ex) { Logger.Write("ecore key: " + ex.Message); }
            return 1;
        }

        private static void HandleSettingsKey(string key)
        {
            try
            {
                if (_dockMapped) OverlayFlag.Mark(OverlayFlag.DockPath, true);
                SettingsKeyResult res = _settingsCtl.Key(key);
                switch (res.Action)
                {
                    case SettingsActionKind.Moved:
                        SoundPlayer.Play(SoundEffect.Move);
                        _settingsModal.Apply(_settingsCtl, _inputs);
                        StartAnimator();
                        ResetIdle(SettingsIdleSeconds);
                        break;
                    case SettingsActionKind.Changed:
                        _fileSettings.SaveAtomic(_confPath);
                        ApplyEffectiveSettings();
                        SoundPlayer.SetEnabled(_effectiveSettings.EffectiveSoundEnabled);
                        SoundPlayer.Play(SoundEffect.Move);
                        _appsDock.InvalidateChrome();
                        if (_allDock != null) _allDock.InvalidateChrome();
                        _inputsDock.InvalidateChrome();
                        StartClock();
                        _dock.Apply(_ctl);
                        _settingsCtl.RefreshLabels();
                        _settingsModal.Apply(_settingsCtl, _inputs);
                        StartAnimator();
                        if (res.NeedsWeatherRefresh && Weather.Enabled) StartWeatherRefreshPoll();
                        ResetIdle(SettingsIdleSeconds);
                        break;
                    case SettingsActionKind.SearchCity:
                        SoundPlayer.Play(SoundEffect.Move);
                        _settingsModal.Apply(_settingsCtl, _inputs);
                        StartAnimator();
                        StartCitySearch(res.Query);
                        ResetIdle(SettingsIdleSeconds);
                        break;
                    case SettingsActionKind.SwitchInput:
                        CloseSettings(playSound: false);
                        AppEntry[] hdmi = _settingsCtl.HdmiInputs;
                        if (res.Index >= 0 && res.Index < hdmi.Length) LaunchDirect(hdmi[res.Index]);
                        break;
                    case SettingsActionKind.LaunchApps:
                        CloseSettings(playSound: false);
                        OpenAllApps();
                        break;
                    case SettingsActionKind.LaunchVetro:
                        CloseSettings(playSound: false);
                        LaunchDirect(_vetroEntry);
                        break;
                    case SettingsActionKind.LaunchTvStore:
                        CloseSettings(playSound: false);
                        LaunchDirect(_tvStoreEntry);
                        break;
                    case SettingsActionKind.LaunchTvSettings:
                        CloseSettings(playSound: false);
                        LaunchDirect(_tvSettingsEntry);
                        break;
                    case SettingsActionKind.Close:
                        CloseSettings(playSound: true);
                        break;
                    case SettingsActionKind.CloseAll:
                        if (key == "XF86Home")
                        {
                            double now = Efl.ecore_time_get();
                            if (now - _input.LastRawHome < HomeEchoSeconds)
                            {
                                Logger.Write("home echo ignored " + ((now - _input.LastRawHome) * 1000).ToString("0") + " ms after the press");
                                _settingsModal.ShowWindow();
                                break;
                            }
                            _input.LastHome = now;
                            CloseSettings(playSound: false);
                            HideDock();
                            LauncherSvc.ArmGuard();
                            break;
                        }
                        CloseSettings(playSound: false);
                        HideDock();
                        break;
                }
            }
            catch (Exception ex) { Logger.Write("settings key: " + ex); }
        }

        private static void EnsurePollTimer()
        {
            if (_pollTimer == IntPtr.Zero)
                _pollTimer = Efl.ecore_timer_add(0.12, PollAsyncCb, IntPtr.Zero);
        }

        private static void StartWeatherRefreshPoll()
        {
            try
            {
                _weatherTask = Weather.Refresh();
                EnsurePollTimer();
            }
            catch (Exception ex) { Logger.Write("weather refresh: " + ex.Message); }
        }

        private static void StartCitySearch(string query)
        {
            int gen = Interlocked.Increment(ref _citySearchGen);
            EnsurePollTimer();
            Weather.SearchCityAsync(query, Localization.Language).ContinueWith(t =>
            {
                if (Volatile.Read(ref _citySearchGen) != gen) return;
                GeoSearchOutcome outcome = t.IsFaulted
                    ? new GeoSearchOutcome(new GeoResult[0], t.Exception?.GetBaseException().Message ?? "error")
                    : t.Result;
                _pendingCityGen = gen;
                Interlocked.Exchange(ref _pendingCityOutcome, outcome);
            });
        }

        private static byte OnPollAsync(IntPtr data)
        {
            try
            {
                GeoSearchOutcome city = Interlocked.Exchange(ref _pendingCityOutcome, null);
                if (city != null && _settingsCtl != null && _pendingCityGen == _citySearchGen && _settingsCtl.View == SettingsView.CitySearch && _settingsCtl.CityBusy)
                {
                    _settingsCtl.SetCitySearchResults(city.Results, city.Error);
                    _settingsModal.Apply(_settingsCtl, _inputs);
                }

                bool weatherDone = (_weatherTask != null && _weatherTask.IsCompleted && !Weather.IsRefreshing);
                if (weatherDone)
                {
                    _weatherTask = null;
                    _appsDock.InvalidateChrome();
                    if (_allDock != null) _allDock.InvalidateChrome();
                    _inputsDock.InvalidateChrome();
                    if (_ctl != null && _ctl.IsActive && _dock != null) _dock.Apply(_ctl);
                }

                bool stillBusy = _weatherTask != null || Weather.IsRefreshing
                    || (_settingsCtl != null && _settingsCtl.CityBusy);
                if (stillBusy) return 1;
            }
            catch (Exception ex) { Logger.Write("poll: " + ex.Message); }
            _pollTimer = IntPtr.Zero;
            return 0;
        }

        private static void Activate(AppEntry e)
        {
            if (e.Panel != null)
            {
                if (e.Panel == "allapps") OpenAllApps();
                else if (e.Panel == "inputs") OpenInputs();
                else Logger.Write("unknown panel " + e.Panel);
                return;
            }
            if (e.Slug == "settings")
            {
                OpenSettings();
                return;
            }
            LaunchDirect(e);
        }

        private static void LaunchDirect(AppEntry e)
        {
            if (e == null) return;
            SoundPlayer.Play(SoundEffect.Launch);
            if (LauncherSvc.ShouldCancelGuard(e.AppId)) LauncherSvc.CancelGuard();
            HideDock(playSound: false);
            _pendingLaunch = e;
        }

        // ------------------------------------------------------------------ warm-up

        private static void WarmUp()
        {
            long t0 = Stopwatch.GetTimestamp();
            Controller[] ctls = _allCtl != null ? new[] { _appsCtl, _allCtl, _inputsCtl } : new[] { _appsCtl, _inputsCtl };
            Dock[] docks = _allDock != null ? new[] { _appsDock, _allDock, _inputsDock } : new[] { _appsDock, _inputsDock };
            for (int p = 0; p < ctls.Length; p++)
            {
                Controller c = ctls[p]; Dock d = docks[p];
                int idx;
                c.Show();
                WarmFrames(c, d, 18);
                c.Key("Right", out idx);
                WarmFrames(c, d, 24);
                c.Key("Left", out idx);
                WarmFrames(c, d, 24);
                c.Hide();
                WarmFrames(c, d, 14);
                c.Reset();
            }
            Logger.Write("warm-up " + ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0") + " ms");
        }

        private static void WarmFrames(Controller c, Dock d, int frames)
        {
            for (int i = 0; i < frames; i++) { c.Tick(1.0 / 60.0); d.Apply(c); }
        }

        // ------------------------------------------------------------------ entry point

        private static int GetHome()
        {
            try
            {
                IntPtr id;
                int rc = Efl.app_operation_get_current_app("http://tizen.org/appcontrol/operation/main", out id);
                Console.WriteLine("rc=" + rc);
                Console.WriteLine("home=" + (id == IntPtr.Zero ? "(null)" : Marshal.PtrToStringAnsi(id)));
                return rc == 0 ? 0 : 1;
            }
            catch (Exception ex) { Console.WriteLine("get-home failed: " + ex.Message); return 2; }
        }

        private static int Main(string[] args)
        {
            if (Flag(args, "--get-home")) return GetHome();
            _perf = Flag(args, "--perf");
            if (Flag(args, "--no-motion")) { Controller.Instant = true; DockLayout.Grow = false; DockLayout.GlowFromIcon = false; PadMotion.Instant = true; VolumeFade.Instant = true; }
            _dir = ResolveDir(args);
            _pidPath = Path.Combine(_dir, "tvlauncher.pid");
            _fifoPath = Path.Combine(_dir, "tvlauncher.fifo");
            _claimPath = Path.Combine(_dir, "tvlauncher.homeclaim");
            _claimHome = Flag(args, "--claim-home");
            _volumeEdge = VolumePlacement.ParseEdge(Arg(args, "--volume-edge"));
            int port;
            if (int.TryParse(Arg(args, "--service-port"), out port) && port >= 1024 && port <= 65535)
                LauncherSvc.ServicePort = port;
            OverlayFlag.Pid = Libc.getpid().ToString(CultureInfo.InvariantCulture);
            Logger.Init(Path.Combine(_dir, "tvlauncher.log"));

            if (!AcquireInstance()) return 0;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { SoundPlayer.Shutdown(); CleanupFiles(); };

            try
            {
                string engine = SetupEnvironment();
                Logger.Write("start pid=" + Libc.getpid() + " uid=" + Libc.getuid() + " engine=" + engine);

                string res = Path.Combine(AppContext.BaseDirectory, "res");
                _resDir = res;

                Efl.elm_init(0, IntPtr.Zero);
                _win = Efl.elm_win_add(IntPtr.Zero, "tvlauncher", Efl.ElmWinNotification);
                if (_win == IntPtr.Zero) { Logger.Write("elm_win_add failed"); return 2; }

                int sx, sy, sw, sh;
                Efl.elm_win_screen_size_get(_win, out sx, out sy, out sw, out sh);
                int screenW = DefaultScreenW, screenH = DefaultScreenH;
                string forced = Arg(args, "--screen");
                if (forced != null)
                {
                    string[] wh = forced.Split('x');
                    int fw, fh;
                    if (wh.Length == 2 && int.TryParse(wh[0], out fw) && int.TryParse(wh[1], out fh)) { screenW = fw; screenH = fh; }
                }
                _screenW = screenW;
                _screenH = screenH;
                Logger.Write("screen reported " + sw + "x" + sh + ", layout " + screenW + "x" + screenH);

                Weather.Init(_dir);
                SetupLocalization(args);
                Sonos.StartDiscovery();
                ConfigureVolumeOsdSuppression();
                SoundPlayer.Init(Path.Combine(res, "sounds"), Logger.Write, Flag(args, "--no-sound"), _effectiveSettings.EffectiveSoundEnabled);

                AppEntry[] rawApps;
                _apps = LoadApps(Arg(args, "--apps") ?? Path.Combine(res, "apps.tsv"), out rawApps);
                _allApps = LoadAllApps(Arg(args, "--all-apps") ?? Path.Combine(Weather.CacheDir, "vetro-dock-allapps.tsv"), _apps);
                _inputs = LoadList(Arg(args, "--inputs") ?? Path.Combine(res, "inputs.tsv"), Catalog.BuiltInInputs);
                ConfigureShortcuts(rawApps);
                int leftPins = Catalog.LeftPinCount(_apps);
                int group = Catalog.GroupStart(_apps);
                var layout = new DockLayout(_apps.Length, screenW, screenH, 0, false, group, leftPins, true);
                var allLayout = new DockLayout(_allApps.Length, screenW, screenH, layout.WinW, false, Catalog.GroupStart(_allApps), 0, false);
                var inputsLayout = new DockLayout(_inputs.Length, screenW, screenH, layout.WinW, true, -1);
                _appsCtl = new Controller(layout) { PanelEnabled = false };
                _allCtl = new Controller(allLayout, PageKind.Panel) { PanelEnabled = false };
                _inputsCtl = new Controller(inputsLayout, PageKind.Panel);
                _ctl = _appsCtl;

                Efl.elm_win_title_set(_win, "tvlauncher");
                Efl.elm_win_alpha_set(_win, true);
                Efl.elm_win_borderless_set(_win, true);
                Efl.elm_win_prop_focus_skip_set(_win, false);
                int level = Efl.efl_util_set_notification_window_level(_win, NotificationLevelTop);
                Efl.elm_win_layer_set(_win, WindowLayer);
                Efl.elm_win_aux_hint_add(_win, "wm.policy.win.user.geometry", "1");
                Efl.elm_win_aux_hint_add(_win, "wm.comp.win.effect.enable", "0");
                Efl.evas_object_move(_win, layout.WinX, layout.WinY);
                Efl.evas_object_resize(_win, layout.WinW, layout.WinH);
                Logger.Write("window " + layout.WinW + "x" + layout.WinH + " at " + layout.WinX + "," + layout.WinY + " notification-level=" + level);

                string dumpIcons = Arg(args, "--dump-icons");
                if (dumpIcons != null && Directory.Exists(dumpIcons)) Dock.DumpIconsTo = dumpIcons;
                Weather.LoadCache();
                try { Weather.Refresh(); } catch (Exception ex) { Logger.Write("weather refresh: " + ex.Message); }
                Logger.Write("weather " + (Weather.Text ?? "none") + (Weather.LastError != null ? " (" + Weather.LastError + ")" : ""));

                _appsDock = new Dock(_win, layout, _apps, res, Logger.Write, Design.HintText, true, true);
                _appsDock.Build();
                _allDock = new Dock(_win, allLayout, _allApps, res, Logger.Write, Design.HintText, false, false);
                _allDock.Build();
                _allDock.SetVisible(false);
                _inputsDock = new Dock(_win, inputsLayout, _inputs, res, Logger.Write, "Back", false, false);
                _inputsDock.Build();
                _inputsDock.SetVisible(false);
                _dock = _appsDock;
                if (!Flag(args, "--no-warmup")) WarmUp();
                _appsDock.Apply(_appsCtl);
                _allDock.Apply(_allCtl);
                _inputsDock.Apply(_inputsCtl);

                _settingsCtl = new SettingsController(_fileSettings, _inputs);
                _settingsModal = new SettingsModal(layout, screenW, screenH, res, Logger.Write);
                _settingsModal.Build();
                if (_settingsModal.Win != IntPtr.Zero)
                    Efl.evas_object_event_callback_add(_settingsModal.Win, Efl.EvasCallbackKeyDown, KeyCb, IntPtr.Zero);

                Logger.Write("engine in use: " + (Efl.EngineName(_dock.Evas) ?? "unknown") + ", " + _apps.Length + " apps, " + _allApps.Length + " all-apps, " + _inputs.Length + " inputs, "
                    + (_appsDock.MissingIcons + _allDock.MissingIcons + _inputsDock.MissingIcons) + " icons missing");

                Efl.evas_object_event_callback_add(_win, Efl.EvasCallbackKeyDown, KeyCb, IntPtr.Zero);
                Efl.evas_object_event_callback_add(_win, Efl.EvasCallbackKeyUp, KeyUpCb, IntPtr.Zero);
                SetupFifo();
                LauncherSvc.ConfigureGuard(Arg(args, "--kill-on-home"), _dir);

                string homeLaunch = Arg(args, "--home-launch");
                if (homeLaunch != null)
                {
                    _homeLaunch = HomeLaunch.Parse(homeLaunch);
                    Logger.Write(_homeLaunch != null ? "home key launches " + homeLaunch + "; the dock is not shown" : "home-launch: bad app id ignored");
                }

                _input = new InputWatcher(OnRawHome, OnRawHomeBurst, OnRawNav, OnRawRelease);
                _input.Start(Arg(args, "--home-code") ?? Environment.GetEnvironmentVariable("TVLAUNCHER_HOME_CODE"), Flag(args, "--learn"));

                string fpsArg = Arg(args, "--vblank-fps");
                int fps;
                if (fpsArg != null && int.TryParse(fpsArg, out fps) && (fps == 0 || (fps >= 30 && fps <= 120))) _vblankFps = fps;
                if (_vblankFps > 0 && File.Exists("/usr/bin/tdm-monitor")) Efl.ecore_animator_add(FpsCb, IntPtr.Zero);
                Logger.Write("animator frame time " + (Efl.ecore_animator_frametime_get() * 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms");

                if (!Flag(args, "--hidden")) ShowDock();
                else if (!Flag(args, "--no-premap"))
                {
                    _appsCtl.HoldFaint(0.012f);
                    _appsDock.Apply(_appsCtl);
                    Efl.evas_object_show(_win);
                    if (Flag(args, "--premap-activate")) Efl.elm_win_activate(_win);
                    _premapTimer = Efl.ecore_timer_add(0.5, PremapCb, IntPtr.Zero);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                Logger.Write("elm_run");
                Efl.elm_run();
                Logger.Write("elm_run returned");
                Efl.elm_shutdown();
            }
            catch (Exception ex)
            {
                Logger.Write("fatal: " + ex);
                return 1;
            }
            return 0;
        }
    }
}
