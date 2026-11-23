# vetro-bar

An enhanced overlay for the native dock and overlay ecosystem of [Vetro](https://github.com/andrearicci93/vetro) (0.7.0, with backward compatibility for 0.5.x–0.6.x) on Samsung Tizen TVs. It is not a fork of Vetro: you build the launcher DLL and copy it over the live installation. If Vetro updates itself, run `apply.sh` again; `restore.sh` puts back the dock that was there before the first apply.

Developed and tested on a Samsung QE55Q65AAUXXC (Tizen 6, firmware `T-NKLADEUC-2310.0`, paired with a Sonos Beam on HDMI eARC). Other Tizen 6 TVs with Vetro installed should work.

---

## What it changes

### 1. Main Dock Pill

On top of the video, inside the dock pill:

- **TV** at the left (opens the DVB tuner app `TdtSwitch0.Tv`), followed by a divider. The selector always starts focused here whenever the dock opens.
- Your favorite apps in the middle, scrolling horizontally.
- **All Apps** (`squares-four`) and **Samsung Home** (`house`) at the right, stacked, icon-only.

Outside the pill:

- **Local weather** at the left (Open-Meteo), supporting both day and night icon variants (`is_day`).
- **Date and clock** at the right.

---

### 2. All Apps Bar & Settings Integration

In Vetro 0.7.0, Vetro introduces an **All Apps** catalog (`vetro-dock-allapps.tsv`). `vetro-bar` adopts this seamlessly:

- The rightmost stacked tiles feature **All Apps** and **Samsung Home**.
- Selecting **All Apps** slides out the full horizontal catalog containing all installed applications, inputs, and system tools.
- **Settings** (`gear`) and **TV-Store** (`tv-store`) are elegantly placed inside this All Apps bar.
- **Clean Focus & Geometry:** While the All Apps bar is open, the weather and clock side pills are automatically hidden so that they do not overlap or crowd the screen.

---

### 3. Settings Drawer

Clicking on the **Settings** icon (accessible from All Apps or via shortcut) opens a dedicated bottom-anchored modal panel:

- **Quick-Access Tiles:** Direct shortcuts to HDMI Inputs, Apps list, Vetro dashboard, TV-Store, and native Samsung TV Settings.
- **HDMI Inputs Panel:** Dedicated HDMI source selector (HDMI 1–4) with visual active-source indicators.
- **Weather Configuration:**
  - Toggle weather display on/off.
  - Switch between automatic IP geolocation or manual location.
  - Interactive city search with an on-screen virtual keyboard powered by the Open-Meteo geocoding API.
  - Toggles for displaying the city name and daily low/high temperatures.
  - Temperature unit selection (°C, °F, or auto by country).
- **Clock & Date:**
  - Toggle clock on/off.
  - 12h / 24h format selection.
  - Toggle date on/off.
  - Date format selection (Long, Short, Numeric, ISO).
- **Sound:** Toggle UI sound effects on/off.
- **Iconography:** All system icons, weather conditions, navigation carets, and settings UI controls are rendered from official **[Phosphor Icons](https://phosphoricons.com/)** in `regular` style.

---

### 4. Volume Pill & Sonos / UPnP Integration

`vetro-bar` includes a dedicated native volume pill that integrates directly with external sound systems and ARC/eARC receivers:

- **Placement:** Positionable on any screen edge (`--volume-edge left|right|top|bottom|hidden`). Defaults to `left`.
- **UPnP / Sonos Discovery:** Discovers network soundbars (such as Sonos Beam on HDMI 2) via SSDP and UPnP `RenderingControl:1`, fetching the true current hardware volume and mute status without relying solely on Samsung's CEC approximation.
- **Samsung Volume OSD Suppression:**
  - On Samsung TVs, pressing volume buttons with an external audio system often triggers Samsung's native, opaque volume OSD (`tv-viewer-graphics` / `0x001ce200`), resulting in duplicate volume bars.
  - When `vetro-bar`'s volume pill is active, it automatically sets the system vconf key `db/volume/appid` to `org.vetro.noop` and restarts `hdmicec-service` once. This completely suppresses Samsung's native volume banner while keeping HDMI-CEC ARC audio control intact.
  - If `--volume-edge hidden` is chosen, `vetro-bar` automatically restores `org.tizen.tv-viewer` as the volume app handler.

---

### 5. Fast Channel Zapping & Tuner Optimizations

Along with the dock, `vetro-bar` includes critical optimizations for `vetro-tuner` on Samsung Tizen:

- **Samsung-Style Coalescing for Rapid Zapping (`CH+` / `CH-`):**
  - The first `CH+` or `CH-` press tunes immediately (`0 ms` latency).
  - Rapid subsequent presses (or holding `CH+`/`CH-`) update the channel banner indicator instantly on each click, but coalesce the hardware `Tvs.Tune` call (`0.38 s` window). This prevents `tvs-daemon` from locking and aborting intermediate DVB-T multiplexes, reducing rapid multi-channel zapping from 4–5+ seconds down to instant feedback.
- **Prioritized Hardware Tuning:**
  - EPG programme requests (`IServiceGuide`) are deferred by `0.65 s` after a tune, ensuring RPC calls do not compete with `tvs-daemon` during tuner frequency lock.
- **GuideStore Stale-Entry Fix:**
  - Resolves a bug in `GuideStore.PutFailed` where channels without immediate EPG data never had their timestamp updated, causing continuous EPG re-read spam to `tvs-daemon` every 20 seconds.
- **Cached IPC Proxies:**
  - Reuses `IServiceNavigation`, `ISourceControl`, and `IServiceGuide` proxy instances across requests on the worker thread, avoiding redundant IPC connection churn.

---

### 6. UI Sounds

Subtle, non-intrusive sound effects (48 kHz PCM stereo through PulseAudio, loaded into memory):

- `res/sounds/open.wav` when the dock appears
- `res/sounds/close.wav` when it hides
- `res/sounds/move.wav` when navigating between tiles
- `res/sounds/launch.wav` when launching an app

Sounds can be disabled in the Settings drawer or with the `--no-sound` CLI flag. The PulseAudio stream is opened on-demand and closed after 1.5 seconds of inactivity to avoid locking Samsung's audio sink across standby or HDMI switches.

---

## Language, Time and Weather: Automatic

By default, everything adapts automatically without manual configuration:

| Feature | Source / Behavior |
| --- | --- |
| Date & UI Language | Follows the TV menu language (`db/menu_widget/language`), falling back to `$LANG`, then English. Built-in translations: `en`, `es`, `fr`, `de`, `it`, `pt`, `nl`. |
| Time & DST | Follows the TV's system timezone (`/etc/localtime`), re-evaluated every time the dock opens and once per minute. |
| 12h / 24h Clock | Auto-detected by country (12h for US, CA, AU, NZ, PH; 24h elsewhere). |
| °C / °F Units | Auto-detected by country (°F for US and a few others; °C elsewhere). |
| Weather Location | Auto-detected via public IP using [ipwho.is](https://ipwho.is/) (fallback [ipapi.co](https://ipapi.co/)), cached for 24 hours. The last known location is retained when offline. |

> [!NOTE]
> Automatic IP geolocation queries public lookup services, and forecast requests send approximate coordinates (rounded to 3 decimal places) to [Open-Meteo](https://open-meteo.com/). If you prefer not to make external IP lookups, choose **Manual** in the Settings drawer or define a fixed location in `vetro-bar.conf`.

---

## Configuration & Overrides

Settings can be customized interactively in the **Settings drawer** (which saves directly to `vetro-bar.conf`), or manually via configuration file, CLI arguments, or environment variables.

Priority order: **CLI Arguments > Environment Variables > `vetro-bar.conf` > Automatic TV Defaults**.

### `vetro-bar.conf`

Located at `/opt/usr/home/owner/share/vetro-bar.conf`:

```ini
# language=es          # en, es, fr, de, it, pt, nl
# location=41.65,-4.72 # latitude,longitude (bypasses IP lookup)
# location_name=Madrid # custom display name for manual location
# weather_mode=manual  # auto or manual
# show_weather=true    # true or false
# weather_show_location=false # true or false
# weather_show_minmax=true    # true or false
# show_clock=true      # true or false
# clock=24             # 12 or 24
# show_date=true       # true or false
# date_format=long     # long, short, numeric, iso
# units=c              # c or f
# sound=true           # true or false
# volume_edge=left     # left, right, top, bottom, hidden
# tv_store_app=A2STVStore.Store # custom app ID for TV-Store shortcut
```

Supported CLI flags and environment variables:
- `--lang` / `TVLAUNCHER_LANG`
- `--location` / `TVLAUNCHER_LOCATION`
- `--clock` / `TVLAUNCHER_CLOCK`
- `--units` / `TVLAUNCHER_UNITS`
- `--sound` / `TVLAUNCHER_SOUND`
- `--volume-edge` / `TVLAUNCHER_VOLUME_EDGE`
- `--all-apps` (path to `allapps.tsv`)

---

## Project Structure

- `src/`: The launcher codebase:
  - `Core/`: Pure logic (dock layout, catalog parsing, weather engine, localization, settings model, volume placement, WAV parsing). Unit-tested without TV dependencies.
  - `Host/`: Host OS integration (evdev input handling, application launching, Home-guard process management, UPnP Sonos client, logging).
  - `Native/`: Native interop (EFL/Elementary P/Invoke, libc, PulseAudio `libpulse-simple`, audio output query).
  - `Ui/`: EFL rendering for the main dock (`Dock.cs`), modal settings drawer (`SettingsModal.cs`), and volume pill (`VolumePill.cs`).
- `tests/`: Desktop unit test suite (`dotnet run --project tests`).
- `res/`: Graphical assets (Phosphor icons, weather glyphs, volume indicators) and UI sound clips (`sounds/`).
- `tools/`: Python asset generators (`system_icons.py`, `svg_raster.py`), glass shader generators (`make-glass.cjs`), and the overlay deployment script (`overlay.py`).
- `designer/`: Standalone web preview tool to test dimensions and visual parameters.
- `apply.sh` / `restore.sh` / `status.sh`: TV deployment scripts.

---

## Building & Testing

Requires .NET Core 3.1 SDK:

```sh
sh build.sh                   # Builds Release netcoreapp3.1 DLL
dotnet run --project tests    # Executes desktop test suite (5500+ checks)
```

---

## Installation & Deployment

Communication with the TV is handled over HTTP via Vetro's local agent on port 8099 (root access over `sdb` is disabled on production Tizen 6 firmware):

```sh
export TV_IP=192.168.1.50     # Replace with your TV's IP address (required)
sh apply.sh                   # Backs up stock dock on first run and deploys vetro-bar
sh status.sh                  # Inspects running dock PID, checksums, and recent logs
sh restore.sh                 # Restores the original stock dock and restarts it
```

On first run, `apply.sh` archives Vetro's original dock in `native/dock/.vetro-bar-stock/` on the TV. Running `restore.sh` reinstalls that pristine backup at any time.

---

## Credits & License

- Licensed under the [MIT License](LICENSE).
- Icon glyphs in `tools/glyphs/regular` are from **[Phosphor Icons](https://phosphoricons.com/)** ([MIT License](tools/glyphs/LICENSE-phosphor.txt)).
- UI sound effects derived from royalty-free clips under the [Pixabay Content License](https://pixabay.com/service/license-summary/).
