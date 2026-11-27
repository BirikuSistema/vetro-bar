using System;
using System.IO;
using System.Runtime.InteropServices;
using Launcher.Core;
using Launcher.Native;

namespace Launcher.Ui
{
    /// <summary>
    /// Soft borderless glass volume pill anchored to the chosen screen edge, matching vetro-bar's
    /// dock_bg.png (#003049 @ 70% opacity, 24 px corners), SamsungOneUI 700 font, and smooth slide-in/out.
    /// When Sonos is active, displays a discreet secondary capsule below the pill with Night Mode and
    /// Speech Enhancement icons and subtle Left/Right hints.
    /// </summary>
    internal sealed class VolumePill
    {
        private const string Font = Design.SideChromeFont;
        private const int NotificationLevelTop = 40, WindowLayer = 900;
        private const int SlidePad = 48;
        private const double FadeInSeconds = VolumeFade.FadeInSeconds;
        private const double ModesHoldSeconds = 2.4;

        private readonly string _assets;
        private readonly Action<string> _log;
        private readonly VolumeEdge _edge;
        private readonly VolumePlacement _p;
        private readonly Action<string> _onModeKey;
        private readonly NavigationInput _nav = new NavigationInput();
        private readonly int _winX, _winY, _winW, _winH;

        private IntPtr _win, _body, _track, _fill, _clip, _num;
        private IntPtr _modesBg, _caretLeft, _nightBadge, _nightIcon, _voiceBadge, _voiceIcon, _caretRight;
        private readonly IntPtr[] _icons = new IntPtr[3];
        private IntPtr _animator, _holdTimer;
        private readonly Efl.TaskCb _tick, _holdEnd;
        private readonly Efl.EvasCb _keyCb;
        private bool _built, _failed, _shown, _muted, _showModes, _nightOn, _voiceOn, _grabbed;
        private double _shownAt, _until;
        private int _fillLength, _iconLevel = -1;
        private int _numBaseX, _numBaseY;
        private int _clipBaseX, _clipBaseY, _clipW, _clipH;
        private int _lastShiftX = int.MinValue, _lastShiftY = int.MinValue;

        public VolumePill(string assets, Action<string> log, int screenW, int screenH, VolumeEdge edge, Action<string> onModeKey = null)
        {
            _assets = assets;
            _log = log;
            _edge = edge;
            _onModeKey = onModeKey;
            _p = VolumePlacement.For(edge, screenW, screenH);
            _winX = Math.Max(0, _p.WinX - SlidePad);
            _winY = Math.Max(0, _p.WinY - SlidePad);
            _winW = Math.Min(screenW - _winX, _p.WinW + 2 * SlidePad);
            _winH = Math.Min(screenH - _winY, _p.WinH + 2 * SlidePad);
            _tick = Tick;
            _holdEnd = OnHoldEnd;
            _keyCb = OnKey;
        }

        public bool IsOnScreen { get { return _shown; } }
        public bool HasSonosModes { get { return _showModes; } }

        public void Show(int volume, bool muted, string customLabel = null)
        {
            Show(volume, muted, false, false, false, false, customLabel);
        }

        public void Show(int volume, bool muted, bool showModes, bool nightOn, bool voiceOn, bool allowKeyGrab, string customLabel = null)
        {
            if (!Build()) return;
            _showModes = showModes;
            _nightOn = nightOn;
            _voiceOn = voiceOn;
            SetModesVisible(showModes);
            Update(volume, muted, customLabel);

            double now = Efl.ecore_time_get();
            _until = now + (showModes ? ModesHoldSeconds : VolumeFade.HoldSeconds);
            if (!_shown)
            {
                _shown = true;
                _shownAt = now;
                _lastShiftX = int.MinValue;
                _lastShiftY = int.MinValue;
                Apply(0);
                Efl.evas_object_show(_win);
            }
            else if (now - _shownAt < FadeInSeconds + 0.01) { }
            else
            {
                _shownAt = now - FadeInSeconds;
                Apply(1.0);
            }
            Efl.elm_win_raise(_win);
            if (showModes && allowKeyGrab) GrabModeKeys();
            else UngrabModeKeys();

            CancelHold();
            if (_animator == IntPtr.Zero) _animator = Efl.ecore_animator_add(_tick, IntPtr.Zero);
        }

        public void ReleaseModeKeys()
        {
            UngrabModeKeys();
        }

