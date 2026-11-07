# vetro-bar

Overlay del dock nativo de [Vetro](https://github.com/) 0.5.0 para una Samsung QE55Q65AAUXXC (Tizen 6). No es un fork de Vetro: se construye la DLL del launcher y se copia encima de la instalación viva. Un update de Vetro se vuelve a aplicar con `apply.sh`.

El dock original se conserva en `native/dock/.vetro-bar-stock/` la primera vez.

## Qué cambia

Sobre el vídeo, en la pastilla:

- **TV** a la izquierda (abre `TdtSwitch0.Tv`), con un divisor
- las apps en el centro, con scroll
- **Ajustes** y **Home de Samsung** a la derecha, apilados, solo icono (engranaje / casa)

Fuera de la pastilla:

- tiempo de Valladolid a la izquierda (Open-Meteo)
- reloj 24 h a la derecha

No hay página Sources ni tiles HDMI. El icono de Steam de la tele no se toca.

## Layout

- `src/` el dock. `Core/` es lógica pura (layout, catálogo, tiempo) y tiene tests; `Native/` es P/Invoke; `Ui/` dibuja con EFL.
- `tests/` tests de escritorio: `dotnet run --project tests`.
- `res/` imágenes (barra, glow, anillo, iconos de sistema, `weather/`).
- `tools/` generadores (`system_icons.py`, `make-glass.cjs`).
- `apply.sh` / `restore.sh` / `status.sh` overlay en la tele.

## Build

```sh
sh build.sh                          # netcoreapp3.1
dotnet run --project tests           # lógica pura, sin tele
python3 -c "import sys; sys.path.insert(0,'tools'); import system_icons as s; s.render_all('res/icons')"
```

## Overlay

Hace falta `sdb` (Tizen Studio) y Developer Mode con Host PC IP = tu Mac.

```sh
export TV_IP=192.168.4.32            # opcional, ese es el valor por defecto
sh apply.sh                          # backup stock + copia DLL e iconos + reinicia vetro-dock
sh status.sh
sh restore.sh                        # vuelve al dock de Vetro
```

Tras un update de Vetro, vuelve a ejecutar `apply.sh`. Si Vetro sustituyó la DLL, el script no pisa el backup existente; si quieres un backup fresco del stock nuevo, borra `native/dock/.vetro-bar-stock/` en la tele y aplica otra vez.
