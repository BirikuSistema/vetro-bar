# vetro-bar

An overlay for the native dock of Vetro (0.5.x) on Samsung Tizen TVs. It is not a fork of Vetro: you build the launcher DLL and copy it over the live installation. If Vetro updates itself, run `apply.sh` again; `restore.sh` puts back the dock that was there before the first apply.

Developed and tested on a Samsung QE55Q65AAUXXC (Tizen 6, firmware `T-NKLADEUC-2310.0`). Other Tizen 6 TVs with Vetro installed should work, but are untested.

## What it changes

On top of the video, inside the pill:

- **TV** at the left (opens the DVB tuner app `TdtSwitch0.Tv`), followed by a divider
- your apps in the middle, scrolling
- **Settings** and **Samsung Home** at the right, stacked, icon only

Outside the pill:

- local weather at the left (Open-Meteo)
- date and clock at the right

UI sounds (48 kHz PCM through PulseAudio, loaded in memory; disable with `--no-sound`):

- `res/sounds/open.wav` when the dock appears
- `res/sounds/close.wav` when it hides
- `res/sounds/move.wav` when moving between tiles
- `res/sounds/launch.wav` when an app is launched

There is no Sources page and no HDMI tiles. The app list comes from Vetro's own list on the TV, not from this repository.

## Language, time and weather: automatic

Nothing needs configuring. The dock follows the TV:

| What | Where it comes from |
| --- | --- |
| Language of the date and the Low/High label | TV menu language (`db/menu_widget/language`), falling back to `$LANG`, then English. Built in: `en es fr de it pt nl` |
| Time and daylight saving | The TV's own time zone (`/etc/localtime`), re-read every time the dock opens and every minute |
| 12 h / 24 h clock | By country (12 h in US, CA, AU, NZ, PH; 24 h elsewhere) |
| °C / °F | By country (°F in US and a few others) |
| Weather location | Your public IP address, looked up with [ipwho.is](https://ipwho.is/) (fallback [ipapi.co](https://ipapi.co/)), cached for 24 h; the last known place is used when offline |

> [!NOTE]
> The IP lookup sends your TV's public IP address to those two services, and the forecast request sends coordinates (rounded to three decimals) to [Open-Meteo](https://open-meteo.com/). To avoid the lookup, set a fixed location as shown below.

### Overrides

Everything is optional. Priority: command line, environment variable, then `vetro-bar.conf`, then automatic.

`vetro-bar.conf` lives next to the weather cache on the TV (`/opt/usr/home/owner/share/vetro-bar.conf`):

```ini
# language=es          # en es fr de it pt nl
# location=41.65,-4.72 # latitude,longitude; no IP lookup is made
# clock=24             # 12 or 24
# units=c              # c or f
```

The same four settings exist as `--lang`, `--location`, `--clock`, `--units` and as `TVLAUNCHER_LANG`, `TVLAUNCHER_LOCATION`, `TVLAUNCHER_CLOCK`, `TVLAUNCHER_UNITS`.

To add a language, add one entry to the table in `src/Core/Localization.cs` (day names, month names, Low/High words and the date pattern).

## Layout

- `src/` the dock. `Core/` is pure logic (layout, catalog, weather, localization, WAV) and has tests; `Host/` handles evdev input, app launching and the log; `Native/` is P/Invoke (EFL, libc, PulseAudio); `Ui/` draws with EFL.
- `tests/` desktop tests: `dotnet run --project tests`.
- `res/` images and sounds (`icons/`, `weather/`, `sounds/`).
- `tools/` generators (`system_icons.py`, `make-glass.cjs`) and the overlay script.
- `designer/` a standalone page to try out the look.
- `apply.sh` / `restore.sh` / `status.sh` overlay on the TV.

## Build

```sh
sh build.sh                          # netcoreapp3.1
dotnet run --project tests           # pure logic, no TV needed
```

## Install and uninstall

Nothing uses `sdb`: on these TVs root over `sdb` is disabled, and the developer-mode host IP has to stay `127.0.0.1` so Vetro's agent starts. The scripts talk to that agent on port 8099 of your TV.

```sh
export TV_IP=192.168.1.50            # your TV's IP address (required)
sh apply.sh                          # first run keeps Vetro's dock, then installs this one
sh status.sh
sh restore.sh                        # puts the original dock back and restarts it
```

The original is kept on the TV in `native/dock/.vetro-bar-stock/`, next to the running dock, and later applies leave it alone.

If a Vetro update deletes that folder, the next `apply.sh` saves the new Vetro and then installs this dock. If the old copy is still there and you want `restore.sh` to return to the updated Vetro, delete `.vetro-bar-stock` on the TV and run `apply.sh` again.

## Credits

Icon glyphs in `tools/glyphs` are from [Phosphor Icons](https://phosphoricons.com/) (see `tools/glyphs/LICENSE-phosphor.txt`).