        public void HandleRawModeKey(int code)
        {
            if (!_shown || !_showModes || _onModeKey == null) return;
            if (code != 105 && code != 106) return;
            string key = _nav.Press(code, true, Efl.ecore_time_get());
            if (key != null) _onModeKey(key);
        }

        private void OnKey(IntPtr data, IntPtr evas, IntPtr obj, IntPtr eventInfo)
        {
            try
            {
                string key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(eventInfo));
                if (_nav.IsEcho(key, Efl.ecore_time_get())) return;
                if (!_shown || !_showModes || _onModeKey == null) return;
                if (key == "Left" || key == "Right") _onModeKey(key);
            }
            catch (Exception ex) { _log("volume key: " + ex.Message); }
        }

        private void GrabModeKeys()
        {
            OverlayFlag.Mark(OverlayFlag.VolumePath, true, _log);
            if (_grabbed || _win == IntPtr.Zero) return;
            foreach (string k in new[] { "Left", "Right" })
            {
                Efl.evas_object_key_grab(_win, k, 0, 0, true);
                Efl.elm_win_keygrab_set(_win, k, 0, 0, 0, Efl.ElmWinKeygrabTopmost);
            }
            _grabbed = true;
        }

        private void UngrabModeKeys()
        {
            OverlayFlag.Mark(OverlayFlag.VolumePath, false, _log);
            if (!_grabbed || _win == IntPtr.Zero) return;
            _grabbed = false;
            foreach (string k in new[] { "Left", "Right" })
            {
                Efl.elm_win_keygrab_unset(_win, k, 0, 0);
                Efl.evas_object_key_ungrab(_win, k, 0, 0);
            }
        }

        private void CancelHold()
        {
            if (_holdTimer != IntPtr.Zero) { Efl.ecore_timer_del(_holdTimer); _holdTimer = IntPtr.Zero; }
        }

        private byte OnHoldEnd(IntPtr data)
        {
            _holdTimer = IntPtr.Zero;
            try { if (_shown && _animator == IntPtr.Zero) _animator = Efl.ecore_animator_add(_tick, IntPtr.Zero); }
            catch (Exception ex) { _log("volume hold: " + ex.Message); }
            return 0;
        }

