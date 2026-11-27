using System;
using System.Collections.Generic;
using Tuner.Core;
using Tuner.Native;

namespace Tuner.Ui
{
    /// <summary>
    /// The progress row of the Banner and Info: start time at the left end of the line, end time right-aligned to its right end, the track
    /// (bar.png, white .22), the progress with its head (progress.png) and "N min left" above the head. Where the label goes and which time gives
    /// way to it is <see cref="ProgressRow"/>. Every text has the halo; a time that gives way goes to alpha 0 with its halo.
    /// </summary>
    internal sealed class ProgressView
    {
        public const int TimeSize = 26, ProgressH = 32, HeadAbove = 13;   // the progress picture is 32 px tall, its line 13 px below its top
        private readonly IntPtr _start, _end, _left, _prog;
        private readonly Chip _track;
        private readonly bool _halo;
        private readonly int _timeSize;
        private int _baseStartX, _baseEndX, _baseLeftX, _baseX0, _y, _lineY;
        private bool _visible;

        public ProgressView(Surface s) : this(s, true, TimeSize, null, null) { }

        public ProgressView(Surface s, bool halo, int timeSize, string font, string boldFont)
        {
            _halo = halo;
            _timeSize = timeSize;
            _track = s.Bar();
            _prog = s.Progress();
            string reg = font ?? Theme.Font500;
            string bold = boldFont ?? Theme.Font600;
            if (halo)
            {
                _start = s.HaloText(timeSize, Theme.VeilMuted, reg);
                _end = s.HaloText(timeSize, Theme.VeilMuted, reg);
                _left = s.HaloText(timeSize, Theme.TextMain, bold);
            }
            else
            {
                _start = s.Text(timeSize, Theme.VeilMuted, reg);
                _end = s.Text(timeSize, Theme.VeilMuted, reg);
                _left = s.Text(timeSize, Theme.TextMain, bold);
            }
        }

        /// <summary>The row's parts by the mock-up's names: the times, the label, the track and the progress.</summary>
        public void AddParts(IDictionary<string, Part> to)
        {
            to["tstart"] = new Part(_start, _timeSize, _halo); to["tend"] = new Part(_end, _timeSize, _halo); to["tleft"] = new Part(_left, _timeSize, _halo);
            to["track"] = new Part(_track.Obj, 0, false); to["prog"] = new Part(_prog, 0, false);
        }

        /// <param name="timesTop">The line-box top of the times and the label (the mock-up's y).</param>
        /// <param name="lineY">The top of the 6 px line.</param>
        public void Set(int x0, int x1, int timesTop, int lineY, string start, string end, string left, double fraction)
        {
            int pad = _halo ? Theme.HaloPad : 0;
            int y = Theme.TopOf(timesTop, _timeSize, pad);
            Texts.Set(_start, start);
            Texts.Set(_end, end);
            Texts.Set(_left, left);
            int startW = Texts.Advance(_start), endW = Texts.Advance(_end);
            int leftW = string.IsNullOrEmpty(left) ? 0 : Texts.Advance(_left);
            ProgressRow r = ProgressRow.Of(x0, x1, fraction, startW, endW, leftW);
            _baseStartX = x0;
            _baseEndX = x1 - endW;
            _baseLeftX = r.LeftX;
            _baseX0 = x0;
            _y = y;
            _lineY = lineY;
            _visible = true;
            if (_halo)
            {
                Theme.TintHalo(_start, r.HideStart ? 0 : Theme.VeilMuted);
                Theme.TintHalo(_end, r.HideEnd ? 0 : Theme.VeilMuted);
                Theme.TintHalo(_left, Theme.TextMain);
                Theme.MoveHalo(_start, _baseStartX, y);
                Theme.MoveHalo(_end, _baseEndX, y);
                Theme.MoveHalo(_left, _baseLeftX, y);
            }
            else
            {
                int aStart = r.HideStart ? 0 : Theme.VeilMuted;
                int aEnd = r.HideEnd ? 0 : Theme.VeilMuted;
                Efl.evas_object_color_set(_start, aStart, aStart, aStart, aStart);
                Efl.evas_object_color_set(_end, aEnd, aEnd, aEnd, aEnd);
                Efl.evas_object_color_set(_left, Theme.TextMain, Theme.TextMain, Theme.TextMain, Theme.TextMain);
                Efl.evas_object_move(_start, _baseStartX, y);
                Efl.evas_object_move(_end, _baseEndX, y);
                Efl.evas_object_move(_left, _baseLeftX, y);
            }
            _track.SetRect(x0, lineY, x1 - x0, 6);
            _track.SetWhite(Theme.RimAlpha);
            Efl.evas_object_move(_prog, x0, lineY - HeadAbove);
            Efl.evas_object_resize(_prog, r.ProgressW, ProgressH);
            Efl.evas_object_show(_prog);
        }

