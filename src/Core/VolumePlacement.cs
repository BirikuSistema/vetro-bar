namespace Launcher.Core
{
    internal enum VolumeEdge { Left, Right, Top, Bottom, Hidden }

    /// <summary>
    /// Geometry of the volume pill for any screen edge and screen size.
    /// </summary>
    internal sealed class VolumePlacement
    {
        public const int Margin = 96;
        public const int Pad = 4;
        public const int Thickness = 88, LongVertical = 480, LongHorizontal = 640;
        public const int Inset = 12, IconSize = 40, NumberSize = 32;
        public const int NumberBand = 56, IconBand = 64;
        public const int HIconX = 24, HTrackX = 80, HNumberRoom = 100, HNumberRight = 28;

        public bool Vertical;
        public int WinX, WinY, WinW, WinH;
        public int BodyX, BodyY, BodyW, BodyH;
        public int TrackX, TrackY, TrackW, TrackH;
        public int IconX, IconY;
        public int NumberCenterX, NumberCenterY, NumberRight;

        public static VolumeEdge ParseEdge(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "right": return VolumeEdge.Right;
                case "top": return VolumeEdge.Top;
                case "bottom": return VolumeEdge.Bottom;
                case "hidden": return VolumeEdge.Hidden;
                default: return VolumeEdge.Left;
            }
        }

        public static VolumePlacement For(VolumeEdge edge, int screenW, int screenH)
        {
            var p = new VolumePlacement();
            p.Vertical = edge == VolumeEdge.Left || edge == VolumeEdge.Right;
            p.BodyW = p.Vertical ? Thickness : LongHorizontal;
            p.BodyH = p.Vertical ? LongVertical : Thickness;
            p.BodyX = edge == VolumeEdge.Left ? Margin : edge == VolumeEdge.Right ? screenW - Margin - p.BodyW : (screenW - p.BodyW) / 2;
            p.BodyY = edge == VolumeEdge.Top ? Margin : edge == VolumeEdge.Bottom ? screenH - Margin - p.BodyH : (screenH - p.BodyH) / 2;
            p.WinX = p.BodyX - Pad; p.WinY = p.BodyY - Pad; p.WinW = p.BodyW + 2 * Pad; p.WinH = p.BodyH + 2 * Pad;
            if (p.Vertical)
            {
                p.TrackX = p.BodyX + Inset; p.TrackW = p.BodyW - 2 * Inset;
                p.TrackY = p.BodyY + NumberBand; p.TrackH = p.BodyH - NumberBand - IconBand - Inset;
                p.IconX = p.BodyX + (p.BodyW - IconSize) / 2; p.IconY = p.BodyY + p.BodyH - Inset - IconSize - 6;
                p.NumberCenterX = p.BodyX + p.BodyW / 2; p.NumberCenterY = p.BodyY + NumberBand / 2 + 4;
            }
            else
            {
                p.IconX = p.BodyX + HIconX; p.IconY = p.BodyY + (p.BodyH - IconSize) / 2;
                p.TrackX = p.BodyX + HTrackX; p.TrackY = p.BodyY + Inset;
                p.TrackW = p.BodyW - HTrackX - HNumberRoom; p.TrackH = p.BodyH - 2 * Inset;
                p.NumberRight = p.BodyX + p.BodyW - HNumberRight; p.NumberCenterY = p.BodyY + p.BodyH / 2;
            }
            return p;
        }
    }
}
