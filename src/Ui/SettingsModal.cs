using System;
using System.IO;
using Launcher.Core;
using Launcher.Native;

namespace Launcher.Ui
{
    /// <summary>
    /// Compact EFL overlay panel anchored directly above the central dock pill.
    /// Uses the same dock_bg.png glass and shadow as the dock pills, with 5 quick-access
    /// tiles at the top and large, readable settings rows with graphical switches below.
    /// </summary>
    internal sealed class SettingsModal
    {
        private const string Font = "SamsungOneUI 500,Sans";
        private const string BoldFont = "SamsungOneUI 700,Sans";
        private const int NotificationLevelTop = 40;
        private const int WindowLayer = 901;
        private const int Margin = DockLayout.Margin;
        private const int MaxRows = SettingsController.MaxVisibleRows;
        private const int SlotCount = MaxRows + 2;
        private const int MaxCityResults = 4;
        private const int KeyCount = 34;
        private const int TileCount = SettingsController.QuickTileCount;
        private const int TileSize = DockLayout.Icon;
        private const int TileFocusSize = DockLayout.FocusIcon;
        private const int TileGap = DockLayout.Gap;
        private const int TileGlowPad = DockLayout.HighlightPad;
        private const int TileBlue = 0x005F73;

        private readonly IntPtr _win;
        private readonly IntPtr _evas;
        private readonly string _assets;
        private readonly Action<string> _log;

        public readonly int PanelW;
        public readonly int PanelH;
        public readonly int WinW;
        public readonly int WinH;
        public readonly int WinX;
        public readonly int WinY;

        private IntPtr _shadow, _panel;
        private IntPtr _tileGlow, _tileGlow2, _tileRing, _tileRing2, _tileLabel, _topDivider;
        private readonly IntPtr[] _tiles = new IntPtr[TileCount];
        private IntPtr _headerIcon, _headerTitle, _headerDivider;
        private IntPtr _scrollUp, _scrollDown;
        private IntPtr _listClip, _selBg, _selAccent;

        private readonly IntPtr[] _rowIcon = new IntPtr[SlotCount];
        private readonly IntPtr[] _rowLabel = new IntPtr[SlotCount];
        private readonly IntPtr[] _rowValue = new IntPtr[SlotCount];
        private readonly IntPtr[] _rowSwitch = new IntPtr[SlotCount];
        private readonly IntPtr[] _rowArrowL = new IntPtr[SlotCount];
        private readonly IntPtr[] _rowArrowR = new IntPtr[SlotCount];
        private readonly string[] _rowIconShown = new string[SlotCount];
        private readonly string[] _rowLabelShown = new string[SlotCount];
        private readonly string[] _rowValueShown = new string[SlotCount];
        private readonly int[] _rowValueW = new int[SlotCount];
        private readonly int[] _rowSwitchShown = new int[SlotCount];

        private IntPtr _cityInputBg, _citySearchIcon, _cityQueryText, _cityStatusText;
        private readonly IntPtr[] _keyBg = new IntPtr[KeyCount];
        private readonly IntPtr[] _keyIcon = new IntPtr[KeyCount];
        private readonly IntPtr[] _keyText = new IntPtr[KeyCount];
        private readonly IntPtr[] _resBg = new IntPtr[MaxCityResults];
        private readonly IntPtr[] _resIcon = new IntPtr[MaxCityResults];
        private readonly IntPtr[] _resText = new IntPtr[MaxCityResults];
        private readonly string[] _resTextShown = new string[MaxCityResults];

        private string _tileLabelShown;
        private int _tileLabelW;
        private string _headerTitleShown, _headerIconShown;
        private string _cityQueryShown, _cityStatusShown;
        private bool _built;
        private bool _visible;
        private string _kbLang;

        public IntPtr Win { get { return _win; } }

        public SettingsModal(DockLayout dockLayout, int screenW, int screenH, string assetsDir, Action<string> log)
        {
            _assets = assetsDir;
            _log = log ?? (_ => { });

            PanelW = Math.Min(608, screenW - 2 * Margin - 24);
            int maxPanelH = Math.Max(360, dockLayout.WinY + dockLayout.Top - 14 - Margin - 12);
            PanelH = Math.Min(596, maxPanelH);
            WinW = PanelW + 2 * Margin;
            WinH = PanelH + 2 * Margin;
            WinX = (screenW - WinW) / 2;
            WinY = Math.Max(12, dockLayout.WinY + dockLayout.Top - 14 - PanelH - Margin);

            _win = Efl.elm_win_add(IntPtr.Zero, "tvlauncher-settings", Efl.ElmWinNotification);
            if (_win == IntPtr.Zero)
            {
                _log("settings: elm_win_add failed");
                return;
            }
            _evas = Efl.evas_object_evas_get(_win);
            Efl.elm_win_title_set(_win, "tvlauncher-settings");
            Efl.elm_win_alpha_set(_win, true);
            Efl.elm_win_borderless_set(_win, true);
            Efl.elm_win_prop_focus_skip_set(_win, false);
            Efl.efl_util_set_notification_window_level(_win, NotificationLevelTop);
            Efl.elm_win_layer_set(_win, WindowLayer);
            Efl.elm_win_aux_hint_add(_win, "wm.policy.win.user.geometry", "1");
            Efl.elm_win_aux_hint_add(_win, "wm.comp.win.effect.enable", "0");
            Efl.evas_object_move(_win, WinX, WinY);
            Efl.evas_object_resize(_win, WinW, WinH);
        }