        public void MoveX(int dx)
        {
            if (!_visible) return;
            if (_halo)
            {
                Theme.MoveHalo(_start, _baseStartX + dx, _y);
                Theme.MoveHalo(_end, _baseEndX + dx, _y);
                Theme.MoveHalo(_left, _baseLeftX + dx, _y);
            }
            else
            {
                Efl.evas_object_move(_start, _baseStartX + dx, _y);
                Efl.evas_object_move(_end, _baseEndX + dx, _y);
                Efl.evas_object_move(_left, _baseLeftX + dx, _y);
            }
            Efl.evas_object_move(_track.Obj, _baseX0 + dx, _lineY);
            Efl.evas_object_move(_prog, _baseX0 + dx, _lineY - HeadAbove);
        }

        /// <summary>No programme: no times, no line (an empty line would say "0%").</summary>
        public void Clear()
        {
            _visible = false;
            Texts.Set(_start, "");
            Texts.Set(_end, "");
            Texts.Set(_left, "");
            _track.SetWhite(0);
            Efl.evas_object_hide(_prog);
        }
    }

    /// <summary>
    /// The channel Banner, vertically centred on the right edge of the screen in a borderless vetro-bar glass card
    /// (dock_bg.png: #003049 @ 70% opacity, 24 px corners), without channel logos, using SamsungOneUI 700/500,
    /// sliding in and out from the right edge (96 px).
    /// </summary>
    internal sealed class BannerView : IStage
    {
        private const int CardRight = 1920 - 40, CardW = 540, CardH = 216;
        private const int BaseCardX = CardRight - CardW, CardY = (1080 - CardH) / 2;
        private const int PadX = 28, BaseX0 = BaseCardX + PadX, BaseX1 = BaseCardX + CardW - PadX;
        private const int SlideDistance = 96;
        private const double SlideInSeconds = 0.18, SlideOutSeconds = 0.16;
        private const string SlideAnimKey = "banner-slide";

        private const int NumGap = 16, LogoSize = 72, LogoGap = 16, LogoTop = CardY + 12;
        private const int NumSize = 70, NumTop = CardY + 14, NameSize = 40, NameTop = CardY + 30;
        private const int TitleSize = 26, TitleTop = CardY + 94, NoneSize = 24;
        private const int TimesTop = CardY + 138, LineY = CardY + 178;

        private readonly Surface _s;
        private readonly Veil _veil;
        private readonly IntPtr _card, _num, _name, _title, _none;
        private readonly Logo _logo;
        private readonly ProgressView _progress;

        private int _dx;
        private int _numAdv, _nameBaseX;
        private string _nameText = "";
        private string _logoPath;
        private bool _hasLogo;
        private bool _hasProg;
        private bool _noProg;
        private string _progTitle = "", _progStart = "", _progEnd = "", _progLeft = "";
        private double _progFraction;

        public BannerView(Surface s, Veil veil)
        {
            _s = s; _veil = veil;
            _card = s.DockGlass();
            Efl.evas_object_color_set(_card, 255, 255, 255, 255);
            Efl.evas_object_resize(_card, CardW, CardH);
            _num = s.Text(NumSize, Theme.TextMain, Theme.DockBoldFont);
            _name = s.Text(NameSize, Theme.TextMain, Theme.DockBoldFont);
            _logo = s.Logo();
            _title = s.Text(TitleSize, Theme.VeilTitle, Theme.DockFont);
            _none = s.Text(NoneSize, Theme.VeilFaint, Theme.DockFont);
            Texts.Set(_none, "No programme information");
            _progress = new ProgressView(s, false, 24, Theme.DockFont, Theme.DockBoldFont);
            _parts["num"] = new Part(_num, NumSize, false); _parts["name"] = new Part(_name, NameSize, false);
            _parts["title"] = new Part(_title, TitleSize, false); _parts["none"] = new Part(_none, NoneSize, false);
            _progress.AddParts(_parts);
            UpdateContent();
        }

