using System;

namespace Launcher.Core
{
    internal sealed class FrameStep
    {
        private readonly double _max;
        private double _last, _interval = 1.0 / 60.0;
        private bool _first = true, _resume;

        public FrameStep(double max) { _max = max; }
        public double Interval { get { return _interval; } }
        public void Begin(double now) { _last = now; _first = true; _resume = false; }
        public void Resume() { _first = true; _resume = true; }

        public double Next(double now)
        {
            double dt;
            if (_first && _resume) dt = _interval;
            else
            {
                dt = Math.Min(_max, Math.Max(0, now - _last));
                if (!_first) _interval = dt;
            }
            _first = false; _resume = false;
            _last = now;
            return dt;
        }
    }

    internal sealed class PadMotion
    {
        public const double FadeSeconds = 0.16, SlideRate = 18;
        public static bool Instant { get; set; }
        public double Fade, FocusX;

        public bool Step(double dt, bool closing, bool onDone, double target, out bool gone)
        {
            if (Instant)
            {
                gone = closing;
                Fade = closing ? 0 : 1;
                if (!onDone) FocusX = target;
                return false;
            }
            Fade = closing ? Fade - dt / FadeSeconds : Math.Min(1, Fade + dt / FadeSeconds);
            gone = closing && Fade <= 0;
            if (gone) { Fade = 0; return false; }
            if (!onDone)
            {
                FocusX += (target - FocusX) * Math.Min(1, dt * SlideRate);
                if (Math.Abs(target - FocusX) < 0.5) FocusX = target;
            }
            return closing || Fade < 1 || (!onDone && FocusX != target);
        }
    }
}