        public void Build()
        {
            if (_win == IntPtr.Zero || _evas == IntPtr.Zero || _built) return;
            _built = true;

            _shadow = NineSlice("shadow", Margin + Design.ShellRadius);
            _panel = NineSlice("dock_bg", Design.ShellSlice);

            // Top quick-access bar: two cross-fading glows < tiles < two cross-fading rings < label < divider
            _tileGlow = ImageObj(Path.Combine(_assets, "dock_glow.png"));
            _tileGlow2 = ImageObj(Path.Combine(_assets, "dock_glow.png"));
            for (int i = 0; i < TileCount; i++)
            {
                _tiles[i] = ImageObj(Path.Combine(_assets, "icons", QuickTiles.All[i].IconSlug + ".png"));
                Efl.evas_object_image_scale_hint_set(_tiles[i], Efl.EvasImageScaleHintDynamic);
            }
            _tileRing = ImageObj(Path.Combine(_assets, "dock_ring.png"));
            _tileRing2 = ImageObj(Path.Combine(_assets, "dock_ring.png"));
            _tileLabel = TextObj(BoldFont, 24);
            _topDivider = Rect();

            // Subview header
            _headerIcon = UiImage("gear");
            _headerTitle = TextObj(BoldFont, 26);
            _headerDivider = Rect();

            _scrollUp = ImageObj(Path.Combine(_assets, "chev_up.png"));
            _scrollDown = ImageObj(Path.Combine(_assets, "chev_down.png"));

            _listClip = Rect();
            Efl.evas_object_color_set(_listClip, 255, 255, 255, 255);
            _selBg = Rect();
            Efl.evas_object_clip_set(_selBg, _listClip);
            _selAccent = Rect();
            Efl.evas_object_clip_set(_selAccent, _listClip);

            for (int i = 0; i < SlotCount; i++)
            {
                _rowIcon[i] = UiImage(null);
                _rowLabel[i] = TextObj(Font, 26);
                _rowValue[i] = TextObj(BoldFont, 24);
                _rowSwitch[i] = UiImage(null);
                _rowArrowL[i] = UiImage("caret-left");
                _rowArrowR[i] = UiImage("caret-right");
                Efl.evas_object_clip_set(_rowIcon[i], _listClip);
                Efl.evas_object_clip_set(_rowLabel[i], _listClip);
                Efl.evas_object_clip_set(_rowValue[i], _listClip);
                Efl.evas_object_clip_set(_rowSwitch[i], _listClip);
                Efl.evas_object_clip_set(_rowArrowL[i], _listClip);
                Efl.evas_object_clip_set(_rowArrowR[i], _listClip);
            }

            _cityInputBg = Rect();
            _citySearchIcon = UiImage("magnifying-glass");
            _cityQueryText = TextObj(BoldFont, 24);
            _cityStatusText = TextObj(Font, 19);

            for (int i = 0; i < KeyCount; i++)
            {
                _keyBg[i] = Rect();
                _keyIcon[i] = UiImage(null);
                _keyText[i] = TextObj(BoldFont, 19);
            }

            for (int i = 0; i < MaxCityResults; i++)
            {
                _resBg[i] = Rect();
                _resIcon[i] = UiImage("map-pin");
                _resText[i] = TextObj(Font, 21);
            }

            HideAllContent();
        }

        public void ShowWindow()
        {
            if (_win == IntPtr.Zero) return;
            if (!_visible)
            {
                _visible = true;
                Efl.evas_object_show(_win);
            }
            Efl.elm_win_activate(_win);
        }

        public void HideWindow()
        {
            if (_win == IntPtr.Zero || !_visible) return;
            _visible = false;
            Efl.evas_object_hide(_win);
        }

        public void Apply(SettingsController ctl, AppEntry[] inputs)
        {
            if (!_built || ctl == null) return;

            if (ctl.Phase == Phase.Hidden)
            {
                HideWindow();
                return;
            }
            int alpha = (int)Math.Round(255f * ctl.Presence);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            if (!_visible)
            {
                _visible = true;
                Efl.evas_object_show(_win);
            }

            int px = Margin;
            int py = Margin + ctl.Slide;
            int pw = PanelW;
            int ph = PanelH;

            Efl.evas_object_move(_shadow, px - Margin, py - Margin + Design.ShadowDy);
            Efl.evas_object_resize(_shadow, pw + 2 * Margin, ph + 2 * Margin);
            Tint(_shadow, alpha);

            Efl.evas_object_move(_panel, px, py);
            Efl.evas_object_resize(_panel, pw, ph);
            Tint(_panel, alpha);

            if (ctl.View == SettingsView.Main)
            {
                HideSubviewHeader();
                HideCityPools();
                ApplyTopTiles(ctl, px, py, pw, alpha);
                ApplyListView(ctl, ctl.HdmiInputs, px, py + 184, pw, py + ph - 8, alpha);
            }
            else if (ctl.View == SettingsView.CitySearch)
            {
                HideTopTiles();
                HideListPools();
                ApplySubviewHeader(ctl.View, px, py, pw, alpha);
                ApplyCitySearch(ctl, px, py + 72, pw, alpha);
            }
            else
            {
                HideTopTiles();
                HideCityPools();
                ApplySubviewHeader(ctl.View, px, py, pw, alpha);
                ApplyListView(ctl, ctl.HdmiInputs, px, py + 72, pw, py + ph - 10, alpha);
            }
        }

