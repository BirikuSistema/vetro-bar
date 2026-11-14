using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Launcher.Core;
using Launcher.Native;

namespace Launcher.Host
{
    /// <summary>
    /// Launches Tizen apps, system controls, TV/HDMI sources, and remote-key entries,
    /// and manages the Home-key guard that keeps Samsung's own Home out of the way.
    /// </summary>
    internal sealed class AppLauncher
    {
        private const string SourceIconDir = "/usr/apps/com.samsung.tv.theme-resource/shared/res/org.tizen.menu/Settings_Popup/device_icon/";
        private const string KeyTool = "/usr/bin/input_keyevent ";

        private readonly Efl.TaskCb _keyTimerCb;
        private string[] _guardApps;
        private string _guardFile;
        private int _guardGen;
        private AppEntry _pendingKey;
        private IntPtr _keyTimer;

        public AppLauncher()
        {
            _keyTimerCb = OnKeyTimer;
        }

        public void ConfigureGuard(string guardArg, string dir)
        {
            if (guardArg == null) return;
            string[] parsed = HomeGuard.Parse(guardArg);
            if (parsed == null)
            {
                Logger.Write("guard: bad app id ignored");
                return;
            }
            if (Directory.Exists("/usr/apps"))
            {
                var installed = new List<string>();
                foreach (string id in parsed)
                {
                    if (Directory.Exists("/usr/apps/" + id) || Directory.Exists("/opt/usr/apps/" + id))
                        installed.Add(id);
                }
                if (installed.Count > 0) parsed = installed.ToArray();
            }
            _guardApps = parsed;
            _guardFile = Path.Combine(dir, "tvlauncher.guard");
            Logger.Write("guard: " + string.Join(",", _guardApps) + " is closed after each Home press (until it is asked for): " + HomeGuard.Command(_guardApps, _guardFile, 0));
        }

        public bool ShouldCancelGuard(string appId)
        {
            return HomeGuard.ShouldCancel(_guardApps, appId);
        }

