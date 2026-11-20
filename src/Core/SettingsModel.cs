using System;

namespace Launcher.Core
{
    internal enum SettingsView
    {
        Main = 0,
        CitySearch = 1,
        Inputs = 2
    }

    internal enum SettingsItemKind
    {
        Header = 0,
        Action = 1,
        Toggle = 2,
        Cycle = 3
    }

    internal enum SettingsItemId
    {
        None = 0,
        Inputs = 1,
        Apps = 2,
        Vetro = 3,
        TvStore = 4,
        TvSettings = 5,
        ShowWeather = 6,
        WeatherMode = 7,
        CitySearch = 8,
        WeatherShowLocation = 9,
        WeatherShowMinMax = 10,
        Units = 11,
        ShowClock = 12,
        ClockFormat = 13,
        ShowDate = 14,
        DateFormat = 15,
        SoundEnabled = 16
    }

    internal sealed class SettingsRow
    {
        public readonly SettingsItemId Id;
        public readonly SettingsItemKind Kind;
        public readonly string Icon;
        public readonly string Label;
        public readonly string Value;
        public readonly bool Enabled;
        public readonly bool HasArrow;
        public readonly bool SwitchOn;

        public SettingsRow(SettingsItemId id, SettingsItemKind kind, string icon, string label, string value, bool enabled, bool hasArrow = false, bool switchOn = false)
        {
            Id = id;
            Kind = kind;
            Icon = icon;
            Label = label;
            Value = value;
            Enabled = enabled;
            HasArrow = hasArrow;
            SwitchOn = switchOn;
        }
    }

    internal sealed class QuickTileSpec
    {
        public readonly SettingsItemId Id;
        public readonly string IconSlug;
        public readonly string LabelKey;

        public QuickTileSpec(SettingsItemId id, string iconSlug, string labelKey)
        {
            Id = id;
            IconSlug = iconSlug;
            LabelKey = labelKey;
        }
    }

    internal static class QuickTiles
    {
        public static readonly QuickTileSpec[] All =
        {
            new QuickTileSpec(SettingsItemId.Inputs, "qs-hdmi", "item.inputs"),
            new QuickTileSpec(SettingsItemId.Apps, "qs-apps", "item.apps"),
            new QuickTileSpec(SettingsItemId.Vetro, "qs-vetro", "item.vetro"),
            new QuickTileSpec(SettingsItemId.TvStore, "qs-store", "item.tv_store"),
            new QuickTileSpec(SettingsItemId.TvSettings, "qs-settings", "item.tv_settings"),
        };
    }

    internal enum SettingsActionKind
    {
        None = 0,
        Moved = 1,
        Changed = 2,
        Close = 3,
        CloseAll = 4,
        OpenInputsPanel = 5,
        LaunchApps = 6,
        LaunchVetro = 7,
        LaunchTvStore = 8,
        LaunchTvSettings = 9,
        SwitchInput = 10,
        SearchCity = 11
    }

    internal struct SettingsKeyResult
    {
        public SettingsActionKind Action;
        public int Index;
        public string Query;
        public bool NeedsWeatherRefresh;

        public static SettingsKeyResult Of(SettingsActionKind action)
        {
            return new SettingsKeyResult { Action = action };
        }
    }

    internal sealed class KeyboardKey
    {
        public readonly string Code;
        public readonly string Label;
        public readonly string Icon;
        public readonly int SpanCols;

        public KeyboardKey(string code, string label, string icon, int spanCols)
        {
            Code = code;
            Label = label;
            Icon = icon;
            SpanCols = spanCols;
        }
    }

    internal static class VirtualKeyboard
    {
        public const int GridCols = 10;

        public static readonly KeyboardKey[][] Rows =
        {
            RowChars("Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"),
            RowChars("A", "S", "D", "F", "G", "H", "J", "K", "L", "\u00d1"),
            RowChars("Z", "X", "C", "V", "B", "N", "M", "-", "'", "."),
            new[]
            {
                new KeyboardKey("SPACE", "SPACE", null, 3),
                new KeyboardKey("BACKSPACE", "DEL", "backspace", 2),
                new KeyboardKey("CLEAR", "CLEAR", "x", 2),
                new KeyboardKey("SEARCH", "SEARCH", "magnifying-glass", 3),
            }
        };

        private static KeyboardKey[] RowChars(params string[] chars)
        {
            var keys = new KeyboardKey[chars.Length];
            for (int i = 0; i < chars.Length; i++)
                keys[i] = new KeyboardKey(chars[i], chars[i], null, 1);
            return keys;
        }

        public static int MapRow2ToRow3(int col2)
        {
            if (col2 <= 2) return 0;
            if (col2 <= 4) return 1;
            if (col2 <= 6) return 2;
            return 3;
        }

        public static int MapRow3ToRow2(int col3)
        {
            switch (col3)
            {
                case 0: return 1;
                case 1: return 3;
                case 2: return 5;
                default: return 8;
            }
        }
    }
}
