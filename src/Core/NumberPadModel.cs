using System;
using System.Collections.Generic;
using System.Text;

namespace Launcher.Core
{
    internal enum PadResult { None, Moved, Typed, Erased, Page, Send, SendKey, Close }
    internal enum CellKind { Digit, Erase, ToColors, ToDigits, Key }

    internal sealed class PadCell
    {
        public CellKind Kind; public string Label, Icon, Tip, Key; public int Color, Width, Gap;
        public string Mark;
        public PadCell(CellKind kind, string label, string icon, string tip, string key, int color, int width, int gap)
        { Kind = kind; Label = label; Icon = icon; Tip = tip; Key = key; Color = color; Width = width; Gap = gap; }
    }

    internal sealed class NumberPadModel
    {
        public const int MaxTyped = 5;
        public const int DigitsPage = 0, ColorsPage = 1;
        public const int RedColor = 0xE5484D, GreenColor = 0x30A46C, YellowColor = 0xF2B01E, BlueColor = 0x3E7BFA;

        public static readonly PadCell[] Digits = BuildDigits();
        public static readonly PadCell[] Colors = BuildColors();
        public static readonly string[] CoreGrabKeys = new[] { "Left", "Right", "Up", "Down", "Return", "XF86Back" };
        public static readonly string[] GrabKeys = new[]
        {
            "Left", "Right", "Up", "Down", "Return", "KP_Enter", "Select", "BackSpace", "XF86Back", "Escape", "XF86Exit",
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"
        };

        private static bool IsKeyName(string k)
        {
            if (string.IsNullOrEmpty(k)) return false;
            foreach (char c in k) if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }
        private readonly StringBuilder _typed = new StringBuilder();

        public int Page { get; private set; }
        public int Focus { get; private set; }
        public bool OnDone { get; private set; }
        public string Typed { get { return _typed.ToString(); } }
        public string PendingKey { get; private set; }

        public PadCell[] Row { get { return Page == DigitsPage ? Digits : Colors; } }
        public PadCell Current { get { return OnDone ? null : Row[Focus]; } }

        private static PadCell[] BuildDigits()
        {
            var l = new List<PadCell>();
            foreach (char c in "1234567890-") l.Add(new PadCell(CellKind.Digit, c.ToString(), null, null, null, 0, 72, 0));
            l.Add(new PadCell(CellKind.Erase, null, "pad_bs", null, null, 0, 72, 24));
            l.Add(new PadCell(CellKind.ToColors, null, "pad_colors", null, null, 0, 72, 0) { Mark = "pad_colors_dots" });
            return l.ToArray();
        }

        private static PadCell[] BuildColors()
        {
            return new[]
            {
                new PadCell(CellKind.ToDigits, "123", "pad_back", null, null, 0, 104, 0),
                new PadCell(CellKind.Key, "A", null, "Red A", "XF86Red", RedColor, 72, 16),
                new PadCell(CellKind.Key, "B", null, "Green B", "XF86Green", GreenColor, 72, 0),
                new PadCell(CellKind.Key, "C", null, "Yellow C", "XF86Yellow", YellowColor, 72, 0),
                new PadCell(CellKind.Key, "D", null, "Blue D", "XF86Blue", BlueColor, 72, 0),
                new PadCell(CellKind.Key, null, "pad_ttx", "Teletext", "XF86TTXMIX", 0, 72, 24),
                new PadCell(CellKind.Key, null, "pad_timer", "Sleep timer", "XF86Sleep", 0, 72, 0),
                new PadCell(CellKind.Key, null, "pad_info", "Info", "XF86Info", 0, 72, 24),
            };
        }

        public static int BodyWidth(PadCell[] row)
        {
            int w = 48;
            foreach (PadCell c in row) w += c.Gap + c.Width;
            return w;
        }

        public void Reset() { _typed.Length = 0; Page = DigitsPage; Focus = 0; OnDone = false; PendingKey = null; }

        public PadResult Key(string key)
        {
            switch (key)
            {
                case "Left": if (OnDone || Focus == 0) return PadResult.None; Focus--; return PadResult.Moved;
                case "Right": if (OnDone || Focus == Row.Length - 1) return PadResult.None; Focus++; return PadResult.Moved;
                case "Up": if (Page != DigitsPage || OnDone) return PadResult.None; OnDone = true; return PadResult.Moved;
                case "Down": if (!OnDone) return PadResult.None; OnDone = false; return PadResult.Moved;
                case "Return": case "KP_Enter": case "Select": return Press();
                case "BackSpace": return Erase();
                case "XF86Back": case "Escape": case "XF86Exit": return PadResult.Close;
            }
            if (Page == DigitsPage && key != null && key.Length == 1 && key[0] >= '0' && key[0] <= '9') return Add(key);
            return PadResult.None;
        }