        public void ArmGuard()
        {
            if (_guardApps == null) return;
            try
            {
                int gen = NewGuardGeneration();
                string cmd = HomeGuard.Command(_guardApps, _guardFile, gen);
                if (Array.IndexOf(_guardApps, "com.samsung.tv.csfs") >= 0)
                    cmd = "pkill -9 -f csfs.dll >/dev/null 2>&1; " + cmd;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Libc.system(cmd); }
                    catch (Exception ex) { Logger.Write("guard: " + ex.Message); }
                });
            }
            catch (Exception ex) { Logger.Write("guard: " + ex.Message); }
        }

        public void CancelGuard()
        {
            if (_guardApps == null) return;
            try
            {
                NewGuardGeneration();
                Logger.Write("guard: cancelled, " + string.Join(",", _guardApps) + " was asked for");
            }
            catch (Exception ex) { Logger.Write("guard cancel: " + ex.Message); }
        }

        private int NewGuardGeneration()
        {
            _guardGen++;
            File.WriteAllText(_guardFile, _guardGen.ToString(CultureInfo.InvariantCulture));
            return _guardGen;
        }

        public void CancelPendingKey()
        {
            _pendingKey = null;
            if (_keyTimer != IntPtr.Zero)
            {
                Efl.ecore_timer_del(_keyTimer);
                _keyTimer = IntPtr.Zero;
            }
        }

        public void Execute(AppEntry e)
        {
            if (e.SourceType >= 0) SwitchSource(e);
            if (e.AppId != null || e.Key != null || e.SourceType < 0) Launch(e);
        }

        public void Launch(AppEntry e)
        {
            if (e.Key != null)
            {
                CancelPendingKey();
                _pendingKey = e;
                _keyTimer = Efl.ecore_timer_add(0.30, _keyTimerCb, IntPtr.Zero);
                return;
            }
            LaunchApp(e);
        }

        public void LaunchApp(AppEntry e)
        {
            try
            {
                if (e.KillFirst != null && e.AppId != null)
                {
                    int rc = Libc.system("( /usr/bin/app_launcher -k " + e.KillFirst + "; sleep 0.4; /usr/bin/app_launcher -s " + e.AppId + " ) >/dev/null 2>&1 &");
                    Logger.Write("launch " + e.AppId + " after closing " + e.KillFirst + " rc=" + rc);
                    return;
                }
                if (HomeGuard.ShouldCancel(_guardApps, e.AppId)) CancelGuard();
                if (e.SystemApp || e.AppId == "com.samsung.tv.csfs")
                {
                    Logger.Write("launch " + e.AppId + " rc=" + Libc.system(SystemLaunch.Command(e.AppId, e.FallbackAppId)));
                    return;
                }
                long t0 = Stopwatch.GetTimestamp();
                bool sent = SendLaunch(e.AppId);
                long ms = (Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency;
                if (sent) { Logger.Write("launch " + e.AppId + " request " + ms + " ms"); return; }
                Logger.Write("launch failed: " + e.AppId);
                if (e.FallbackAppId != null && SendLaunch(e.FallbackAppId))
                {
                    Logger.Write("launch " + e.FallbackAppId + " (fallback)");
                    return;
                }
                Process.Start(new ProcessStartInfo("/usr/bin/app_launcher", "-s " + (e.FallbackAppId ?? e.AppId)) { UseShellExecute = false });
                Logger.Write("launch via app_launcher: " + (e.FallbackAppId ?? e.AppId));
            }
            catch (Exception ex) { Logger.Write("launch error: " + ex.Message); }
        }

        private static bool SendLaunch(string appId)
        {
            IntPtr ac;
            if (Efl.app_control_create(out ac) != 0) return false;
            try
            {
                Efl.app_control_set_app_id(ac, appId);
                return Efl.app_control_send_launch_request(ac, IntPtr.Zero, IntPtr.Zero) == 0;
            }
            finally { Efl.app_control_destroy(ac); }
        }

        private static void SwitchSource(AppEntry e)
        {
            IntPtr ac = IntPtr.Zero;
            try
            {
                if (Efl.app_control_create(out ac) != 0) { Logger.Write("source: app_control_create failed"); return; }
                string n = e.SourceType.ToString();
                string icon = SourceIconDir + (e.SourceType >= 13 && e.SourceType <= 16 ? "r_icon_device_s_hdmi" + (e.SourceType - 12) : "r_icon_device_s_tv") + ".png";
                Efl.app_control_set_app_id(ac, "org.tizen.tv-viewer");
                Efl.app_control_add_extra_data(ac, "change_source", n);
                Efl.app_control_add_extra_data(ac, "sourceType", n);
                Efl.app_control_add_extra_data(ac, "sourceName", e.Label);
                Efl.app_control_add_extra_data(ac, "sourceIcon", icon);
                Efl.app_control_add_extra_data(ac, "mbrIndex", "-2");
                Efl.app_control_add_extra_data(ac, "__K_EX_LAUNCH_REQUESTOR", "user");
                int rc = Efl.app_control_send_launch_request(ac, IntPtr.Zero, IntPtr.Zero);
                Logger.Write("source " + e.Label + " (" + n + ") rc=" + rc);
            }
            catch (Exception ex) { Logger.Write("source error: " + ex.Message); }
            finally { if (ac != IntPtr.Zero) Efl.app_control_destroy(ac); }
        }

        private byte OnKeyTimer(IntPtr data)
        {
            AppEntry e = _pendingKey;
            _pendingKey = null;
            _keyTimer = IntPtr.Zero;
            try
            {
                if (e == null) return 0;
                int rc = Libc.system(KeyTool + e.Key);
                Logger.Write("key " + e.Key + " rc=" + rc);
                if (rc != 0 && e.AppId != null) LaunchApp(e);
            }
            catch (Exception ex) { Logger.Write("key timer: " + ex.Message); }
            return 0;
        }
    }
}
