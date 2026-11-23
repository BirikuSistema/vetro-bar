"""Icons for the dock's system entries (TV, Settings, Home), inputs panel, weather, and Settings UI.

All glyphs come from Phosphor Icons (regular weight, tools/glyphs/regular/, MIT license).
Drawn at 4x and reduced via Lanczos for crisp anti-aliasing.
"""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

SIZE = 128            # design grid: every coordinate below is on this grid, drawn at SS x
import layout_consts
OUT = layout_consts.FOCUS_ICON   # final size, read from Layout.cs
SS = 4
# Corner radius on the 128 grid: the same percentage of the side as every other icon, ring and glow (Design.IconRadiusPct).
CORNER = layout_consts.design_num("IconRadiusPct") / 100.0 * SIZE
RIM_ALPHA = int(round(255 * layout_consts.design_num("IconRimAlpha")))

# slug -> (label, target, fallback app or None, accent)
SYSTEM = {
    "allapps": ("All apps", "panel:allapps", None, (196, 204, 220)),
    "settings": ("Settings", "key:XF86SysMenu", "org.tizen.menu", (196, 204, 220)),
    "samsunghome": ("Samsung Home", "sys:com.samsung.tv.csfs", None, (196, 204, 220)),
    "tvstore": ("TV-Store", "sys:A2STVStore.Store", "com.samsung.tv.store", (196, 204, 220)),
}

# One flat dark blue for the TV, Settings and Home tiles.
TILE_BLUE = (0x00, 0x5F, 0x73)

GLYPH_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "glyphs", "regular")
CUSTOM_GLYPH_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "glyphs", "custom")


def _flat_tile(rim=True):
    """Same rounded tile as the apps, but one flat colour: no vertical gradient and no sheen."""
    big = SIZE * SS
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), radius=CORNER * SS, fill=255)
    base = Image.new("RGBA", (big, big), TILE_BLUE + (255,))
    if rim:
        edge = Image.new("RGBA", (big, big), (0, 0, 0, 0))
        ImageDraw.Draw(edge).rounded_rectangle(
            (0, 0, big - 1, big - 1),
            radius=CORNER * SS,
            outline=(255, 255, 255, RIM_ALPHA),
            width=max(1, round(SIZE / OUT * SS)),
        )
        base = Image.alpha_composite(base, edge)
    base.putalpha(ImageChops.multiply(base.getchannel("A"), mask))
    return base


def _tile(accent):
    big = SIZE * SS
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), radius=CORNER * SS, fill=255)
    base = Image.new("RGBA", (big, big))
    px = base.load()
    for y in range(big):
        t = y / (big - 1)
        r = int(44 - 24 * t)
        g = int(54 - 28 * t)
        b = int(82 - 38 * t)
        for x in range(big):
            px[x, y] = (r, g, b, 255)
    # soft accent glow from the top left, so each tile has a hint of its colour
    glow = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(glow)
    d.ellipse((-big * 0.25, -big * 0.55, big * 0.85, big * 0.45), fill=accent + (70,))
    glow = glow.filter(ImageFilter.GaussianBlur(big * 0.12))
    base = Image.alpha_composite(base, glow)
    # rim
    rim = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle(
        (0, 0, big - 1, big - 1),
        radius=CORNER * SS,
        outline=(255, 255, 255, RIM_ALPHA),
        width=max(1, round(SIZE / OUT * SS)),
    )
    base = Image.alpha_composite(base, rim)
    base.putalpha(ImageChops.multiply(base.getchannel("A"), mask))
    return base


def _load_glyph(name, box_px, tint=None, directory=None):
    """Load a glyph PNG, crop to its ink bbox, fit into `box_px`, optionally tint RGB."""
    base_dir = directory if directory is not None else GLYPH_DIR
    g = Image.open(os.path.join(base_dir, name + ".png")).convert("RGBA")
    bbox = g.getchannel("A").getbbox()
    if bbox:
        g = g.crop(bbox)
    k = min(box_px / g.width, box_px / g.height)
    g = g.resize((max(1, round(g.width * k)), max(1, round(g.height * k))), Image.LANCZOS)
    if tint is not None:
        r, gr, b = tint
        alpha = g.getchannel("A")
        g = Image.merge(
            "RGBA",
            (
                Image.new("L", g.size, r),
                Image.new("L", g.size, gr),
                Image.new("L", g.size, b),
                alpha,
            ),
        )
    return g