        private readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>();
        public Surface Surface { get { return _s; } }
        public IEnumerable<KeyValuePair<string, Part>> Parts { get { return _parts; } }
        public bool GroupFade { get { return true; } }

        public bool Visible { get { return _s.Visible; } }

        public void Hide()
        {
            _veil.SetBanner(false);
            if (!_s.Visible) return;
            int startDx = _dx;
            _s.Leave(SlideOutSeconds, Motion.In);
            Anim.Run(SlideAnimKey, SlideOutSeconds, 0, Motion.In, p => ApplyOffset(Motion.Lerp(startDx, SlideDistance, p)));
        }

        public void ShowChannel(string number, string name, string logo = null, bool room = false)
        {
            bool wasVisible = _s.Visible;
            Texts.Set(_num, number);
            _numAdv = Texts.Advance(_num);
            _nameText = name ?? "";
            _logoPath = logo;
            _hasProg = false;
            _noProg = false;
            _veil.SetBanner(false);

            if (!wasVisible)
            {
                _dx = SlideDistance;
                UpdateContent();
                _s.Show(SlideInSeconds, 0);
                Anim.Run(SlideAnimKey, SlideInSeconds, 0, Motion.Ease, p => ApplyOffset(Motion.Lerp(SlideDistance, 0, p)));
            }
            else
            {
                UpdateContent();
                _s.Show(SlideInSeconds, 0);
            }
        }

        public void SetLogo(string logo, bool room)
        {
            _logoPath = logo;
            UpdateContent();
        }

        public void SetProgram(string title, string start, string end, string left, double fraction)
        {
            _hasProg = true;
            _noProg = false;
            _progTitle = title ?? "";
            _progStart = start ?? "";
            _progEnd = end ?? "";
            _progLeft = left ?? "";
            _progFraction = fraction;
            UpdateContent();
        }

        public void SetNoProgram()
        {
            _hasProg = false;
            _noProg = true;
            _progTitle = "";
            UpdateContent();
        }

        private void UpdateContent()
        {
            _nameBaseX = BaseX0 + _numAdv + NumGap;
            _hasLogo = !string.IsNullOrEmpty(_logoPath);
            int nameRight = _hasLogo ? BaseX1 - LogoSize - LogoGap : BaseX1;
            int maxNameW = Math.Max(40, nameRight - _nameBaseX);
            Surface.SetFitted(_name, _nameText, maxNameW);
            if (_hasLogo) _logo.Show(_logoPath, BaseX1 - LogoSize + _dx, LogoTop, LogoSize);
            else _logo.Hide();

            int fullW = BaseX1 - BaseX0;
            if (_hasProg)
            {
                Efl.evas_object_hide(_none);
                Surface.SetFitted(_title, _progTitle, fullW);
                _progress.Set(BaseX0, BaseX1, TimesTop, LineY, _progStart, _progEnd, "", _progFraction);
            }
            else if (_noProg)
            {
                Texts.Set(_title, "");
                Surface.SetFitted(_none, GuideDate.NoInfoText, fullW);
                Efl.evas_object_show(_none);
                _progress.Clear();
            }
            else
            {
                Texts.Set(_title, "");
                Efl.evas_object_hide(_none);
                _progress.Clear();
            }
            ApplyOffset(_dx);
        }

        private void ApplyOffset(int dx)
        {
            _dx = dx;
            Efl.evas_object_move(_card, BaseCardX + dx, CardY);
            Efl.evas_object_move(_num, BaseX0 + dx, Theme.TopOf(NumTop, NumSize, 0));
            Efl.evas_object_move(_name, _nameBaseX + dx, Theme.TopOf(NameTop, NameSize, 0));
            if (_hasLogo) Efl.evas_object_move(_logo.Obj, BaseX1 - LogoSize + dx, LogoTop);

            if (_hasProg)
            {
                Efl.evas_object_move(_title, BaseX0 + dx, Theme.TopOf(TitleTop, TitleSize, 0));
                _progress.MoveX(dx);
            }
            else if (_noProg)
            {
                Efl.evas_object_move(_none, BaseX0 + dx, Theme.TopFor(Theme.Baseline(TitleTop, TitleSize), NoneSize, 0));
            }
        }
    }