        private void ApplyTopTiles(SettingsController ctl, int px, int py, int pw, int alpha)
        {
            int stripW = TileCount * TileSize + (TileCount - 1) * TileGap;
            int startX = px + (pw - stripW) / 2;
            int centerY = py + 24 + TileFocusSize / 2;
            float fp = ctl.TileFocus < 0f ? 0f : (ctl.TileFocus > TileCount - 1 ? TileCount - 1 : ctl.TileFocus);

            // Stationary cross-fading glows on the icon being left (g1) and the icon being reached (g2),
            // plus two rings that hug each icon's growing/shrinking box — identical to Dock.Apply.
            int g1 = (int)fp;
            int g2 = g1 < TileCount - 1 ? g1 + 1 : g1;
            float w2 = g2 == g1 ? 0f : fp - g1;
            int ga1 = (int)Math.Round(alpha * ctl.TileActive * (1f - w2));
            int ga2 = (int)Math.Round(alpha * ctl.TileActive * w2);

            int glowSide = (DockLayout.Grow ? TileFocusSize : TileSize) + 2 * TileGlowPad;
            int glowY = centerY - glowSide / 2 - (DockLayout.Grow ? Design.Lift : 0);
            int cx1 = startX + g1 * (TileSize + TileGap) + TileSize / 2;
            int cx2 = startX + g2 * (TileSize + TileGap) + TileSize / 2;

            Efl.evas_object_move(_tileGlow, cx1 - glowSide / 2, glowY);
            Efl.evas_object_resize(_tileGlow, glowSide, glowSide);
            TintRgb(_tileGlow, ga1, TileBlue);
            if (ga1 > 0) Efl.evas_object_show(_tileGlow); else Efl.evas_object_hide(_tileGlow);

            Efl.evas_object_move(_tileGlow2, cx2 - glowSide / 2, glowY);
            Efl.evas_object_resize(_tileGlow2, glowSide, glowSide);
            TintRgb(_tileGlow2, ga2, TileBlue);
            if (ga2 > 0) Efl.evas_object_show(_tileGlow2); else Efl.evas_object_hide(_tileGlow2);

            for (int i = 0; i < TileCount; i++)
            {
                float amount = DockLayout.Grow ? DockLayout.Amount(fp, i) * ctl.TileActive : 0f;
                // Even sizes only: an odd size would put the centre half a pixel off and the icon would wobble sideways.
                int size = TileSize + 2 * (int)Math.Round((TileFocusSize - TileSize) / 2f * amount);
                int lift = (int)Math.Round(Design.Lift * amount);
                int cx = startX + i * (TileSize + TileGap) + TileSize / 2;
                int tx = cx - size / 2;
                int ty = centerY - size / 2 - lift;

                Efl.evas_object_move(_tiles[i], tx, ty);
                Efl.evas_object_resize(_tiles[i], size, size);
                int ta = (int)Math.Round(alpha * (Design.IconDim + (1f - Design.IconDim) * DockLayout.Amount(fp, i) * ctl.TileActive));
                Tint(_tiles[i], ta);
                Efl.evas_object_show(_tiles[i]);

                if (i == g1)
                {
                    Efl.evas_object_move(_tileRing, tx - Design.RingW, ty - Design.RingW);
                    Efl.evas_object_resize(_tileRing, size + 2 * Design.RingW, size + 2 * Design.RingW);
                }
                if (i == g2)
                {
                    Efl.evas_object_move(_tileRing2, tx - Design.RingW, ty - Design.RingW);
                    Efl.evas_object_resize(_tileRing2, size + 2 * Design.RingW, size + 2 * Design.RingW);
                }
            }

            TintRgb(_tileRing, ga1, Design.RingColor);
            if (ga1 > 0) Efl.evas_object_show(_tileRing); else Efl.evas_object_hide(_tileRing);

            TintRgb(_tileRing2, ga2, Design.RingColor);
            if (ga2 > 0) Efl.evas_object_show(_tileRing2); else Efl.evas_object_hide(_tileRing2);

            string title = ctl.ActiveTileTitle();
            if (title != _tileLabelShown)
            {
                _tileLabelShown = title;
                Efl.evas_object_text_text_set(_tileLabel, title);
                _tileLabelW = Efl.evas_object_text_horiz_advance_get(_tileLabel);
            }
            Efl.evas_object_move(_tileLabel, px + (pw - _tileLabelW) / 2, py + 140);
            Tint(_tileLabel, (int)Math.Round(alpha * (0.57f + 0.43f * ctl.TileActive)));
            Efl.evas_object_show(_tileLabel);

            Efl.evas_object_move(_topDivider, px + 24, py + 178);
            Efl.evas_object_resize(_topDivider, pw - 48, 1);
            TintRgb(_topDivider, (int)Math.Round(alpha * Design.DividerAlpha), Design.DividerColor);
            Efl.evas_object_show(_topDivider);
        }