        private bool Build()
        {
            if (_built) return true;
            if (_failed) return false;
            try
            {
                _win = Efl.elm_win_add(IntPtr.Zero, "vetro-volume", Efl.ElmWinNotification);
                if (_win == IntPtr.Zero) { _failed = true; _log("volume: elm_win_add failed"); return false; }
                Efl.elm_win_title_set(_win, "vetro-volume");
                Efl.elm_win_alpha_set(_win, true);
                Efl.elm_win_borderless_set(_win, true);
                Efl.elm_win_prop_focus_skip_set(_win, true);
                Efl.efl_util_set_notification_window_level(_win, NotificationLevelTop);
                Efl.elm_win_layer_set(_win, WindowLayer);
                Efl.elm_win_aux_hint_add(_win, "wm.policy.win.user.geometry", "1");
                Efl.elm_win_aux_hint_add(_win, "wm.comp.win.effect.enable", "0");
                Efl.evas_object_move(_win, _winX, _winY);
                Efl.evas_object_resize(_win, _winW, _winH);
                Efl.evas_object_pass_events_set(_win, true);
                IntPtr evas = Efl.evas_object_evas_get(_win);

                int ox = _winX, oy = _winY;
                int trackHalf = (_p.Vertical ? _p.TrackW : _p.TrackH) / 2;
                _body = Capsule(evas, "dock_bg", Design.ShellSlice, _p.BodyX - ox, _p.BodyY - oy, _p.BodyW, _p.BodyH);
                _track = Capsule(evas, "vol_cap", trackHalf, _p.TrackX - ox, _p.TrackY - oy, _p.TrackW, _p.TrackH);
                _fill = Capsule(evas, "vol_cap", trackHalf, _p.TrackX - ox, _p.TrackY - oy, _p.TrackW, _p.TrackH);
                _clip = Efl.evas_object_rectangle_add(evas);
                Efl.evas_object_color_set(_clip, 255, 255, 255, 255);
                Efl.evas_object_move(_clip, _p.TrackX - ox, _p.TrackY - oy);
                Efl.evas_object_resize(_clip, 1, 1);
                Efl.evas_object_clip_set(_fill, _clip);
                Efl.evas_object_show(_clip);
                _num = Efl.evas_object_text_add(evas);
                Efl.evas_object_text_font_set(_num, Font, VolumePlacement.NumberSize);
                Efl.evas_object_pass_events_set(_num, true);
                Efl.evas_object_show(_num);
                for (int i = 0; i < _icons.Length; i++)
                {
                    IntPtr img = Efl.evas_object_image_add(evas);
                    Efl.evas_object_image_file_set(img, Path.Combine(_assets, "vol_icon_" + i + ".png"), null);
                    if (Efl.evas_object_image_load_error_get(img) != Efl.EvasLoadErrorNone) _log("volume: asset missing vol_icon_" + i);
                    Efl.evas_object_image_filled_set(img, true);
                    Efl.evas_object_image_smooth_scale_set(img, true);
                    Efl.evas_object_move(img, _p.IconX - ox, _p.IconY - oy);
                    Efl.evas_object_resize(img, VolumePlacement.IconSize, VolumePlacement.IconSize);
                    Efl.evas_object_pass_events_set(img, true);
                    _icons[i] = img;
                }

                // Sonos modes capsule below/above the main volume pill (matching 88 px width & dock_bg glass):
                //   moon
                //   voice
                //   <  >
                _modesBg = Capsule(evas, "dock_bg", Design.ShellSlice, _p.ModesX - ox, _p.ModesY - oy, VolumePlacement.ModesW, VolumePlacement.ModesH);
                int iOff = (VolumePlacement.ModeBadgeSize - VolumePlacement.ModeIconSize) / 2;
                double badgeScale = (VolumePlacement.ModeBadgeSize / 2.0) / 36.0;

                _nightBadge = ScaledCapsule(evas, "pill_cap", 36, badgeScale, _p.ModeBadgeX - ox, _p.NightBadgeY - oy, VolumePlacement.ModeBadgeSize, VolumePlacement.ModeBadgeSize);
                _nightIcon = UiImage(evas, "moon.png", _p.ModeBadgeX + iOff - ox, _p.NightBadgeY + iOff - oy, VolumePlacement.ModeIconSize, VolumePlacement.ModeIconSize);

                _voiceBadge = ScaledCapsule(evas, "pill_cap", 36, badgeScale, _p.ModeBadgeX - ox, _p.VoiceBadgeY - oy, VolumePlacement.ModeBadgeSize, VolumePlacement.ModeBadgeSize);
                _voiceIcon = UiImage(evas, "voice.png", _p.ModeBadgeX + iOff - ox, _p.VoiceBadgeY + iOff - oy, VolumePlacement.ModeIconSize, VolumePlacement.ModeIconSize);

                _caretLeft = UiImage(evas, "caret-left.png", _p.LeftCaretX - ox, _p.CaretsY - oy, VolumePlacement.ModeCaretSize, VolumePlacement.ModeCaretSize);
                _caretRight = UiImage(evas, "caret-right.png", _p.RightCaretX - ox, _p.CaretsY - oy, VolumePlacement.ModeCaretSize, VolumePlacement.ModeCaretSize);

                SetModesVisible(false);
                Efl.evas_object_event_callback_add(_win, Efl.EvasCallbackKeyDown, _keyCb, IntPtr.Zero);
                _built = true;
                _log("volume: pill ready");
                return true;
            }
            catch (Exception ex) { _failed = true; _log("volume: " + ex.Message); return false; }
        }

        private void SetModesVisible(bool show)
        {
            IntPtr[] objs = { _modesBg, _caretLeft, _nightBadge, _nightIcon, _voiceBadge, _voiceIcon, _caretRight };
            foreach (IntPtr o in objs)
            {
                if (o == IntPtr.Zero) continue;
                if (show) Efl.evas_object_show(o); else Efl.evas_object_hide(o);
            }
        }

        private IntPtr Capsule(IntPtr evas, string name, int border, int x, int y, int w, int h)
        {
            IntPtr img = Efl.evas_object_image_add(evas);
            Efl.evas_object_image_file_set(img, Path.Combine(_assets, name + ".png"), null);
            if (Efl.evas_object_image_load_error_get(img) != Efl.EvasLoadErrorNone) _log("volume: asset missing " + name);
            Efl.evas_object_image_filled_set(img, true);
            Efl.evas_object_image_border_set(img, border, border, border, border);
            Efl.evas_object_image_border_center_fill_set(img, 1);
            Efl.evas_object_image_smooth_scale_set(img, true);
            Efl.evas_object_move(img, x, y);
            Efl.evas_object_resize(img, w, h);
            Efl.evas_object_pass_events_set(img, true);
            Efl.evas_object_show(img);
            return img;
        }