    /// <summary>
    /// The channel list (OK), right side, centred on the screen's height: eleven rows around the selection, on a panel of the osd 123's
    /// material (radius 40, no shadow). The selected row is one ice capsule with its number and name in slate; the numbers are right-aligned.
    /// </summary>
    internal sealed class ListView
    {
        public const int Rows = 11, RowH = 64, W = 540, X = 1920 - 40 - W, Pad = 24, LogoSize = 50, H = Rows * RowH + 2 * Pad, Y = 540 - H / 2;
        private const int NumRight = X + 86;
        private readonly Surface _s;
        private readonly Chip _focus;
        private readonly IntPtr[] _num = new IntPtr[Rows], _name = new IntPtr[Rows];
        private readonly Logo[] _logo = new Logo[Rows];

        private readonly IntPtr _card;

        public ListView(Surface s)
        {
            _s = s;
            _card = s.DockGlass();
            Efl.evas_object_color_set(_card, 255, 255, 255, 255);
            Efl.evas_object_move(_card, X, Y);
            Efl.evas_object_resize(_card, W, H);
            _focus = s.Cap();
            for (int i = 0; i < Rows; i++) { _logo[i] = s.Logo(); _num[i] = s.Text(32, Theme.TextMuted, Theme.DockFont); _name[i] = s.Text(32, Theme.TextMain, Theme.DockBoldFont); }
        }

        public bool Visible { get { return _s.Visible; } }
        public void Hide() { _s.Hide(); }

        /// <param name="logo">The logo file of a channel (null for none); <paramref name="room"/> keeps a column for it in every row.</param>
        public void Draw(ChannelList list, int selected, Func<Channel, string> logo = null, bool room = false)
        {
            int top = Math.Max(0, Math.Min(selected - Rows / 2, list.Count - Rows));
            for (int r = 0; r < Rows; r++)
            {
                int idx = top + r;
                if (idx >= list.Count) { Efl.evas_object_hide(_num[r]); Efl.evas_object_hide(_name[r]); _logo[r].Hide(); continue; }
                Efl.evas_object_show(_num[r]); Efl.evas_object_show(_name[r]);
                Channel c = list[idx];
                int y = Y + Pad + r * RowH;
                bool sel = idx == selected;
                if (sel) { _focus.SetRect(X + 16, y + 2, W - 32, RowH - 4); _focus.SetRadius((RowH - 4) / 2); _focus.SetTint(Theme.Ice, Theme.FocusAlpha); }
                Texts.Set(_num[r], c.Major.ToString());
                if (sel) Theme.TintText(_num[r], Theme.Slate, 255); else Theme.TintText(_num[r], 0xFFFFFF, Theme.TextMuted);
                if (sel) Theme.TintText(_name[r], Theme.Slate, 255); else Theme.TintText(_name[r], 0xFFFFFF, Theme.TextMain);
                int nameX = room ? X + 168 : X + 112;
                Surface.SetFitted(_name[r], c.Name, X + W - 36 - nameX);
                int ty = Theme.TopForCentre(y, RowH, 32);                         // capitals centred in the row (Evas metrics, not a measured offset)
                Efl.evas_object_move(_num[r], NumRight - Texts.Advance(_num[r]), ty);
                Efl.evas_object_move(_name[r], nameX, ty);
                string file = logo == null ? null : logo(c);
                if (file != null) _logo[r].Show(file, X + 104, y + (RowH - LogoSize) / 2, LogoSize); else _logo[r].Hide();
            }
            _s.Show();
        }
    }

    /// <summary>
    /// The Channel pad (the remote has no digit keys): what is typed, the name of that channel, and a 4x3 grid, on the right, centred on the
    /// screen's height, on a panel of the osd 123's material (radius 40, no shadow). The keys are capsules (white .14); the selected one is the
    /// ice capsule with its label in slate.
    /// </summary>
    internal sealed class PadView
    {
        private const int X = 1284, W = 540, Pad = 34, KeyW = 148, KeyH = 76, Gap = 14, GridY = 232, H = 232 + 4 * 76 + 3 * 14 + 38, Y = 540 - H / 2;
        // Baselines of the three lines above the keys, from the panel's top (the mock-up's). Placed by baseline: Evas's ascent is taller than the
        // font's own (see TextMetrics), so a plain y left the heading 3 px and the typed number 7 px lower than drawn.
        private const int TitleBase = 54, TypedBase = 133, PreviewBase = 192;
        private readonly Surface _s;
        private readonly IntPtr _title, _typed, _preview;
        private readonly Logo _logo;
        private readonly Chip[] _key = new Chip[NumberEntry.Rows * NumberEntry.Cols];
        private readonly IntPtr[] _label = new IntPtr[NumberEntry.Rows * NumberEntry.Cols];
        private readonly Chip _focus;