        private void ApplySubviewHeader(SettingsView view, int px, int py, int pw, int alpha)
        {
            string hIcon = HeaderIconFor(view);
            string hTitle = HeaderTitleFor(view);
            if (hIcon != _headerIconShown)
            {
                _headerIconShown = hIcon;
                SetUiIcon(_headerIcon, hIcon);
            }
            Efl.evas_object_move(_headerIcon, px + 26, py + 18);
            Efl.evas_object_resize(_headerIcon, 28, 28);
            Tint(_headerIcon, alpha);
            Efl.evas_object_show(_headerIcon);

            if (hTitle != _headerTitleShown)
            {
                _headerTitleShown = hTitle;
                Efl.evas_object_text_text_set(_headerTitle, hTitle);
            }
            Efl.evas_object_move(_headerTitle, px + 64, py + 15);
            Tint(_headerTitle, alpha);
            Efl.evas_object_show(_headerTitle);

            Efl.evas_object_move(_headerDivider, px + 24, py + 64);
            Efl.evas_object_resize(_headerDivider, pw - 48, 1);
            TintRgb(_headerDivider, (int)Math.Round(alpha * Design.DividerAlpha), Design.DividerColor);
            Efl.evas_object_show(_headerDivider);
        }

        private void ApplyListView(SettingsController ctl, AppEntry[] inputs, int px, int top, int pw, int bot, int alpha)
        {
            int arrowSize = 24;
            int upBand = 32;
            int downBand = 32;
            int startY = top + upBand;
            int rowsBot = bot - downBand;
            int totalCount = ItemCountFor(ctl, inputs);
            float scroll = ctl.ListScroll;
            if (scroll < 0f) scroll = 0f;
            int firstIdx = Math.Max(0, (int)Math.Floor(scroll));
            float frac = scroll - firstIdx;
            int maxRows = Math.Min(MaxRows, Math.Max(1, totalCount - firstIdx));
            int rowH = ctl.View == SettingsView.Main ? 56 : 58;
            if (startY + maxRows * rowH > rowsBot) rowH = Math.Max(42, (rowsBot - startY) / Math.Max(1, maxRows));

            Efl.evas_object_move(_listClip, px, startY);
            Efl.evas_object_resize(_listClip, pw, Math.Max(1, rowsBot - startY));
            Efl.evas_object_color_set(_listClip, 255, 255, 255, 255);
            Efl.evas_object_show(_listClip);

            bool canUp = scroll > 0.01f;
            bool canDown = scroll + MaxRows < totalCount - 0.01f;
            int arrowX = px + (pw - arrowSize) / 2;
            if (canUp)
            {
                Efl.evas_object_move(_scrollUp, arrowX, top + (upBand - arrowSize) / 2);
                Efl.evas_object_resize(_scrollUp, arrowSize, arrowSize);
                Tint(_scrollUp, alpha * 220 / 255);
                Efl.evas_object_show(_scrollUp);
            }
            else Efl.evas_object_hide(_scrollUp);

            if (canDown)
            {
                Efl.evas_object_move(_scrollDown, arrowX, rowsBot + (downBand - arrowSize) / 2);
                Efl.evas_object_resize(_scrollDown, arrowSize, arrowSize);
                Tint(_scrollDown, alpha * 220 / 255);
                Efl.evas_object_show(_scrollDown);
            }
            else Efl.evas_object_hide(_scrollDown);

            int selA = (int)Math.Round(alpha * ctl.ListActive);
            if (selA > 0 && totalCount > 0)
            {
                int selY = startY + (int)Math.Round((ctl.ListFocus - scroll) * rowH);
                Efl.evas_object_move(_selBg, px + 18, selY + 2);
                Efl.evas_object_resize(_selBg, pw - 36, rowH - 4);
                TintRgba(_selBg, 0, 95, 115, selA * 205 / 255);
                Efl.evas_object_show(_selBg);

                Efl.evas_object_move(_selAccent, px + 18, selY + 8);
                Efl.evas_object_resize(_selAccent, 4, rowH - 16);
                Tint(_selAccent, selA);
                Efl.evas_object_show(_selAccent);
            }
            else
            {
                Efl.evas_object_hide(_selBg);
                Efl.evas_object_hide(_selAccent);
            }

            for (int slot = 0; slot < SlotCount; slot++)
            {
                int idx = firstIdx + slot;
                if (idx >= totalCount)
                {
                    HideRow(slot);
                    continue;
                }

                string icon, label, value;
                bool isHeader = false, enabled = true, focused = false, isToggle = false, switchOn = false, showLeft = false, showRight = false;
                DescribeListItem(ctl, inputs, idx, out isHeader, out icon, out label, out value, out enabled, out focused, out isToggle, out switchOn, out showLeft, out showRight);

                int ry = startY + (int)Math.Round((slot - frac) * rowH);
                if (isHeader)
                {
                    Efl.evas_object_hide(_rowIcon[slot]);
                    Efl.evas_object_hide(_rowValue[slot]);
                    Efl.evas_object_hide(_rowSwitch[slot]);
                    Efl.evas_object_hide(_rowArrowL[slot]);
                    Efl.evas_object_hide(_rowArrowR[slot]);

                    SetRowLabel(slot, label, 20, BoldFont, pw - 56);
                    Efl.evas_object_move(_rowLabel[slot], px + 26, ry + rowH - 30);
                    Tint(_rowLabel[slot], alpha * 165 / 255);
                    Efl.evas_object_show(_rowLabel[slot]);
                    continue;
                }

                float rowDist = Math.Abs(ctl.ListFocus - idx);
                float rowFocus = rowDist < 1f ? (1f - rowDist) * ctl.ListActive : 0f;
                int fgA = enabled ? (int)Math.Round(alpha * (0.86f + 0.14f * rowFocus)) : alpha * 95 / 255;

                if (!string.IsNullOrEmpty(icon))
                {
                    if (icon != _rowIconShown[slot])
                    {
                        _rowIconShown[slot] = icon;
                        SetUiIcon(_rowIcon[slot], icon);
                    }
                    Efl.evas_object_move(_rowIcon[slot], px + 34, ry + (rowH - 26) / 2);
                    Efl.evas_object_resize(_rowIcon[slot], 26, 26);
                    Tint(_rowIcon[slot], fgA);
                    Efl.evas_object_show(_rowIcon[slot]);
                }
                else Efl.evas_object_hide(_rowIcon[slot]);

                int rightEdge = px + pw - 30;
                int rightReserve = isToggle ? 74 : 185;
                int maxLabelW = Math.Max(180, pw - 72 - rightReserve);

                SetRowLabel(slot, label, 26, Font, maxLabelW);
                Efl.evas_object_move(_rowLabel[slot], px + 72, ry + (rowH - 31) / 2);
                Tint(_rowLabel[slot], fgA);
                Efl.evas_object_show(_rowLabel[slot]);

                if (isToggle)
                {
                    Efl.evas_object_hide(_rowValue[slot]);
                    Efl.evas_object_hide(_rowArrowL[slot]);
                    Efl.evas_object_hide(_rowArrowR[slot]);

                    int wantState = switchOn ? 2 : 1;
                    if (_rowSwitchShown[slot] != wantState)
                    {
                        _rowSwitchShown[slot] = wantState;
                        SetUiIcon(_rowSwitch[slot], switchOn ? "switch_on" : "switch_off");
                    }
                    Efl.evas_object_move(_rowSwitch[slot], rightEdge - 56, ry + (rowH - 30) / 2);
                    Efl.evas_object_resize(_rowSwitch[slot], 56, 30);
                    Tint(_rowSwitch[slot], enabled ? alpha : alpha * 100 / 255);
                    Efl.evas_object_show(_rowSwitch[slot]);
                }
                else if (!string.IsNullOrEmpty(value))
                {
                    Efl.evas_object_hide(_rowSwitch[slot]);
                    if (value != _rowValueShown[slot])
                    {
                        _rowValueShown[slot] = value;
                        Efl.evas_object_text_text_set(_rowValue[slot], value);
                        _rowValueW[slot] = Efl.evas_object_text_horiz_advance_get(_rowValue[slot]);
                    }
                    int arrowSpace = showRight ? 26 : 0;
                    int vx = rightEdge - arrowSpace - _rowValueW[slot];
                    Efl.evas_object_move(_rowValue[slot], vx, ry + (rowH - 29) / 2);
                    int valA = enabled ? (int)Math.Round(alpha * (0.80f + 0.20f * rowFocus)) : alpha * 90 / 255;
                    Tint(_rowValue[slot], valA);
                    Efl.evas_object_show(_rowValue[slot]);

                    if (showLeft && enabled)
                    {
                        Efl.evas_object_move(_rowArrowL[slot], vx - 24, ry + (rowH - 20) / 2);
                        Efl.evas_object_resize(_rowArrowL[slot], 20, 20);
                        Tint(_rowArrowL[slot], focused ? alpha : alpha * 135 / 255);
                        Efl.evas_object_show(_rowArrowL[slot]);
                    }
                    else Efl.evas_object_hide(_rowArrowL[slot]);

                    if (showRight && enabled)
                    {
                        Efl.evas_object_move(_rowArrowR[slot], rightEdge - 20, ry + (rowH - 20) / 2);
                        Efl.evas_object_resize(_rowArrowR[slot], 20, 20);
                        Tint(_rowArrowR[slot], focused ? alpha : alpha * 135 / 255);
                        Efl.evas_object_show(_rowArrowR[slot]);
                    }
                    else Efl.evas_object_hide(_rowArrowR[slot]);
                }
                else
                {
                    Efl.evas_object_hide(_rowSwitch[slot]);
                    Efl.evas_object_hide(_rowValue[slot]);
                    Efl.evas_object_hide(_rowArrowL[slot]);
                    Efl.evas_object_hide(_rowArrowR[slot]);
                }
            }
        }

