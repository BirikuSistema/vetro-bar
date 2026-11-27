using System;
using System.Collections.Generic;
using Tuner.Core;
using Tuner.Native;

namespace Tuner.Ui
{
    /// <summary>
    /// The TV guide on screen, framed in vetro-bar's borderless glass panel (dock_bg.png: #003049 @ 70%, 24 px corners),
    /// using SamsungOneUI 700/500, vetro-bar accent teal (#0088A3) for live programmes, crisp white focus with #003049 ink,
    /// no channel logos, and smooth vertical slide-in / slide-out animation.
    /// </summary>
    internal sealed class GuideView : IStage
    {
        private const int CardX = 32, CardY = 32, CardW = 1856, CardH = 984;
        private const int SlideDistance = 36;
        private const double SlideInSeconds = 0.22, SlideOutSeconds = 0.16;
        private const string SlideAnimKey = "guide-slide";

        private readonly Surface _s;
        private readonly Veil _veil;
        private readonly IntPtr _bg;
        private readonly IntPtr[] _lines = new IntPtr[GuideScene.MaxLines];
        private readonly Chip[] _fills = new Chip[GuideScene.MaxFills];
        private readonly IntPtr[] _hatches = new IntPtr[GuideScene.MaxHatches];
        private readonly IntPtr[] _hatchEnds = new IntPtr[GuideScene.MaxHatchEnds];
        private readonly Chip _focus;
        private readonly Logo[] _logos = new Logo[GuideScene.MaxLogos];
        private readonly Slot[] _texts = new Slot[GuideScene.MaxTexts - 3];
        private readonly Dictionary<string, Slot> _keyed = new Dictionary<string, Slot>();
        private readonly Dictionary<string, IntPtr> _keys = new Dictionary<string, IntPtr>();
        private readonly HashSet<string> _keysShown = new HashSet<string>(), _keyedShown = new HashSet<string>();
        private readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>();
        private readonly IntPtr _probe;

        private readonly int[] _lineBaseX = new int[GuideScene.MaxLines], _lineBaseY = new int[GuideScene.MaxLines];
        private readonly int[] _fillBaseX = new int[GuideScene.MaxFills], _fillBaseY = new int[GuideScene.MaxFills];
        private readonly int[] _hatchBaseX = new int[GuideScene.MaxHatches], _hatchBaseY = new int[GuideScene.MaxHatches];
        private readonly int[] _hatchEndBaseX = new int[GuideScene.MaxHatchEnds], _hatchEndBaseY = new int[GuideScene.MaxHatchEnds];
        private readonly int[] _textBaseX = new int[GuideScene.MaxTexts - 3], _textBaseY = new int[GuideScene.MaxTexts - 3];
        private readonly Dictionary<string, int> _keyBaseX = new Dictionary<string, int>(), _keyBaseY = new Dictionary<string, int>();
        private int _focusBaseX, _focusBaseY;
        private bool _hasFocus;
        private int _dy;
        private int _linesUsed, _fillsUsed, _hatchesUsed, _hatchEndsUsed, _textsUsed;

        private sealed class Slot { public IntPtr T; public string Text, Font; public int Size = -1, MaxW = -1; public int BaseX, BaseY; }

