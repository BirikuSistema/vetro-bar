using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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

        // Delegates handed to native code live in static fields so the GC can never collect them.
        private static readonly Efl.EvasCb KeyCb = OnKey;
        private static readonly Efl.TaskCb AnimCb = OnFrame;
        private static readonly Efl.TaskCb IdleCb = OnIdle;
        private static readonly Efl.FdCb FifoCb = OnFifo;
        private static readonly Efl.TaskCb ClockCb = OnClock;
        private static readonly Efl.TaskCb WeatherCb = OnWeather;
        private static readonly Efl.TaskCb WeatherSoonCb = OnWeatherSoon;
        private static readonly Efl.TaskCb FpsCb = OnFps;
        private static readonly Efl.TaskCb PremapCb = OnPremap;

        private static readonly CommandBuffer Commands = new CommandBuffer();
        private static readonly NavigationInput Navigation = new NavigationInput();
        private static readonly AppLauncher LauncherSvc = new AppLauncher();
        private static readonly byte[] ReadBuf = new byte[128];

        private static InputWatcher _input;
        private static string _dir, _pidPath, _fifoPath;
        private static IntPtr _win, _anim, _idle, _fifoHandler, _clockTimer, _weatherTimer, _premapTimer;
        private static int _fifoFd = -1;
        private static double _lastFrame;
        private static AppEntry[] _apps, _inputs;
        private static Controller _ctl, _appsCtl, _inputsCtl;
        private static Dock _dock, _appsDock, _inputsDock;
        private static AppEntry _pendingLaunch;
        private static AppEntry _homeLaunch;

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
            if (File.Exists("/run/pulse/native")) Libc.setenv("PULSE_RUNTIME_PATH", "/run/pulse", 0);
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
                int pid;
                if (File.Exists(_pidPath) && int.TryParse(File.ReadAllText(_pidPath).Trim(), out pid) && pid == Libc.getpid())
                {
                    File.Delete(_pidPath);
                    Libc.unlink(_fifoPath);
                }
            }
            catch (Exception) { }
        }

        private static void SetupFifo()
        {
            Libc.unlink(_fifoPath);
            if (Libc.mkfifo(_fifoPath, 438u) != 0) { Logger.Write("mkfifo failed: " + Marshal.GetLastWin32Error()); return; }
            Libc.chmod(_fifoPath, 438u);
            _fifoFd = Libc.open(_fifoPath, Libc.O_RDWR | Libc.O_NONBLOCK);
            if (_fifoFd < 0) { Logger.Write("open fifo failed: " + Marshal.GetLastWin32Error()); return; }
            _fifoHandler = Efl.ecore_main_fd_handler_add(_fifoFd, Efl.EcoreFdRead, FifoCb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        private static AppEntry[] LoadApps(string path) { return Catalog.ArrangeBar(LoadList(path, Catalog.BuiltIn)); }

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
            _pendingLaunch = null;
            LauncherSvc.CancelPendingKey();
            if (_ctl != _appsCtl && !_ctl.IsActive) ActivatePage(_appsCtl, _appsDock);
            if (!_ctl.Show()) { ResetIdle(); return; }

            SoundPlayer.Play(SoundEffect.Open);
            _dock.Apply(_ctl);
            Efl.evas_object_show(_win);
            Efl.elm_win_activate(_win);
            StartClock();
            StartAnimator();
            ResetIdle();
        }

        private static void HideDock(bool playSound = true)
        {
            _pendingLaunch = null;
            CancelIdle();
            StopClock();
            if (_ctl.Hide())
            {
                if (playSound) SoundPlayer.Play(SoundEffect.Close);
                StartAnimator();
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

        private static readonly string[] DayNames = { "Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado" };
        private static readonly string[] MonthNames = { "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre" };

        private static string ClockText() { return DateTime.Now.ToString("HH:mm"); }

        private static string DayText()
        {
            DateTime n = DateTime.Now;
            return DayNames[(int)n.DayOfWeek] + " " + n.Day + " de " + MonthNames[n.Month - 1];
        }

        private static void StartClock()
        {
            string t = ClockText();
            string day = DayText();
            _appsDock.SetClock(t);
            _inputsDock.SetClock(t);
            _appsDock.SetDay(day);
            _inputsDock.SetDay(day);
            if (_clockTimer != IntPtr.Zero) Efl.ecore_timer_del(_clockTimer);
            DateTime now = DateTime.Now;
            double wait = 60.0 - now.Second - now.Millisecond / 1000.0 + 0.05;
            _clockTimer = Efl.ecore_timer_add(wait, ClockCb, IntPtr.Zero);
            if (Design.SideChrome)
            {
                Efl.ecore_timer_add(2.0, WeatherSoonCb, IntPtr.Zero);
                if (_weatherTimer == IntPtr.Zero)
                    _weatherTimer = Efl.ecore_timer_add(Design.WeatherSeconds, WeatherCb, IntPtr.Zero);
            }
        }

        private static void StopClock()
        {
            if (_clockTimer == IntPtr.Zero) return;
            Efl.ecore_timer_del(_clockTimer);
            _clockTimer = IntPtr.Zero;
        }

        private static byte OnWeatherSoon(IntPtr data)
        {
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
            if (_idle == IntPtr.Zero) _idle = Efl.ecore_timer_add(Controller.IdleHideSeconds, IdleCb, IntPtr.Zero);
            else Efl.ecore_timer_reset(_idle);
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
                _lastFrame = now;
                _dock.Apply(_ctl);
                if (_ctl.Phase == Phase.Hidden)
                {
                    Efl.evas_object_hide(_win);
                    AppEntry pending = _pendingLaunch;
                    _pendingLaunch = null;
                    if (pending != null) LauncherSvc.Execute(pending);
                }
                if (_perf) PerfSample(dt, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, !more);
                if (more) return 1;
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
            try { HideDock(); } catch (Exception ex) { Logger.Write("idle: " + ex); }
            return 0;
        }

        private static void OnKey(IntPtr data, IntPtr evas, IntPtr obj, IntPtr eventInfo)
        {
            try
            {
                string key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(eventInfo));
                if (Navigation.IsEcho(key, Efl.ecore_time_get())) return;
                HandleKey(key);
            }
            catch (Exception ex) { Logger.Write("key: " + ex); }
        }

        private static void OnRawHome()
        {
            _perfWhy = "home";
            if (_homeLaunch != null) { LauncherSvc.LaunchApp(_homeLaunch); LauncherSvc.ArmGuard(); return; }
            if (_ctl.IsActive) HideDock(); else ShowDock();
            LauncherSvc.ArmGuard();
        }

        private static void OnRawNav(int code)
        {
            string key = Navigation.Press(code, _ctl.IsActive, Efl.ecore_time_get());
            if (key != null) HandleKey(key);
        }

        private static void HandleKey(string key)
        {
            try
            {
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
                        Activate((_ctl == _appsCtl ? _apps : _inputs)[index]);
                        break;
                    case KeyAction.OpenPanel:
                        OpenInputs();
                        break;
                    case KeyAction.Back:
                        SelectPage(_appsCtl, _appsDock);
                        break;
                    case KeyAction.Hide:
                        if (key == "XF86Home")
                        {
                            double now = Efl.ecore_time_get();
                            if (now - _input.LastRawHome < HomeEchoSeconds)
                            {
                                Logger.Write("home echo ignored " + ((now - _input.LastRawHome) * 1000).ToString("0") + " ms after the press");
                                break;
                            }
                            _input.LastHome = now;
                            HideDock();
                            LauncherSvc.ArmGuard();
                            break;
                        }
                        HideDock();
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
                            case Command.Show: _perfWhy = "fifo-show"; ShowDock(); break;
                            case Command.Hide: _perfWhy = "fifo-hide"; HideDock(); break;
                            case Command.Toggle: _perfWhy = "fifo-toggle"; if (_ctl.IsActive) HideDock(); else ShowDock(); break;
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Write("fifo: " + ex); }
            return 1;
        }

        private static void Activate(AppEntry e)
        {
            if (e.Panel != null)
            {
                if (e.Panel == "inputs") OpenInputs(); else Logger.Write("unknown panel " + e.Panel);
                return;
            }
            SoundPlayer.Play(SoundEffect.Launch);
            if (LauncherSvc.ShouldCancelGuard(e.AppId)) LauncherSvc.CancelGuard();
            HideDock(playSound: false);
            _pendingLaunch = e;
        }

        // ------------------------------------------------------------------ warm-up

        private static void WarmUp()
        {
            long t0 = Stopwatch.GetTimestamp();
            Controller[] ctls = { _appsCtl, _inputsCtl };
            Dock[] docks = { _appsDock, _inputsDock };
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
            if (Flag(args, "--no-motion")) { Controller.Instant = true; DockLayout.Grow = false; DockLayout.GlowFromIcon = false; }
            _dir = ResolveDir(args);
            _pidPath = Path.Combine(_dir, "tvlauncher.pid");
            _fifoPath = Path.Combine(_dir, "tvlauncher.fifo");
            Logger.Init(Path.Combine(_dir, "tvlauncher.log"));

            if (!AcquireInstance()) return 0;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { SoundPlayer.Shutdown(); CleanupFiles(); };

            try
            {
                string engine = SetupEnvironment();
                Logger.Write("start pid=" + Libc.getpid() + " uid=" + Libc.getuid() + " engine=" + engine);

                string res = Path.Combine(AppContext.BaseDirectory, "res");
                SoundPlayer.Init(Path.Combine(res, "sounds"), Logger.Write, Flag(args, "--no-sound"));

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
                Logger.Write("screen reported " + sw + "x" + sh + ", layout " + screenW + "x" + screenH);

                _apps = LoadApps(Arg(args, "--apps") ?? Path.Combine(res, "apps.tsv"));
                _inputs = LoadList(Arg(args, "--inputs") ?? Path.Combine(res, "inputs.tsv"), Catalog.BuiltInInputs);
                int leftPins = Catalog.LeftPinCount(_apps);
                int group = Catalog.GroupStart(_apps);
                var layout = new DockLayout(_apps.Length, screenW, screenH, 0, false, group, leftPins, true);
                var inputsLayout = new DockLayout(_inputs.Length, screenW, screenH, layout.WinW, true, -1);
                _appsCtl = new Controller(layout) { PanelEnabled = false };
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
                try { Weather.Refresh().Wait(5000); } catch (Exception ex) { Logger.Write("weather wait: " + ex.Message); }
                Logger.Write("weather " + (Weather.Text ?? "none") + (Weather.LastError != null ? " (" + Weather.LastError + ")" : ""));

                _appsDock = new Dock(_win, layout, _apps, res, Logger.Write, Design.HintText, true);
                _appsDock.Build();
                _inputsDock = new Dock(_win, inputsLayout, _inputs, res, Logger.Write, "Back", false);
                _inputsDock.Build();
                _inputsDock.SetVisible(false);
                _dock = _appsDock;
                if (!Flag(args, "--no-warmup")) WarmUp();
                _appsDock.Apply(_appsCtl);
                _inputsDock.Apply(_inputsCtl);
                Logger.Write("engine in use: " + (Efl.EngineName(_dock.Evas) ?? "unknown") + ", " + _apps.Length + " apps, " + _inputs.Length + " inputs, "
                    + (_appsDock.MissingIcons + _inputsDock.MissingIcons) + " icons missing");

                Efl.evas_object_event_callback_add(_win, Efl.EvasCallbackKeyDown, KeyCb, IntPtr.Zero);
                SetupFifo();
                LauncherSvc.ConfigureGuard(Arg(args, "--kill-on-home"), _dir);

                string homeLaunch = Arg(args, "--home-launch");
                if (homeLaunch != null)
                {
                    _homeLaunch = HomeLaunch.Parse(homeLaunch);
                    Logger.Write(_homeLaunch != null ? "home key launches " + homeLaunch + "; the dock is not shown" : "home-launch: bad app id ignored");
                }

                _input = new InputWatcher(OnRawHome, OnRawNav);
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