        private void ApplyCitySearch(SettingsController ctl, int px, int top, int pw, int alpha)
        {
            Efl.evas_object_hide(_scrollUp);
            Efl.evas_object_hide(_scrollDown);

            int boxX = px + 24;
            int boxW = pw - 48;
            Efl.evas_object_move(_cityInputBg, boxX, top + 2);
            Efl.evas_object_resize(_cityInputBg, boxW, 48);
            TintRgba(_cityInputBg, 255, 255, 255, alpha * 28 / 255);
            Efl.evas_object_show(_cityInputBg);

            Efl.evas_object_move(_citySearchIcon, boxX + 14, top + 15);
            Efl.evas_object_resize(_citySearchIcon, 22, 22);
            Tint(_citySearchIcon, alpha * 215 / 255);
            Efl.evas_object_show(_citySearchIcon);

            string qDisp = ctl.CityQuery + (ctl.CityInResults ? "" : "_");
            if (qDisp != _cityQueryShown)
            {
                _cityQueryShown = qDisp;
                Efl.evas_object_text_text_set(_cityQueryText, qDisp);
            }
            Efl.evas_object_move(_cityQueryText, boxX + 46, top + 11);
            Tint(_cityQueryText, alpha);
            Efl.evas_object_show(_cityQueryText);

            if (ctl.CityStatus != _cityStatusShown)
            {
                _cityStatusShown = ctl.CityStatus;
                Efl.evas_object_text_text_set(_cityStatusText, ctl.CityStatus);
            }
            Efl.evas_object_move(_cityStatusText, boxX + 2, top + 56);
            if (ctl.CityIsError) TintRgba(_cityStatusText, 255, 190, 118, alpha);
            else Tint(_cityStatusText, alpha * 215 / 255);
            Efl.evas_object_show(_cityStatusText);

            int kbX = boxX;
            int kbY = top + 86;
            int gap = 5;
            int cellW = (boxW - (VirtualKeyboard.GridCols - 1) * gap) / VirtualKeyboard.GridCols;
            int cellH = 40;
            int keyIdx = 0;

            for (int r = 0; r < VirtualKeyboard.Rows.Length; r++)
            {
                KeyboardKey[] row = VirtualKeyboard.Rows[r];
                int curX = kbX;
                int ry = kbY + r * (cellH + gap);
                for (int c = 0; c < row.Length; c++)
                {
                    KeyboardKey k = row[c];
                    int kw = k.SpanCols * cellW + (k.SpanCols - 1) * gap;
                    if (c == row.Length - 1) kw = kbX + boxW - curX;
                    bool sel = !ctl.CityInResults && ctl.KeyRow == r && ctl.KeyCol == c;
                    bool isSearch = k.Code == "SEARCH";

                    Efl.evas_object_move(_keyBg[keyIdx], curX, ry);
                    Efl.evas_object_resize(_keyBg[keyIdx], kw, cellH);
                    if (sel) TintRgba(_keyBg[keyIdx], 0, 95, 115, alpha * 245 / 255);
                    else if (isSearch) TintRgba(_keyBg[keyIdx], 0, 95, 115, alpha * 145 / 255);
                    else TintRgba(_keyBg[keyIdx], 255, 255, 255, alpha * 30 / 255);
                    Efl.evas_object_show(_keyBg[keyIdx]);

                    string label = KeyLabel(k);
                    if (_kbLang != Localization.Language)
                    {
                        if (k.Icon != null) SetUiIcon(_keyIcon[keyIdx], k.Icon);
                        Efl.evas_object_text_font_set(_keyText[keyIdx], BoldFont, k.SpanCols > 1 ? 15 : 20);
                        Efl.evas_object_text_text_set(_keyText[keyIdx], label);
                    }
                    int tw = Efl.evas_object_text_horiz_advance_get(_keyText[keyIdx]);
                    int th = Efl.evas_object_text_vert_advance_get(_keyText[keyIdx]);
                    if (k.Icon != null)
                    {
                        int iconW = 18;
                        int totalW = iconW + 5 + tw;
                        int tx = curX + (kw - totalW) / 2;
                        Efl.evas_object_move(_keyIcon[keyIdx], tx, ry + (cellH - iconW) / 2);
                        Efl.evas_object_resize(_keyIcon[keyIdx], iconW, iconW);
                        Tint(_keyIcon[keyIdx], sel ? alpha : alpha * 230 / 255);
                        Efl.evas_object_show(_keyIcon[keyIdx]);
                        Efl.evas_object_move(_keyText[keyIdx], tx + iconW + 5, ry + (cellH - th) / 2);
                    }
                    else
                    {
                        Efl.evas_object_hide(_keyIcon[keyIdx]);
                        Efl.evas_object_move(_keyText[keyIdx], curX + (kw - tw) / 2, ry + (cellH - th) / 2);
                    }
                    Tint(_keyText[keyIdx], sel ? alpha : alpha * 235 / 255);
                    Efl.evas_object_show(_keyText[keyIdx]);

                    curX += kw + gap;
                    keyIdx++;
                }
            }
            _kbLang = Localization.Language;

            int resStart = 0;
            if (ctl.CityResultIndex >= MaxCityResults) resStart = ctl.CityResultIndex - MaxCityResults + 1;
            int resTop = kbY + 4 * (cellH + gap) + 8;
            int resH = 40;
            for (int slot = 0; slot < MaxCityResults; slot++)
            {
                int idx = resStart + slot;
                if (idx >= ctl.CityResults.Length)
                {
                    Efl.evas_object_hide(_resBg[slot]);
                    Efl.evas_object_hide(_resIcon[slot]);
                    Efl.evas_object_hide(_resText[slot]);
                    continue;
                }
                int ry = resTop + slot * (resH + 4);
                bool sel = ctl.CityInResults && ctl.CityResultIndex == idx;

                Efl.evas_object_move(_resBg[slot], boxX, ry);
                Efl.evas_object_resize(_resBg[slot], boxW, resH);
                if (sel) TintRgba(_resBg[slot], 0, 95, 115, alpha * 215 / 255);
                else TintRgba(_resBg[slot], 255, 255, 255, alpha * 18 / 255);
                Efl.evas_object_show(_resBg[slot]);

                Efl.evas_object_move(_resIcon[slot], boxX + 12, ry + (resH - 22) / 2);
                Efl.evas_object_resize(_resIcon[slot], 22, 22);
                Tint(_resIcon[slot], sel ? alpha : alpha * 195 / 255);
                Efl.evas_object_show(_resIcon[slot]);

                string label = ctl.CityResults[idx].DisplayLabel;
                if (label != _resTextShown[slot])
                {
                    _resTextShown[slot] = label;
                    Efl.evas_object_text_text_set(_resText[slot], label);
                }
                Efl.evas_object_move(_resText[slot], boxX + 44, ry + (resH - 26) / 2);
                Tint(_resText[slot], sel ? alpha : alpha * 225 / 255);
                Efl.evas_object_show(_resText[slot]);
            }
        }