        public GuideView(Surface s, Veil veil)
        {
            _s = s; _veil = veil;
            _measureNative = MeasureNative;
            _bg = s.DockGlass();
            Efl.evas_object_color_set(_bg, 255, 255, 255, 255);
            Efl.evas_object_move(_bg, CardX, CardY);
            Efl.evas_object_resize(_bg, CardW, CardH);

            for (int i = 0; i < _lines.Length; i++) _lines[i] = s.Rect();
            for (int i = 0; i < _fills.Length; i++) _fills[i] = s.Pill();
            for (int i = 0; i < _hatches.Length; i++) _hatches[i] = s.Tile("hatch", GuideScene.HatchTile);
            for (int i = 0; i < _hatchEnds.Length; i++) _hatchEnds[i] = s.Tile("hatch_ends", HatchEndsSlots * GuideScene.HatchEndW, GuideLayout.CellH);
            _focus = s.Pill();
            for (int i = 0; i < _logos.Length; i++) _logos[i] = s.Logo();
            for (int i = 0; i < _texts.Length; i++) _texts[i] = NewSlot();
            foreach (string k in GuideScene.TextKeys) _keyed[k] = NewSlot();
            foreach (string k in GuideScene.KeyNames) _keys[k] = s.Picture(k);
            _probe = s.Text(24, 0, Theme.DockFont);
            Efl.evas_object_hide(_probe);
            _parts["num"] = new Part(_keyed["num"].T, 24, false);
            _parts["name"] = new Part(_keyed["name"].T, 26, false);
            _parts["title"] = new Part(_keyed["title"].T, 64, false);
        }

        private const int HatchEndsSlots = 1 + GuideScene.HatchTile;

        private Slot NewSlot() { var sl = new Slot { T = _s.Text(24, Theme.TextMain, Theme.DockFont) }; Efl.evas_object_hide(sl.T); return sl; }

        public Surface Surface { get { return _s; } }
        public IEnumerable<KeyValuePair<string, Part>> Parts { get { return _parts; } }
        public bool GroupFade { get { return true; } }

        public bool Visible { get { return _s.Visible; } }
        public void Hide()
        {
            _veil.SetGuide(false);
            if (!_s.Visible) return;
            int startDy = _dy;
            _s.Leave(SlideOutSeconds, Motion.In);
            Anim.Run(SlideAnimKey, SlideOutSeconds, 0, Motion.In, p => ApplySlide(Motion.Lerp(startDy, SlideDistance, p)));
        }

        public int Measure(string text, int size, int weight) { return _widths.Get(text, size, weight, _measureNative); }

        private readonly WidthCache _widths = new WidthCache();
        private readonly Func<string, int, int, int> _measureNative;
        private int MeasureNative(string text, int size, int weight)
        {
            Texts.Font(_probe, Font(weight), size);
            Texts.Set(_probe, text);
            return Texts.Advance(_probe);
        }

        private static string Font(int weight) { return weight >= 600 ? Theme.DockBoldFont : Theme.DockFont; }

