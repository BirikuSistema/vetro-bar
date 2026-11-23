using System;
using System.Globalization;
using System.IO;

namespace Launcher.Core
{
    /// <summary>
    /// Signals to Vetro TV Tuner (/tmp/vetro-dock.open) that the dock or settings overlay is on screen
    /// so the tuner leaves remote keys alone while our windows are visible.
    /// </summary>
    internal static class OverlayFlag
    {
        public const string DockPath = "/tmp/vetro-dock.open";
        public const string PadPath = "/tmp/vetro-numpad.open";

        public static string Pid;
        private static string PidText()
        {
            return Pid ?? (Pid = System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
        }

        public static void Mark(string path, bool on, Action<string> log = null)
        {
            try
            {
                if (on) File.WriteAllText(path, PidText());
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                if (log != null) log("flag " + path + ": " + ex.Message);
            }
        }
    }
}
