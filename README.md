# vetro-bar

Overlay del dock nativo de Vetro 0.5.0 para una Samsung QE55Q65AAUXXC (Tizen 6). No es un fork de Vetro: se construye la DLL del launcher y se copia encima de la instalación viva. Si Vetro se actualiza, `apply.sh` lo vuelve a dejar como ahora. `restore.sh` devuelve el dock que había antes de la primera aplicación.

## Qué cambia

Sobre el vídeo, en la pastilla:

- **TV** a la izquierda (abre `TdtSwitch0.Tv`), con un divisor
- las apps en el centro, con scroll
- **Ajustes** y **Home de Samsung** a la derecha, apilados, solo icono (engranaje / casa)

Fuera de la pastilla:

- tiempo de Valladolid a la izquierda (Open-Meteo)
- reloj 24 h a la derecha

Sonidos de interfaz (48 kHz PCM vía PulseAudio en memoria, desactivables con `--no-sound`):

- `res/sounds/open.wav` al mostrar el dock
- `res/sounds/close.wav` al ocultarlo
- `res/sounds/move.wav` al desplazarse entre tiles
- `res/sounds/launch.wav` al lanzar una app

No hay página Sources ni tiles HDMI. El icono de Steam de la tele no se toca.

## Layout

- `src/` el dock. `Core/` es lógica pura (layout, catálogo, tiempo, WAV) y tiene tests; `Host/` gestiona entrada evdev, lanzamiento de apps y log; `Native/` es P/Invoke (EFL, libc, PulseAudio); `Ui/` dibuja con EFL.
- `tests/` tests de escritorio: `dotnet run --project tests`.
- `res/` imágenes y sonidos (`icons/`, `weather/`, `sounds/`).
- `tools/` generadores (`system_icons.py`, `make-glass.cjs`).
- `apply.sh` / `restore.sh` / `status.sh` overlay en la tele.

## Build

```sh
sh build.sh                          # netcoreapp3.1
dotnet run --project tests           # lógica pura, sin tele
python3 -c "import sys; sys.path.insert(0,'tools'); import system_icons as s; s.render_all('res/icons')"
```

## Ponerlo y quitarlo

No usa `sdb`. En esta tele el root por `sdb` está desactivado, y la IP del modo desarrollador tiene que seguir en `127.0.0.1` para que el agente de Vetro arranque. Los scripts hablan con ese agente (`http://192.168.4.32:8099`).

```sh
export TV_IP=192.168.4.32            # opcional, ese es el valor por defecto
sh apply.sh                          # la primera vez guarda el dock de Vetro y luego pone este
sh status.sh
sh restore.sh                        # vuelve a esa copia original y reinicia el dock
```

La copia original queda en la tele, en `native/dock/.vetro-bar-stock/`, al lado del dock que está en marcha. No se toca en las aplicaciones siguientes.

Si una actualización de Vetro borra esa carpeta, el siguiente `apply.sh` guarda el Vetro nuevo y después pone este dock. Si la copia vieja sigue ahí y quieres que `restore.sh` vuelva al Vetro actualizado, borra `.vetro-bar-stock` en la tele y vuelve a ejecutar `apply.sh`. El icono de Steam no se copia: vive fuera de esta carpeta.