        private static string KeyLabel(KeyboardKey k)
        {
            switch (k.Code)
            {
                case "SPACE": return Localization.Ui("kb.space");
                case "BACKSPACE": return Localization.Ui("kb.backspace");
                case "CLEAR": return Localization.Ui("kb.clear");
                case "SEARCH": return Localization.Ui("kb.search");
                default: return k.Label;
            }
        }

        private static int ItemCountFor(SettingsController ctl, AppEntry[] inputs)
        {
            return ctl.View == SettingsView.Inputs ? (inputs != null ? inputs.Length : 0) : ctl.Rows.Count;
        }

        private static void DescribeListItem(
            SettingsController ctl, AppEntry[] inputs, int idx,
            out bool isHeader, out string icon, out string label, out string value,
            out bool enabled, out bool focused, out bool isToggle, out bool switchOn,
            out bool showLeft, out bool showRight)
        {
            isHeader = false; icon = null; label = ""; value = "";
            enabled = true; focused = false; isToggle = false; switchOn = false;
            showLeft = false; showRight = false;

            if (ctl.View == SettingsView.Inputs)
            {
                AppEntry e = inputs[idx];
                icon = e.SourceType == 0 ? "television-simple" : "plug";
                label = e.Label;
                value = e.SourceType == ctl.ActiveSourceType ? Localization.Ui("val.active") : "";
                focused = ctl.SubIndex == idx;
                return;
            }

            SettingsRow row = ctl.Rows[idx];
            isHeader = row.Kind == SettingsItemKind.Header;
            icon = row.Icon;
            label = row.Label;
            value = row.Value;
            enabled = row.Enabled;
            focused = !isHeader && !ctl.FocusInTiles && ctl.FocusIndex == idx;
            isToggle = row.Kind == SettingsItemKind.Toggle;
            switchOn = row.SwitchOn;
            showLeft = row.Kind == SettingsItemKind.Cycle;
            showRight = showLeft || row.HasArrow;
        }