        public void Draw(List<Prim> scene)
        {
            bool wasVisible = _s.Visible;
            int lines = 0, fills = 0, hatches = 0, hatchEnds = 0, texts = 0;
            bool focus = false;
            _keysShown.Clear(); _keyedShown.Clear();
            foreach (Prim p in scene)
            {
                switch (p.Kind)
                {
                    case PrimKind.Line:
                        if (lines < _lines.Length)
                        {
                            IntPtr r = _lines[lines];
                            int rgb = p.Tint != 0 ? p.Tint : 0xFFFFFF, a = p.Alpha;
                            Efl.evas_object_color_set(r, ((rgb >> 16) & 255) * a / 255, ((rgb >> 8) & 255) * a / 255, (rgb & 255) * a / 255, a);
                            _lineBaseX[lines] = p.X; _lineBaseY[lines] = p.Y;
                            Efl.evas_object_move(r, p.X, p.Y + _dy); Efl.evas_object_resize(r, p.W, p.H); Efl.evas_object_show(r);
                            lines++;
                        }
                        break;
                    case PrimKind.Fill:
                        if (fills < _fills.Length)
                        {
                            _fillBaseX[fills] = p.X; _fillBaseY[fills] = p.Y;
                            _fills[fills].SetRect(p.X, p.Y + _dy, p.W, p.H);
                            if (p.Tint != 0) _fills[fills].SetTint(p.Tint, p.Alpha);
                            else _fills[fills].SetWhite(p.Alpha);
                            fills++;
                        }
                        break;
                    case PrimKind.Hatch:
                        if (hatches < _hatches.Length)
                        {
                            IntPtr h = _hatches[hatches];
                            _hatchBaseX[hatches] = p.X; _hatchBaseY[hatches] = p.Y;
                            Efl.evas_object_move(h, p.X, p.Y + _dy); Efl.evas_object_resize(h, p.W, p.H);
                            Efl.evas_object_image_fill_set(h, -p.Phase, 0, GuideScene.HatchTile, GuideScene.HatchTile);
                            Efl.evas_object_color_set(h, p.Alpha, p.Alpha, p.Alpha, p.Alpha); Efl.evas_object_show(h);
                            hatches++;
                        }
                        break;
                    case PrimKind.HatchEnd:
                        if (hatchEnds < _hatchEnds.Length)
                        {
                            IntPtr h = _hatchEnds[hatchEnds];
                            _hatchEndBaseX[hatchEnds] = p.X; _hatchEndBaseY[hatchEnds] = p.Y;
                            Efl.evas_object_move(h, p.X, p.Y + _dy); Efl.evas_object_resize(h, p.W, p.H);
                            Efl.evas_object_image_fill_set(h, -p.Phase * GuideScene.HatchEndW, 0, HatchEndsSlots * GuideScene.HatchEndW, GuideLayout.CellH);
                            Efl.evas_object_color_set(h, p.Alpha, p.Alpha, p.Alpha, p.Alpha); Efl.evas_object_show(h);
                            hatchEnds++;
                        }
                        break;
                    case PrimKind.Focus:
                        _focusBaseX = p.X; _focusBaseY = p.Y;
                        _focus.SetRect(p.X, p.Y + _dy, p.W, p.H);
                        _focus.SetTint(Theme.Ice, p.Alpha);
                        focus = true;
                        break;
                    case PrimKind.Text:
                        if (p.Key != null && _keyed.ContainsKey(p.Key)) { Place(_keyed[p.Key], p, p.Text, p.Y); _keyedShown.Add(p.Key); }
                        else texts = Text(p, texts);
                        break;
                    case PrimKind.Logo: break;
                    case PrimKind.Key:
                        IntPtr k;
                        if (_keys.TryGetValue(p.Text, out k) && _keysShown.Add(p.Text))
                        {
                            _keyBaseX[p.Text] = p.X; _keyBaseY[p.Text] = p.Y;
                            Efl.evas_object_move(k, p.X, p.Y + _dy); Efl.evas_object_resize(k, p.W, p.H);
                            Efl.evas_object_color_set(k, p.Alpha, p.Alpha, p.Alpha, p.Alpha); Efl.evas_object_show(k);
                        }
                        break;
                }
            }
            _hasFocus = focus;
            if (!focus) _focus.SetWhite(0);
            foreach (var kv in _keys) if (!_keysShown.Contains(kv.Key)) Efl.evas_object_hide(kv.Value);
            foreach (var kv in _keyed) if (!_keyedShown.Contains(kv.Key)) Efl.evas_object_hide(kv.Value.T);
            for (int i = 0; i < _logos.Length; i++) _logos[i].Hide();
            for (int i = lines; i < _linesUsed; i++) Efl.evas_object_hide(_lines[i]);
            for (int i = fills; i < _fillsUsed; i++) _fills[i].SetWhite(0);
            for (int i = hatches; i < _hatchesUsed; i++) Efl.evas_object_hide(_hatches[i]);
            for (int i = hatchEnds; i < _hatchEndsUsed; i++) Efl.evas_object_hide(_hatchEnds[i]);
            for (int i = texts; i < _textsUsed; i++) Efl.evas_object_hide(_texts[i].T);
            _linesUsed = lines; _fillsUsed = fills; _hatchesUsed = hatches; _hatchEndsUsed = hatchEnds; _textsUsed = texts;

            for (int i = 0; i < texts; i++) { _textBaseX[i] = _texts[i].BaseX; _textBaseY[i] = _texts[i].BaseY; }

            _veil.SetGuide(false);
            if (!wasVisible)
            {
                _dy = SlideDistance;
                ApplySlide(_dy);
                _s.Show(SlideInSeconds, 0);
                Anim.Run(SlideAnimKey, SlideInSeconds, 0, Motion.Ease, p => ApplySlide(Motion.Lerp(SlideDistance, 0, p)));
            }
            else
            {
                _s.Show(SlideInSeconds, 0);
            }
        }