        public PadView(Surface s)
        {
            _s = s;
            s.Panel().SetRect(X, Y, W, H, 40);
            _title = s.Text(26, Theme.TextMuted);
            Texts.Set(_title, "Channel number");
            Efl.evas_object_move(_title, X + Pad, Theme.TopFor(Y + TitleBase, 26));
            _typed = s.Text(76, Theme.TextMain);
            Efl.evas_object_move(_typed, X + Pad, Theme.TopFor(Y + TypedBase, 76));
            _preview = s.Text(28, Theme.TextMuted);
            _logo = s.Logo();
            Efl.evas_object_move(_preview, X + Pad, Theme.TopFor(Y + PreviewBase, 28));
            for (int i = 0; i < _key.Length; i++)
            {
                int r = i / NumberEntry.Cols, c = i % NumberEntry.Cols;
                _key[i] = s.Cap();
                _key[i].SetRect(Left(c), Top(r), KeyW, KeyH);
                _key[i].SetRadius(KeyH / 2);
            }
            _focus = s.Cap();
            _focus.SetRadius(KeyH / 2);
            for (int i = 0; i < _label.Length; i++)
            {
                string t = NumberEntry.Label(i / NumberEntry.Cols, i % NumberEntry.Cols);
                _label[i] = s.Text(t.Length > 1 ? 28 : 40, Theme.TextMain);
                Texts.Set(_label[i], t);
                int r = i / NumberEntry.Cols, c = i % NumberEntry.Cols;
                Efl.evas_object_move(_label[i], Left(c) + (KeyW - Texts.Advance(_label[i])) / 2, Theme.TopForCentre(Top(r), KeyH, t.Length > 1 ? 28 : 40));   // capitals and digits centred on the key
            }
        }

        private static int Left(int col) { return X + Pad + col * (KeyW + Gap); }
        private static int Top(int row) { return Y + GridY + row * (KeyH + Gap); }

        public bool Visible { get { return _s.Visible; } }
        public void Hide() { _s.Hide(); }

        /// <summary>Redraws the pad. <paramref name="preview"/> is the name of the channel the typed number would go to ("" for none).</summary>
        public void Draw(NumberEntry e, string preview, string logo = null)
        {
            Texts.Set(_typed, e.Text.Length > 0 ? e.Text : "–");
            Surface.SetFitted(_preview, preview, W - 2 * Pad - (logo != null ? 52 : 0));
            Efl.evas_object_move(_preview, X + Pad + (logo != null ? 52 : 0), Theme.TopFor(Y + PreviewBase, 28));
            if (logo != null) _logo.Show(logo, X + Pad, Y + 158, 44); else _logo.Hide();
            int focus = e.Row * NumberEntry.Cols + e.Col;
            for (int i = 0; i < _key.Length; i++)
            {
                _key[i].SetWhite(i == focus ? 0 : Theme.KeyAlpha);              // the focus capsule takes the key's place
                if (i == focus) Theme.TintText(_label[i], Theme.Slate, 255); else Theme.TintText(_label[i], 0xFFFFFF, Theme.TextMain);
            }
            _focus.SetRect(Left(e.Col), Top(e.Row), KeyW, KeyH);
            _focus.SetTint(Theme.Ice, Theme.FocusAlpha);
            _s.Show();
        }
    }