        private PadResult Press()
        {
            if (OnDone) return _typed.Length > 0 ? PadResult.Send : PadResult.Close;
            PadCell c = Row[Focus];
            switch (c.Kind)
            {
                case CellKind.Digit: return Add(c.Label);
                case CellKind.Erase: return Erase();
                case CellKind.ToColors: Page = ColorsPage; Focus = 1; return PadResult.Page;
                case CellKind.ToDigits: Page = DigitsPage; Focus = Digits.Length - 1; return PadResult.Page;
                default: PendingKey = c.Key; return PadResult.SendKey;
            }
        }

        private PadResult Add(string ch)
        {
            if (_typed.Length >= MaxTyped) return PadResult.None;
            if (ch == "-" && (_typed.Length == 0 || _typed[_typed.Length - 1] == '-')) return PadResult.None;
            _typed.Append(ch);
            return PadResult.Typed;
        }

        private PadResult Erase()
        {
            if (_typed.Length == 0) return PadResult.None;
            _typed.Length--;
            return PadResult.Erased;
        }

        private static string Wrap(string keys, string keyTool, string extra)
        {
            var sb = new StringBuilder("(sleep 0.25");
            foreach (string k in keys.Split(' '))
                if (k.Length > 0 && IsKeyName(k)) sb.Append("; ").Append(keyTool).Append(k);
            return sb.Append(extra).Append(") >/dev/null 2>&1 &").ToString();
        }

        public static string SendCommand(string typed, string keyTool)
        {
            var names = new StringBuilder();
            var tuner = new StringBuilder();
            var number = new StringBuilder();
            foreach (char c in typed)
            {
                if (c >= '0' && c <= '9') { names.Append(c).Append(' '); number.Append(c); }
                else if (c == '-') names.Append("minus ");
            }
            var byName = new StringBuilder();
            foreach (string k in names.ToString().Split(' '))
                if (k.Length > 0 && IsKeyName(k)) byName.Append(' ').Append(keyTool).Append(k).Append(';');
            if (number.Length > 0) tuner.Append(" echo 'tune ").Append(number).Append("' > ").Append(TunerPipe).Append(';');
            if (byName.Length == 0) return "(sleep 0.25) >/dev/null 2>&1 &";
            if (tuner.Length == 0) return "(sleep 0.25;" + byName.ToString().TrimEnd(';') + ") >/dev/null 2>&1 &";
            return "(sleep 0.25; p=$(cat " + TunerPid + " 2>/dev/null); if [ -n \"$p\" ] && [ -d /proc/$p ] && [ -e " + TunerPipe + ".tv ] && [ -p " + TunerPipe + " ]; then" + tuner
                + " else" + byName + " fi) >/dev/null 2>&1 &";
        }

        private const string TunerPid = "/home/owner/vetro-tuner.pid", TunerPipe = "/tmp/vetro-tuner.ctl";
        private const string TunerShown = "p=$(cat " + TunerPid + " 2>/dev/null); if [ -n \"$p\" ] && [ -d /proc/$p ] && [ -e " + TunerPipe + ".shown ] && [ -p " + TunerPipe + " ]; then ";

        public static bool ForLiveTv(string key)
        {
            return key == "XF86Red" || key == "XF86Green" || key == "XF86Yellow" || key == "XF86Blue" || key == "XF86TTXMIX";
        }

        public static string SendKeyCommand(string key, string keyTool)
        {
            string safe = key != null && IsKeyName(key) ? key : "";
            if (safe == "XF86Info")
                return "(sleep 0.25; " + TunerShown + "echo info > " + TunerPipe + "; else " + keyTool + safe + "; fi) >/dev/null 2>&1 &";
            if (safe.Length > 0 && ForLiveTv(safe))
                return "(sleep 0.25; " + TunerShown + "echo hide > " + TunerPipe + "; sleep 0.3; fi; " + keyTool + safe + ") >/dev/null 2>&1 &";
            return Wrap(safe, keyTool, "");
        }
    }
}