        private void ApplySlide(int dy)
        {
            _dy = dy;
            Efl.evas_object_move(_bg, CardX, CardY + dy);
            for (int i = 0; i < _linesUsed; i++) Efl.evas_object_move(_lines[i], _lineBaseX[i], _lineBaseY[i] + dy);
            for (int i = 0; i < _fillsUsed; i++) Efl.evas_object_move(_fills[i].Obj, _fillBaseX[i], _fillBaseY[i] + dy);
            for (int i = 0; i < _hatchesUsed; i++) Efl.evas_object_move(_hatches[i], _hatchBaseX[i], _hatchBaseY[i] + dy);
            for (int i = 0; i < _hatchEndsUsed; i++) Efl.evas_object_move(_hatchEnds[i], _hatchEndBaseX[i], _hatchEndBaseY[i] + dy);
            if (_hasFocus) Efl.evas_object_move(_focus.Obj, _focusBaseX, _focusBaseY + dy);
            for (int i = 0; i < _textsUsed; i++) Efl.evas_object_move(_texts[i].T, _textBaseX[i], _textBaseY[i] + dy);
            foreach (string k in _keyedShown) { Slot sl = _keyed[k]; Efl.evas_object_move(sl.T, sl.BaseX, sl.BaseY + dy); }
            foreach (string k in _keysShown) Efl.evas_object_move(_keys[k], _keyBaseX[k], _keyBaseY[k] + dy);
        }

        private int Text(Prim p, int at)
        {
            if (at >= _texts.Length) return at;
            if (p.Lines <= 1) { Place(_texts[at], p, p.Text, p.Y); return at + 1; }
            string[] words = p.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int w = 0;
            for (int line = 0; line < p.Lines && at < _texts.Length; line++)
            {
                string cur = "";
                while (w < words.Length)
                {
                    string next = cur.Length == 0 ? words[w] : cur + " " + words[w];
                    if (Measure(next, p.Size, p.Weight) > p.MaxW && cur.Length > 0) break;
                    cur = next; w++;
                }
                if (line == p.Lines - 1 && w < words.Length) cur += " " + string.Join(" ", words, w, words.Length - w);
                Place(_texts[at], p, cur, p.Y + line * p.LineH);
                at++;
                if (w >= words.Length) break;
            }
            return at;
        }

        private void Place(Slot sl, Prim p, string text, int top)
        {
            IntPtr t = sl.T;
            string font = Font(p.Weight);
            if (sl.Size != p.Size || sl.Font != font) { Texts.Font(t, font, p.Size); sl.Size = p.Size; sl.Font = font; sl.Text = null; }
            if (p.Tint != 0) Theme.TintText(t, p.Tint, 255); else Efl.evas_object_color_set(t, p.Alpha, p.Alpha, p.Alpha, p.Alpha);
            if (sl.Text != text || sl.MaxW != p.MaxW) { Surface.SetFitted(t, text, p.MaxW); sl.Text = text; sl.MaxW = p.MaxW; }
            int left = p.Right ? p.X - Texts.Advance(t) : p.X;
            int baseY = Theme.TopOf(top, p.Size);
            sl.BaseX = left; sl.BaseY = baseY;
            Efl.evas_object_move(t, left, baseY + _dy);
            Efl.evas_object_show(t);
        }
    }
}