        private IntPtr ScaledCapsule(IntPtr evas, string name, int border, double scale, int x, int y, int w, int h)
        {
            IntPtr img = Capsule(evas, name, border, x, y, w, h);
            Efl.evas_object_image_border_scale_set(img, scale);
            return img;
        }

        private IntPtr UiImage(IntPtr evas, string file, int x, int y, int w, int h)
        {
            IntPtr img = Efl.evas_object_image_add(evas);
            Efl.evas_object_image_file_set(img, Path.Combine(_assets, "ui", file), null);
            if (Efl.evas_object_image_load_error_get(img) != Efl.EvasLoadErrorNone) _log("volume: ui asset missing " + file);
            Efl.evas_object_image_filled_set(img, true);
            Efl.evas_object_image_smooth_scale_set(img, true);
            Efl.evas_object_move(img, x, y);
            Efl.evas_object_resize(img, w, h);
            Efl.evas_object_pass_events_set(img, true);
            return img;
        }

        private void Update(int volume, bool muted, string customLabel)
        {
            _muted = muted;
            _fillLength = VolumeModel.FillHeight(volume, muted, _p.Vertical ? _p.TrackH : _p.TrackW);
            int ox = _winX, oy = _winY;
            if (_fillLength > 0)
            {
                if (_p.Vertical)
                {
                    _clipBaseX = _p.TrackX - ox;
                    _clipBaseY = _p.TrackY + _p.TrackH - _fillLength - oy;
                    _clipW = _p.TrackW;
                    _clipH = _fillLength;
                }
                else
                {
                    _clipBaseX = _p.TrackX - ox;
                    _clipBaseY = _p.TrackY - oy;
                    _clipW = _fillLength;
                    _clipH = _p.TrackH;
                }
            }
            else
            {
                _clipBaseX = _p.TrackX - ox;
                _clipBaseY = _p.TrackY - oy;
                _clipW = 1;
                _clipH = 1;
            }
            _iconLevel = VolumeModel.IconLevel(volume, muted);
            string label = customLabel ?? (muted ? "\u2014" : volume.ToString());
            Efl.evas_object_text_text_set(_num, label);
            int w = Efl.evas_object_text_horiz_advance_get(_num), h = Efl.evas_object_text_vert_advance_get(_num);
            int nx = _p.Vertical ? _p.NumberCenterX - w / 2 : _p.NumberRight - w;
            _numBaseX = nx - ox;
            _numBaseY = _p.NumberCenterY - h / 2 - oy;

            int sx = _lastShiftX == int.MinValue ? 0 : _lastShiftX;
            int sy = _lastShiftY == int.MinValue ? 0 : _lastShiftY;
            Efl.evas_object_move(_clip, _clipBaseX + sx, _clipBaseY + sy);
            Efl.evas_object_resize(_clip, _clipW, _clipH);
            Efl.evas_object_move(_num, _numBaseX + sx, _numBaseY + sy);
        }

        private void LayoutShift(int dx, int dy)
        {
            if (dx == _lastShiftX && dy == _lastShiftY) return;
            _lastShiftX = dx;
            _lastShiftY = dy;
            int ox = _winX, oy = _winY;
            Efl.evas_object_move(_body, _p.BodyX - ox + dx, _p.BodyY - oy + dy);
            Efl.evas_object_move(_track, _p.TrackX - ox + dx, _p.TrackY - oy + dy);
            Efl.evas_object_move(_fill, _p.TrackX - ox + dx, _p.TrackY - oy + dy);
            Efl.evas_object_move(_clip, _clipBaseX + dx, _clipBaseY + dy);
            Efl.evas_object_move(_num, _numBaseX + dx, _numBaseY + dy);
            for (int i = 0; i < _icons.Length; i++)
                Efl.evas_object_move(_icons[i], _p.IconX - ox + dx, _p.IconY - oy + dy);

            if (_modesBg != IntPtr.Zero)
            {
                int iOff = (VolumePlacement.ModeBadgeSize - VolumePlacement.ModeIconSize) / 2;
                Efl.evas_object_move(_modesBg, _p.ModesX - ox + dx, _p.ModesY - oy + dy);
                Efl.evas_object_move(_nightBadge, _p.ModeBadgeX - ox + dx, _p.NightBadgeY - oy + dy);
                Efl.evas_object_move(_nightIcon, _p.ModeBadgeX + iOff - ox + dx, _p.NightBadgeY + iOff - oy + dy);
                Efl.evas_object_move(_voiceBadge, _p.ModeBadgeX - ox + dx, _p.VoiceBadgeY - oy + dy);
                Efl.evas_object_move(_voiceIcon, _p.ModeBadgeX + iOff - ox + dx, _p.VoiceBadgeY + iOff - oy + dy);
                Efl.evas_object_move(_caretLeft, _p.LeftCaretX - ox + dx, _p.CaretsY - oy + dy);
                Efl.evas_object_move(_caretRight, _p.RightCaretX - ox + dx, _p.CaretsY - oy + dy);
            }
        }

