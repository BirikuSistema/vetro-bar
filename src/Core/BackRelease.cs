namespace Launcher.Core
{
    /// <summary>
    /// Back (and Exit) close overlays when the key comes up, not when it goes down, so the app beneath never receives
    /// the release half of the key.
    /// </summary>
    internal sealed class BackRelease
    {
        public const double KernelGraceSeconds = 0.15;
        public const double LimitSeconds = 1.5;
        private string _key;
        private double _since = -1, _kernelAt = -1;

        public bool Pending { get { return _key != null; } }

        public static bool Closes(string key) { return key == "XF86Back" || key == "Escape" || key == "XF86Exit"; }

        public bool Press(string key, double now)
        {
            if (!Closes(key)) return false;
            if (_key == null) { _key = key; _since = now; _kernelAt = -1; }
            return true;
        }

        public bool WindowRelease(string key)
        {
            if (_key == null || !Closes(key)) return false;
            Cancel();
            return true;
        }

        public void KernelRelease(double now) { if (_key != null && _kernelAt < 0) _kernelAt = now; }

        public bool Due(double now)
        {
            return _key != null && ((_kernelAt >= 0 && now - _kernelAt >= KernelGraceSeconds) || now - _since >= LimitSeconds);
        }

        public void Cancel() { _key = null; _since = -1; _kernelAt = -1; }
    }
}