        private static string HeaderIconFor(SettingsView view)
        {
            switch (view)
            {
                case SettingsView.CitySearch: return "magnifying-glass";
                case SettingsView.Inputs: return "plug";
                default: return "gear";
            }
        }

        private static string HeaderTitleFor(SettingsView view)
        {
            switch (view)
            {
                case SettingsView.CitySearch: return Localization.Ui("sub.city.title");
                case SettingsView.Inputs: return Localization.Ui("sub.inputs.title");
                default: return Localization.Ui("settings.title");
            }
        }

        private void SetRowLabel(int slot, string text, int maxSize, string font, int maxW)
        {
            string key = maxSize + ":" + maxW + ":" + text;
            if (key == _rowLabelShown[slot]) return;
            _rowLabelShown[slot] = key;
            int size = maxSize;
            int minSize = Math.Min(maxSize, 20);
            Efl.evas_object_text_font_set(_rowLabel[slot], font, size);
            Efl.evas_object_text_text_set(_rowLabel[slot], text ?? "");
            if (!string.IsNullOrEmpty(text) && maxW > 0)
            {
                int w = Efl.evas_object_text_horiz_advance_get(_rowLabel[slot]);
                while (w > maxW && size > minSize)
                {
                    size--;
                    Efl.evas_object_text_font_set(_rowLabel[slot], font, size);
                    w = Efl.evas_object_text_horiz_advance_get(_rowLabel[slot]);
                }
            }
        }