def _phosphor(name, box, directory=None):
    """White glyph fitted into a box of `box` px (at 128 scale) and optically centred."""
    def draw(d, c, acc):
        g = _load_glyph(name, box * SS, directory=directory)
        layer = Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))
        layer.alpha_composite(g, ((SIZE * SS - g.width) // 2, (SIZE * SS - g.height) // 2))
        return ("image", layer)
    return draw


GLYPH_HEIGHT = {"allapps": 22, "settings": 23, "samsunghome": 22, "tvstore": 23}
GLYPH_BOX = 62
GLYPHS = {
    "allapps": _phosphor("squares-four", GLYPH_BOX),
    "settings": _phosphor("gear", GLYPH_BOX),
    "samsunghome": _phosphor("house", GLYPH_BOX),
    "tvstore": _phosphor("tv-store", 70, directory=CUSTOM_GLYPH_DIR),
}

# The inputs panel: slug -> (label, Samsung SourceType, accent). TV is 0, HDMI 1-4 are 13-16.
INPUTS = {
    "in-tv": ("TV", 0, (120, 214, 160)),
    "in-hdmi1": ("HDMI 1", 13, (90, 169, 255)),
    "in-hdmi2": ("HDMI 2", 14, (90, 169, 255)),
    "in-hdmi3": ("HDMI 3", 15, (90, 169, 255)),
    "in-hdmi4": ("HDMI 4", 16, (90, 169, 255)),
}

NUMBERS = {1: "number-one", 2: "number-two", 3: "number-three", 4: "number-four"}


def _hdmi_glyph(number):
    """Phosphor regular plug on top + Phosphor regular number-one..four underneath."""
    def draw(d, c, acc):
        big = SIZE * SS
        layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
        plug = _load_glyph("plug", 46 * SS)
        digit = _load_glyph(NUMBERS[number], 34 * SS)
        gap = 8 * SS
        total_h = plug.height + gap + digit.height
        y0 = (big - total_h) // 2
        layer.alpha_composite(plug, ((big - plug.width) // 2, y0))
        layer.alpha_composite(digit, ((big - digit.width) // 2, y0 + plug.height + gap))
        return ("image", layer)
    return draw


def render_input(slug, path):
    accent = INPUTS[slug][2]
    glyph = _phosphor("television-simple", 66) if slug == "in-tv" else _hdmi_glyph(int(slug[-1]))
    _render(accent, glyph, path)


def _control():
    """Round frosted button: translucent white over whatever the dock glass shows."""
    big = SIZE * SS
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, big - 1, big - 1), fill=255)
    base = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    px = base.load()
    for y in range(big):
        a = int(46 - 24 * y / (big - 1))
        for x in range(big):
            px[x, y] = (214, 224, 244, a)
    light = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(light).ellipse((-big * 0.1, -big * 0.35, big * 0.75, big * 0.45), fill=(255, 255, 255, 52))
    light = light.filter(ImageFilter.GaussianBlur(big * 0.08))
    base = Image.alpha_composite(base, light)
    rim = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(rim).ellipse((0, 0, big - 1, big - 1), outline=(255, 255, 255, 110), width=2 * SS)
    base = Image.alpha_composite(base, rim)
    base.putalpha(ImageChops.multiply(base.getchannel("A"), mask))
    return base


PILL_W, PILL_H = layout_consts.layout_int("PillItemW"), layout_consts.layout_int("PillItemH")


def _pill(w, h):
    big_w, big_h = w * SS, h * SS
    mask = Image.new("L", (big_w, big_h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, big_w - 1, big_h - 1), radius=big_h // 2, fill=255)
    base = Image.new("RGBA", (big_w, big_h), (0, 0, 0, 0))
    ImageDraw.Draw(base).rounded_rectangle(
        (0, 0, big_w - 1, big_h - 1),
        radius=big_h // 2,
        outline=(255, 255, 255, int(round(255 * layout_consts.design_num("PillBorderAlpha")))),
        width=2 * SS,
    )
    base.putalpha(ImageChops.multiply(base.getchannel("A"), mask))
    return base


def _pill_font(size):
    for path, index in (
        ("/System/Library/Fonts/Helvetica.ttc", 1),
        ("/System/Library/Fonts/SFNS.ttf", 0),
        ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 0),
    ):
        if os.path.isfile(path):
            try:
                return ImageFont.truetype(path, size, index=index)
            except OSError:
                pass
    return ImageFont.load_default(size)


def render(slug, path):
    """A pinned system entry: square icon-only tile, or an oval labelled pill."""
    if layout_consts.design_bool("IconOnlyPins"):
        _render(SYSTEM[slug][3], GLYPHS[slug], path, flat=True)
        return
    label = SYSTEM[slug][0]
    tile = _pill(PILL_W, PILL_H)
    layer = Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))
    extra = GLYPHS[slug](ImageDraw.Draw(layer), (255, 255, 255, 255), None)
    if extra and extra[0] == "image":
        layer = extra[1]
    g = layer.crop(layer.getchannel("A").getbbox())
    gh = round(GLYPH_HEIGHT[slug] * SS)
    g = g.resize((max(1, round(g.width * gh / g.height)), gh), Image.LANCZOS)
    font = _pill_font(int(layout_consts.design_num("PillTextSize")) * SS)
    probe = ImageDraw.Draw(Image.new("RGBA", (8, 8)))
    tb = probe.textbbox((0, 0), label, font=font)
    tw, th = tb[2] - tb[0], tb[3] - tb[1]
    gap = 10 * SS
    total = g.width + gap + tw
    x0 = (tile.width - total) // 2
    out = tile.copy()
    out.alpha_composite(g, (x0, (tile.height - g.height) // 2))
    text = Image.new("RGBA", tile.size, (0, 0, 0, 0))
    ImageDraw.Draw(text).text((x0 + g.width + gap - tb[0], (tile.height - th) // 2 - tb[1]), label, font=font, fill=(255, 255, 255, 255))
    out = Image.alpha_composite(out, text)
    out = out.resize((PILL_W, PILL_H), Image.LANCZOS)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    out.save(path)


def _render(accent, glyph, path, round_control=False, flat=False):
    tile = _control() if round_control else (_flat_tile() if flat else _tile(accent))
    layer = Image.new("RGBA", tile.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    extra = glyph(d, (255, 255, 255, 245), accent + (255,) if accent else None)
    if extra and extra[0] == "image":
        layer = extra[1]
    # a soft shadow under the glyph lifts it off the tile
    shadow = layer.filter(ImageFilter.GaussianBlur(3 * SS))
    shadow = Image.merge("RGBA", (Image.new("L", tile.size, 0),) * 3 + (shadow.getchannel("A").point(lambda v: int(v * 0.45)),))
    out = Image.alpha_composite(tile, Image.alpha_composite(shadow, layer))
    out = out.resize((OUT, OUT), Image.LANCZOS)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    out.save(path)


LIVE = {"livetv": None, "tvplus": (255, 170, 90)}


def render_live(slug, path):
    if slug == "livetv":
        _render(None, _phosphor("television-simple", 64), path, flat=True)
    else:
        _render(LIVE[slug], _phosphor("television-simple", 66), path)


WEATHER_SIZE = 80   # same pixels the dock draws, so the shadow is not scaled again

WEATHER_GLYPHS = {
    "sun": ("sun", (255, 244, 200)),
    "moon-stars": ("moon-stars", (226, 236, 255)),
    "cloud-sun": ("cloud-sun", (255, 246, 224)),
    "cloud-moon": ("cloud-moon", (226, 236, 255)),
    "cloud": ("cloud", (238, 244, 255)),
    "cloud-fog": ("cloud-fog", (218, 230, 248)),
    "cloud-rain": ("cloud-rain", (196, 224, 255)),
    "cloud-snow": ("cloud-snow", (245, 250, 255)),
    "cloud-lightning": ("cloud-lightning", (255, 242, 194)),
}


def render_weather(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for stale in ("rain.png", "snow.png"):
        p = os.path.join(out_dir, stale)
        if os.path.isfile(p):
            os.remove(p)
    big = WEATHER_SIZE * SS
    box = 66 * SS
    for name, (glyph_name, tint) in WEATHER_GLYPHS.items():
        g = _load_glyph(glyph_name, box, tint=tint)
        layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
        layer.alpha_composite(g, ((big - g.width) // 2, (big - g.height) // 2))
        shadow = layer.filter(ImageFilter.GaussianBlur(2 * SS))
        shadow = Image.merge("RGBA", (Image.new("L", (big, big), 0),) * 3 + (shadow.getchannel("A").point(lambda v: int(v * 0.40)),))
        img = Image.alpha_composite(shadow, layer).resize((WEATHER_SIZE, WEATHER_SIZE), Image.LANCZOS)
        img.save(os.path.join(out_dir, name + ".png"))


UI_ICONS = [
    "gear",
    "plug",
    "squares-four",
    "app-window",
    "wrench",
    "storefront",
    "sliders-horizontal",
    "cloud-sun",
    "compass",
    "arrows-down-up",
    "thermometer",
    "clock",
    "timer",
    "calendar",
    "calendar-dots",
    "speaker-high",
    "speaker-slash",
    "magnifying-glass",
    "caret-left",
    "caret-right",
    "caret-up",
    "caret-down",
    "backspace",
    "check",
    "x",
    "map-pin",
    "television-simple",
    "house",
]

# slug -> (glyph_name, box_px, is_custom)
QUICK_TILES = {
    "qs-hdmi": ("hdmi", 82, True),
    "qs-apps": ("squares-four", 62, False),
    "qs-vetro": ("vetro", 68, True),
    "qs-store": ("tv-store", 70, True),
    "qs-settings": ("gear", 62, False),
}


def render_quick_tiles(icons_dir):
    for slug, (glyph_name, box, is_custom) in QUICK_TILES.items():
        directory = CUSTOM_GLYPH_DIR if is_custom else GLYPH_DIR
        _render(None, _phosphor(glyph_name, box, directory=directory), os.path.join(icons_dir, slug + ".png"), flat=True)


def _render_switch(on, out_path):
    w, h = 56, 30
    bw, bh = w * SS, h * SS
    rad = 15 * SS
    track = Image.new("RGBA", (bw, bh), (0, 0, 0, 0))
    td = ImageDraw.Draw(track)
    fill = TILE_BLUE + (255,) if on else (12, 32, 48, 170)
    outline = (255, 255, 255, 115) if on else (255, 255, 255, 105)
    td.rounded_rectangle((0, 0, bw - 1, bh - 1), radius=rad, fill=fill, outline=outline, width=max(1, SS))

    knob_layer = Image.new("RGBA", (bw, bh), (0, 0, 0, 0))
    kd = ImageDraw.Draw(knob_layer)
    kx = (30 if on else 4) * SS
    ky = 4 * SS
    ks = 22 * SS
    knob_fill = (255, 255, 255, 255) if on else (220, 232, 244, 240)
    kd.ellipse((kx, ky, kx + ks - 1, ky + ks - 1), fill=knob_fill)

    shadow = knob_layer.filter(ImageFilter.GaussianBlur(1.5 * SS))
    shadow = Image.merge("RGBA", (Image.new("L", (bw, bh), 0),) * 3 + (shadow.getchannel("A").point(lambda v: int(v * 0.35)),))
    out = Image.alpha_composite(track, Image.alpha_composite(shadow, knob_layer)).resize((w, h), Image.LANCZOS)
    out.save(out_path)


def render_switches(ui_dir):
    _render_switch(True, os.path.join(ui_dir, "switch_on.png"))
    _render_switch(False, os.path.join(ui_dir, "switch_off.png"))


def _render_chevron(glyph_name, out_path):
    size = 24
    big = size * SS
    g = _load_glyph(glyph_name, 16 * SS, tint=(255, 255, 255))
    layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    layer.alpha_composite(g, ((big - g.width) // 2, (big - g.height) // 2))
    halo = layer.filter(ImageFilter.GaussianBlur(1.5 * SS))
    halo = Image.merge("RGBA", (Image.new("L", (big, big), 0),) * 3 + (halo.getchannel("A").point(lambda v: int(v * 0.55)),))
    out = Image.alpha_composite(halo, layer).resize((size, size), Image.LANCZOS)
    out.save(out_path)


def render_ui_icons(res_dir):
    _render_chevron("caret-up", os.path.join(res_dir, "chev_up.png"))
    _render_chevron("caret-down", os.path.join(res_dir, "chev_down.png"))

    ui_dir = os.path.join(res_dir, "ui")
    os.makedirs(ui_dir, exist_ok=True)
    size = 28
    big = size * SS
    box = 22 * SS
    for name in UI_ICONS:
        g = _load_glyph(name, box, tint=(255, 255, 255))
        layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
        layer.alpha_composite(g, ((big - g.width) // 2, (big - g.height) // 2))
        layer.resize((size, size), Image.LANCZOS).save(os.path.join(ui_dir, name + ".png"))
    render_switches(ui_dir)


def render_all(icons_dir):
    for slug in LIVE:
        render_live(slug, os.path.join(icons_dir, slug + ".png"))
    for slug in SYSTEM:
        render(slug, os.path.join(icons_dir, slug + ".png"))
    for slug in INPUTS:
        render_input(slug, os.path.join(icons_dir, slug + ".png"))
    render_quick_tiles(icons_dir)
    render_weather(os.path.join(os.path.dirname(os.path.abspath(icons_dir)), "weather"))


def inputs_tsv_rows():
    return ["\t".join([slug, label, "source:%d" % number]) for slug, (label, number, _) in INPUTS.items()]


def tsv_rows():
    rows = []
    for slug, (label, target, fallback, _) in SYSTEM.items():
        rows.append("\t".join([slug, label, target] + ([fallback] if fallback else [])))
    return rows


if __name__ == "__main__":
    res_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "res")
    render_all(os.path.join(res_dir, "icons"))
    render_ui_icons(res_dir)
    print("rendered Phosphor regular icons into", os.path.abspath(res_dir))