    /// <summary>
    /// The Audio toast (Left), top right: two lines, Audio and Subtitles, on a panel of the osd 123's material (radius 40, no shadow). Each line is
    /// the remote key that changes it (Left, Down), its label (white .76) and its value (600, white). The line just changed is at full strength;
    /// the other is quieter (key .40, label .55, value .70). The panel's size and every column are fixed, so nothing moves whatever the values
    /// are or when they arrive: a value too long for its column is cut with an ellipsis.
    /// </summary>
    internal sealed class AudioToastView
    {
        public const int Right = 1920 - 96, Y = 96, PadX = 40, PadY = 20, RowH = 56, H = 2 * PadY + 2 * RowH, Radius = 40;
        // The value column fits "Italian (hard of hearing)" (335 px in SamsungOneUI 600 at 32) with a little to spare.
        public const int KeySize = 40, KeyGap = 16, LabelCol = 160, ValueW = 352, Size = 32;
        public const int W = PadX + KeySize + KeyGap + LabelCol + ValueW + PadX, X = Right - W;
        public const int KeyX = X + PadX, LabelX = KeyX + KeySize + KeyGap, ValueX = LabelX + LabelCol;
        /// <summary>Alphas of the line just changed and of the other one: key, label, value.</summary>
        public const int KeyOn = 217, LabelOn = 194, ValueOn = 255, KeyOff = 102, LabelOff = 140, ValueOff = 178;
        private readonly Surface _s;
        private readonly IntPtr[] _key = new IntPtr[2], _label = new IntPtr[2], _value = new IntPtr[2];

        public AudioToastView(Surface s)
        {
            _s = s;
            s.Panel().SetRect(X, Y, W, H, Radius);
            string[] keys = { "key_left", "key_down" }, labels = { "Audio", "Subtitles" };
            for (int i = 0; i < 2; i++)
            {
                int top = Y + PadY + i * RowH;
                _key[i] = s.Picture(keys[i]);
                Efl.evas_object_move(_key[i], KeyX, top + (RowH - KeySize) / 2);
                Efl.evas_object_resize(_key[i], KeySize, KeySize);
                Efl.evas_object_show(_key[i]);
                _label[i] = s.Text(Size, LabelOn, Theme.Font500);
                Texts.Set(_label[i], labels[i]);
                Efl.evas_object_move(_label[i], LabelX, Theme.TopForCentre(top, RowH, Size));   // capitals centred in the row; the value shares the baseline
                _value[i] = s.Text(Size, ValueOn, Theme.Font600);
                Efl.evas_object_move(_value[i], ValueX, Theme.TopForCentre(top, RowH, Size));
            }
        }

        public bool Visible { get { return _s.Visible; } }
        public void Hide() { _s.Hide(); }

        /// <param name="audio">The Audio line's value ("Italian", "English (2)", "Not available").</param>
        /// <param name="subtitles">The Subtitles line's value ("Off", "English", "None on this channel"...; "" while it is being read).</param>
        /// <param name="subtitlesChanged">Down changed the subtitles: their line is the full-strength one (otherwise the Audio line is).</param>
        public void Show(string audio, string subtitles, bool subtitlesChanged)
        {
            Surface.SetFitted(_value[0], audio ?? "", ValueW);
            Surface.SetFitted(_value[1], subtitles ?? "", ValueW);
            for (int i = 0; i < 2; i++)
            {
                bool on = (i == 1) == subtitlesChanged;
                int k = on ? KeyOn : KeyOff;
                Efl.evas_object_color_set(_key[i], k, k, k, k);
                Theme.TintText(_label[i], 0xFFFFFF, on ? LabelOn : LabelOff);
                Theme.TintText(_value[i], 0xFFFFFF, on ? ValueOn : ValueOff);
            }
            _s.Show();
        }
    }

    /// <summary>A one-line message, top right ("No channel 12"): the osd 123's capsule (rim and slate body, radius half the height), no shadow.</summary>
    internal sealed class ToastView
    {
        private const int Pad = 40, H = 72, Right = 1920 - 96, Y = 96, Size = 32;
        private readonly Surface _s;
        private readonly IntPtr _card;
        private readonly IntPtr _text;

        public ToastView(Surface s) { _s = s; _card = s.DockGlass(); Efl.evas_object_color_set(_card, 255, 255, 255, 255); _text = s.Text(Size, Theme.TextMain, Theme.DockBoldFont); }

        public bool Visible { get { return _s.Visible; } }
        public void Hide() { _s.Hide(); }

        public void Show(string text)
        {
            Texts.Set(_text, text);
            int w = Texts.Advance(_text) + 2 * Pad;
            Efl.evas_object_move(_card, Right - w, Y);
            Efl.evas_object_resize(_card, w, H);
            Efl.evas_object_move(_text, Right - w + Pad, Theme.TopForCentre(Y, H, Size));
            _s.Show();
        }
    }
}