        private void HideTopTiles()
        {
            Efl.evas_object_hide(_tileGlow);
            Efl.evas_object_hide(_tileGlow2);
            Efl.evas_object_hide(_tileRing);
            Efl.evas_object_hide(_tileRing2);
            Efl.evas_object_hide(_tileLabel);
            Efl.evas_object_hide(_topDivider);
            for (int i = 0; i < TileCount; i++) Efl.evas_object_hide(_tiles[i]);
        }

        private void HideSubviewHeader()
        {
            Efl.evas_object_hide(_headerIcon);
            Efl.evas_object_hide(_headerTitle);
            Efl.evas_object_hide(_headerDivider);
        }

        private void HideRow(int slot)
        {
            Efl.evas_object_hide(_rowIcon[slot]);
            Efl.evas_object_hide(_rowLabel[slot]);
            Efl.evas_object_hide(_rowValue[slot]);
            Efl.evas_object_hide(_rowSwitch[slot]);
            Efl.evas_object_hide(_rowArrowL[slot]);
            Efl.evas_object_hide(_rowArrowR[slot]);
        }

        private void HideListPools()
        {
            Efl.evas_object_hide(_listClip);
            Efl.evas_object_hide(_selBg);
            Efl.evas_object_hide(_selAccent);
            for (int i = 0; i < SlotCount; i++) HideRow(i);
        }

        private void HideCityPools()
        {
            Efl.evas_object_hide(_cityInputBg);
            Efl.evas_object_hide(_citySearchIcon);
            Efl.evas_object_hide(_cityQueryText);
            Efl.evas_object_hide(_cityStatusText);
            for (int i = 0; i < KeyCount; i++)
            {
                Efl.evas_object_hide(_keyBg[i]);
                Efl.evas_object_hide(_keyIcon[i]);
                Efl.evas_object_hide(_keyText[i]);
            }
            for (int i = 0; i < MaxCityResults; i++)
            {
                Efl.evas_object_hide(_resBg[i]);
                Efl.evas_object_hide(_resIcon[i]);
                Efl.evas_object_hide(_resText[i]);
            }
        }

        private void HideAllContent()
        {
            HideTopTiles();
            HideSubviewHeader();
            Efl.evas_object_hide(_scrollUp);
            Efl.evas_object_hide(_scrollDown);
            HideListPools();
            HideCityPools();
        }

        private IntPtr Rect()
        {
            IntPtr r = Efl.evas_object_rectangle_add(_evas);
            Efl.evas_object_show(r);
            return r;
        }

        private IntPtr TextObj(string font, int size)
        {
            IntPtr t = Efl.evas_object_text_add(_evas);
            Efl.evas_object_text_font_set(t, font, size);
            Efl.evas_object_show(t);
            return t;
        }

        private IntPtr ImageObj(string path)
        {
            IntPtr img = Efl.evas_object_image_add(_evas);
            Efl.evas_object_image_filled_set(img, true);
            Efl.evas_object_image_smooth_scale_set(img, true);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Efl.evas_object_image_file_set(img, path, null);
            Efl.evas_object_show(img);
            return img;
        }

        private IntPtr UiImage(string name)
        {
            IntPtr img = Efl.evas_object_image_add(_evas);
            Efl.evas_object_image_filled_set(img, true);
            Efl.evas_object_image_smooth_scale_set(img, true);
            if (!string.IsNullOrEmpty(name)) SetUiIcon(img, name);
            Efl.evas_object_show(img);
            return img;
        }

        private void SetUiIcon(IntPtr img, string name)
        {
            string path = Path.Combine(_assets, "ui", name + ".png");
            if (File.Exists(path)) Efl.evas_object_image_file_set(img, path, null);
        }

        private IntPtr NineSlice(string name, int border)
        {
            IntPtr img = Efl.evas_object_image_add(_evas);
            Efl.evas_object_image_file_set(img, Path.Combine(_assets, name + ".png"), null);
            Efl.evas_object_image_filled_set(img, true);
            Efl.evas_object_image_border_set(img, border, border, border, border);
            Efl.evas_object_image_border_center_fill_set(img, 1);
            Efl.evas_object_image_smooth_scale_set(img, true);
            Efl.evas_object_show(img);
            return img;
        }

        private static void Tint(IntPtr o, int a)
        {
            Efl.evas_object_color_set(o, a, a, a, a);
        }

        private static void TintRgb(IntPtr o, int a, int rgb)
        {
            Efl.evas_object_color_set(o, ((rgb >> 16) & 0xFF) * a / 255, ((rgb >> 8) & 0xFF) * a / 255, (rgb & 0xFF) * a / 255, a);
        }

        private static void TintRgba(IntPtr o, int r, int g, int b, int a)
        {
            Efl.evas_object_color_set(o, r * a / 255, g * a / 255, b * a / 255, a);
        }
    }
}