        private void Apply(double a)
        {
            double ease = 1.0 - (1.0 - a) * (1.0 - a);
            int shift = (int)Math.Round(SlidePad * (1.0 - ease));
            int dx = _edge == VolumeEdge.Left ? -shift : (_edge == VolumeEdge.Right ? shift : 0);
            int dy = _edge == VolumeEdge.Top ? -shift : (_edge == VolumeEdge.Bottom ? shift : 0);
            LayoutShift(dx, dy);

            // dock_bg.png already bakes #003049 @ 70% opacity with no border, so tint pure white * a.
            Tint(_body, 0xFFFFFF, a);
            Tint(_track, 0xFFFFFF, 0.16 * a);
            if (_fillLength > 0) { Tint(_fill, Design.VolumeFillColor, a); Efl.evas_object_show(_fill); }
            else Efl.evas_object_hide(_fill);
            Tint(_num, 0xFFFFFF, (_muted ? 0.55 : 0.95) * a);
            for (int i = 0; i < _icons.Length; i++)
            {
                if (i == _iconLevel) { Tint(_icons[i], 0xFFFFFF, 0.95 * a); Efl.evas_object_show(_icons[i]); }
                else Efl.evas_object_hide(_icons[i]);
            }

            if (_showModes && _modesBg != IntPtr.Zero)
            {
                Tint(_modesBg, 0xFFFFFF, a);
                Tint(_nightBadge, _nightOn ? Design.VolumeFillColor : 0xFFFFFF, (_nightOn ? 0.95 : 0.12) * a);
                Tint(_nightIcon, 0xFFFFFF, (_nightOn ? 0.98 : 0.45) * a);
                Tint(_voiceBadge, _voiceOn ? Design.VolumeFillColor : 0xFFFFFF, (_voiceOn ? 0.95 : 0.12) * a);
                Tint(_voiceIcon, 0xFFFFFF, (_voiceOn ? 0.98 : 0.45) * a);
                Tint(_caretLeft, 0xFFFFFF, 0.55 * a);
                Tint(_caretRight, 0xFFFFFF, 0.55 * a);
            }
        }

        private static void Tint(IntPtr obj, int rgb, double alpha)
        {
            int a = (int)Math.Round(Math.Max(0, Math.Min(1, alpha)) * 255);
            Efl.evas_object_color_set(obj, ((rgb >> 16) & 255) * a / 255, ((rgb >> 8) & 255) * a / 255, (rgb & 255) * a / 255, a);
        }

        private byte Tick(IntPtr data)
        {
            try
            {
                double now = Efl.ecore_time_get();
                double a = VolumeFade.Alpha(now, _shownAt, _until);
                if (a <= 0)
                {
                    UngrabModeKeys();
                    Efl.evas_object_hide(_win);
                    _shown = false;
                    _animator = IntPtr.Zero;
                    CancelHold();
                    return 0;
                }
                Apply(a);
                bool holding = VolumeFade.Holding(a, now, _until);
                if (!holding) return 1;
                _animator = IntPtr.Zero;
                CancelHold();
                _holdTimer = Efl.ecore_timer_add(Math.Max(0.0, _until - now), _holdEnd, IntPtr.Zero);
                return 0;
            }
            catch (Exception ex)
            {
                _log("volume tick: " + ex.Message);
                UngrabModeKeys();
                _animator = IntPtr.Zero;
                _shown = false;
                CancelHold();
                Efl.evas_object_hide(_win);
                return 0;
            }
        }
    }
}
