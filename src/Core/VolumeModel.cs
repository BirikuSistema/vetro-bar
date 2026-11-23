using System;

namespace Launcher.Core
{
    /// <summary>What a volume key does and how the pill shows it. Pure logic, testable without a TV.</summary>
    internal static class VolumeModel
    {
        public const int Max = 100;

        public static int Next(int current, int dir, int step)
        {
            int next = current + (dir < 0 ? -step : step);
            return Math.Max(0, Math.Min(Max, next));
        }

        public static int IconLevel(int volume, bool muted)
        {
            if (muted || volume <= 0) return 0;
            return volume < 40 ? 1 : 2;
        }

        public static int FillHeight(int volume, bool muted, int track)
        {
            if (muted || volume <= 0) return 0;
            return Math.Max(1, Math.Min(track, (int)Math.Round(track * (double)Math.Min(volume, Max) / Max)));
        }
    }

    internal static class VolumeFade
    {
        public const double FadeInSeconds = 0.14;
        public const double HoldSeconds = 1.60;
        public const double FadeOutSeconds = 0.24;

        public static double Alpha(double now, double shownAt, double until)
        {
            if (now < shownAt) return 0;
            if (now - shownAt < FadeInSeconds) return (now - shownAt) / FadeInSeconds;
            if (now <= until) return 1.0;
            double outElapsed = now - until;
            if (outElapsed >= FadeOutSeconds) return 0;
            return 1.0 - (outElapsed / FadeOutSeconds);
        }

        public static bool Holding(double alpha, double now, double until)
        {
            return alpha >= 1.0 && now < until;
        }
    }
}
