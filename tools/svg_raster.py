#!/usr/bin/env python3
"""Rasterizes Phosphor SVG glyphs into 512x512 white-on-transparent PNGs via macOS CoreGraphics."""

import ctypes
import math
import os
import re
import sys
import xml.etree.ElementTree as ET
from PIL import Image

_CG_PATH = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics"
_cg = ctypes.CDLL(_CG_PATH)

_c_double = ctypes.c_double
_c_void_p = ctypes.c_void_p
_c_size_t = ctypes.c_size_t
_c_uint32 = ctypes.c_uint32

_cg.CGColorSpaceCreateDeviceRGB.argtypes = []
_cg.CGColorSpaceCreateDeviceRGB.restype = _c_void_p

_cg.CGColorSpaceRelease.argtypes = [_c_void_p]
_cg.CGColorSpaceRelease.restype = None

_cg.CGBitmapContextCreate.argtypes = [_c_void_p, _c_size_t, _c_size_t, _c_size_t, _c_size_t, _c_void_p, _c_uint32]
_cg.CGBitmapContextCreate.restype = _c_void_p

_cg.CGContextRelease.argtypes = [_c_void_p]
_cg.CGContextRelease.restype = None

_cg.CGContextTranslateCTM.argtypes = [_c_void_p, _c_double, _c_double]
_cg.CGContextScaleCTM.argtypes = [_c_void_p, _c_double, _c_double]
_cg.CGContextSetRGBFillColor.argtypes = [_c_void_p, _c_double, _c_double, _c_double, _c_double]
_cg.CGContextBeginPath.argtypes = [_c_void_p]
_cg.CGContextMoveToPoint.argtypes = [_c_void_p, _c_double, _c_double]
_cg.CGContextAddLineToPoint.argtypes = [_c_void_p, _c_double, _c_double]
_cg.CGContextAddCurveToPoint.argtypes = [_c_void_p, _c_double, _c_double, _c_double, _c_double, _c_double, _c_double]
_cg.CGContextAddQuadCurveToPoint.argtypes = [_c_void_p, _c_double, _c_double, _c_double, _c_double]
_cg.CGContextClosePath.argtypes = [_c_void_p]
_cg.CGContextFillPath.argtypes = [_c_void_p]
_cg.CGContextEOFillPath.argtypes = [_c_void_p]

_NUM_RE = re.compile(r"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")
_CMD_SET = set("MmLlHhVvCcSsQqTtAaZz")


class _PathCursor:
    def __init__(self, d):
        self.d = d
        self.i = 0
        self.n = len(d)

    def _skip_sep(self):
        while self.i < self.n and self.d[self.i] in " \t\r\n,":
            self.i += 1

    def has_number(self):
        self._skip_sep()
        if self.i >= self.n:
            return False
        return self.d[self.i] not in _CMD_SET

    def next_cmd(self):
        self._skip_sep()
        if self.i >= self.n:
            return None
        ch = self.d[self.i]
        if ch in _CMD_SET:
            self.i += 1
            return ch
        return None

    def next_number(self):
        self._skip_sep()
        m = _NUM_RE.match(self.d, self.i)
        if not m:
            raise ValueError("Expected number at pos %d in path: %s" % (self.i, self.d[self.i : self.i + 20]))
        self.i = m.end()
        return float(m.group(0))

    def next_flag(self):
        self._skip_sep()
        if self.i >= self.n or self.d[self.i] not in ("0", "1"):
            raise ValueError("Expected arc flag at pos %d in path: %s" % (self.i, self.d[self.i : self.i + 20]))
        v = 1 if self.d[self.i] == "1" else 0
        self.i += 1
        return v


def _arc_to_cubic(x0, y0, rx, ry, phi_deg, large_arc, sweep, x, y):
    """Convert SVG elliptical arc (W3C F.6.5 / F.6.6) into cubic Bezier segments."""
    if x0 == x and y0 == y:
        return []
    rx = abs(rx)
    ry = abs(ry)
    if rx == 0 or ry == 0:
        return [(None, None, None, None, x, y)]

    phi = math.radians(phi_deg % 360.0)
    cos_phi = math.cos(phi)
    sin_phi = math.sin(phi)

    dx2 = (x0 - x) / 2.0
    dy2 = (y0 - y) / 2.0
    x1p = cos_phi * dx2 + sin_phi * dy2
    y1p = -sin_phi * dx2 + cos_phi * dy2

    lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
    if lam > 1.0:
        s = math.sqrt(lam)
        rx *= s
        ry *= s

    rx2 = rx * rx
    ry2 = ry * ry
    x1p2 = x1p * x1p
    y1p2 = y1p * y1p

    num = max(0.0, rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2)
    den = rx2 * y1p2 + ry2 * x1p2
    factor = math.sqrt(num / den) if den > 0 else 0.0
    if large_arc == sweep:
        factor = -factor

    cxp = factor * (rx * y1p) / ry
    cyp = factor * -(ry * x1p) / rx

    cx = cos_phi * cxp - sin_phi * cyp + (x0 + x) / 2.0
    cy = sin_phi * cxp + cos_phi * cyp + (y0 + y) / 2.0

    def vec_angle(ux, uy, vx, vy):
        dot = ux * vx + uy * vy
        mag = math.hypot(ux, uy) * math.hypot(vx, vy)
        if mag == 0:
            return 0.0
        c = max(-1.0, min(1.0, dot / mag))
        ang = math.acos(c)
        if ux * vy - uy * vx < 0:
            ang = -ang
        return ang

    ux = (x1p - cxp) / rx
    uy = (y1p - cyp) / ry
    vx = (-x1p - cxp) / rx
    vy = (-y1p - cyp) / ry

    theta1 = vec_angle(1.0, 0.0, ux, uy)
    dtheta = vec_angle(ux, uy, vx, vy)
    if sweep == 0 and dtheta > 0:
        dtheta -= 2.0 * math.pi
    elif sweep == 1 and dtheta < 0:
        dtheta += 2.0 * math.pi

    segs = max(1, int(math.ceil(abs(dtheta) / (math.pi / 2.0))))
    delta = dtheta / segs
    t = (4.0 / 3.0) * math.tan(delta / 4.0)

    out = []
    for k in range(segs):
        t1 = theta1 + k * delta
        t2 = t1 + delta
        c1, s1 = math.cos(t1), math.sin(t1)
        c2, s2 = math.cos(t2), math.sin(t2)

        p1x = c1 - t * s1
        p1y = s1 + t * c1
        p2x = c2 + t * s2
        p2y = s2 - t * c2

        def xform(px, py):
            ex = rx * px
            ey = ry * py
            return (
                cos_phi * ex - sin_phi * ey + cx,
                sin_phi * ex + cos_phi * ey + cy,
            )

        cp1x, cp1y = xform(p1x, p1y)
        cp2x, cp2y = xform(p2x, p2y)
        endx, endy = xform(c2, s2)
        out.append((cp1x, cp1y, cp2x, cp2y, endx, endy))
    return out


def _add_svg_path(ctx, d):
    cur = _PathCursor(d)
    cx = cy = 0.0
    sx = sy = 0.0
    last_cmd = None
    last_cp_x = last_cp_y = 0.0

    while True:
        cmd = cur.next_cmd()
        if cmd is None:
            break

        if cmd in ("Z", "z"):
            _cg.CGContextClosePath(ctx)
            cx, cy = sx, sy
            last_cmd = cmd
            continue

        repeat_cmd = cmd
        if cmd == "M":
            repeat_cmd = "L"
        elif cmd == "m":
            repeat_cmd = "l"

        first = True
        while first or cur.has_number():
            c = cmd if first else repeat_cmd
            first = False
            rel = c.islower()
            uc = c.upper()

            if uc == "M":
                x = cur.next_number() + (cx if rel else 0.0)
                y = cur.next_number() + (cy if rel else 0.0)
                _cg.CGContextMoveToPoint(ctx, x, y)
                cx = sx = x
                cy = sy = y
            elif uc == "L":
                x = cur.next_number() + (cx if rel else 0.0)
                y = cur.next_number() + (cy if rel else 0.0)
                _cg.CGContextAddLineToPoint(ctx, x, y)
                cx, cy = x, y
            elif uc == "H":
                x = cur.next_number() + (cx if rel else 0.0)
                _cg.CGContextAddLineToPoint(ctx, x, cy)
                cx = x
            elif uc == "V":
                y = cur.next_number() + (cy if rel else 0.0)
                _cg.CGContextAddLineToPoint(ctx, cx, y)
                cy = y
            elif uc == "C":
                x1 = cur.next_number() + (cx if rel else 0.0)
                y1 = cur.next_number() + (cy if rel else 0.0)
                x2 = cur.next_number() + (cx if rel else 0.0)
                y2 = cur.next_number() + (cy if rel else 0.0)
                x = cur.next_number() + (cx if rel else 0.0)
                y = cur.next_number() + (cy if rel else 0.0)
                _cg.CGContextAddCurveToPoint(ctx, x1, y1, x2, y2, x, y)
                last_cp_x, last_cp_y = x2, y2
                cx, cy = x, y
            elif uc == "S":
                if last_cmd in ("C", "c", "S", "s"):
                    x1 = 2.0 * cx - last_cp_x
                    y1 = 2.0 * cy - last_cp_y
                else:
                    x1, y1 = cx, cy
                x2 = cur.next_number() + (cx if rel else 0.0)
                y2 = cur.next_number() + (cy if rel else 0.0)
                x = cur.next_number() + (cx if rel else 0.0)
                y = cur.next_number() + (cy if rel else 0.0)
                _cg.CGContextAddCurveToPoint(ctx, x1, y1, x2, y2, x, y)
                last_cp_x, last_cp_y = x2, y2
                cx, cy = x, y
            elif uc == "Q":
                x1 = cur.next_number() + (cx if rel else 0.0)
                y1 = cur.next_number() + (cy if rel else 0.0)
                x = cur.next_number() + (cx if rel else 0.0)
                y = cur.next_number() + (cy if rel else 0.0)
                _cg.CGContextAddQuadCurveToPoint(ctx, x1, y1, x, y)
                last_cp_x, last_cp_y = x1, y1
                cx, cy = x, y
            elif uc == "T":
                if last_cmd in ("Q", "q", "T", "t"):
                    x1 = 2.0 * cx - last_cp_x
                    y1 = 2.0 * cy - last_cp_y
                else:
                    x1, y1 = cx, cy
                x = cur.next_number() + (cx if rel else 0.0)
                y = cur.next_number() + (cy if rel else 0.0)
                _cg.CGContextAddQuadCurveToPoint(ctx, x1, y1, x, y)
                last_cp_x, last_cp_y = x1, y1
                cx, cy = x, y
            elif uc == "A":
                rx = cur.next_number()
                ry = cur.next_number()
                rot = cur.next_number()
                large = cur.next_flag()
                sweep = cur.next_flag()
                x = cur.next_number() + (cx if rel else 0.0)
                y = cur.next_number() + (cy if rel else 0.0)
                for seg in _arc_to_cubic(cx, cy, rx, ry, rot, large, sweep, x, y):
                    cp1x, cp1y, cp2x, cp2y, ex, ey = seg
                    if cp1x is None:
                        _cg.CGContextAddLineToPoint(ctx, ex, ey)
                    else:
                        _cg.CGContextAddCurveToPoint(ctx, cp1x, cp1y, cp2x, cp2y, ex, ey)
                cx, cy = x, y
            last_cmd = c


def rasterize_svg(svg_path, png_path, size=512):
    tree = ET.parse(svg_path)
    root = tree.getroot()
    vb = root.attrib.get("viewBox", "0 0 256 256").replace(",", " ").split()
    vb_x = float(vb[0])
    vb_y = float(vb[1])
    vb_w = float(vb[2])
    vb_h = float(vb[3])

    buf = (ctypes.c_uint8 * (size * size * 4))()
    cs = _cg.CGColorSpaceCreateDeviceRGB()
    ctx = _cg.CGBitmapContextCreate(ctypes.cast(buf, _c_void_p), size, size, 8, size * 4, cs, 1)
    try:
        _cg.CGContextTranslateCTM(ctx, 0.0, float(size))
        _cg.CGContextScaleCTM(ctx, float(size) / vb_w, -float(size) / vb_h)
        if vb_x != 0.0 or vb_y != 0.0:
            _cg.CGContextTranslateCTM(ctx, -vb_x, -vb_y)
        _cg.CGContextSetRGBFillColor(ctx, 1.0, 1.0, 1.0, 1.0)

        for elem in root.iter():
            tag = elem.tag.split("}")[-1]
            if tag == "path" and "d" in elem.attrib:
                _cg.CGContextBeginPath(ctx)
                _add_svg_path(ctx, elem.attrib["d"])
                if elem.attrib.get("fill-rule") == "evenodd":
                    _cg.CGContextEOFillPath(ctx)
                else:
                    _cg.CGContextFillPath(ctx)
    finally:
        _cg.CGContextRelease(ctx)
        _cg.CGColorSpaceRelease(cs)

    img = Image.frombytes("RGBA", (size, size), bytes(buf), "raw", "RGBA")
    # CoreGraphics produces premultiplied white (a, a, a, a). Convert to straight white RGBA (255, 255, 255, a).
    alpha = img.getchannel("A")
    white = Image.new("L", (size, size), 255)
    out = Image.merge("RGBA", (white, white, white, alpha))
    if out.getchannel("A").getbbox() is None:
        raise RuntimeError("Rasterized SVG is blank: " + svg_path)
    out.save(png_path)


def rasterize_dir(glyph_dir, size=512):
    count = 0
    for name in sorted(os.listdir(glyph_dir)):
        if not name.endswith(".svg"):
            continue
        svg_path = os.path.join(glyph_dir, name)
        png_path = os.path.join(glyph_dir, name[:-4] + ".png")
        rasterize_svg(svg_path, png_path, size=size)
        count += 1
    return count


if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "glyphs", "regular")
    n = rasterize_dir(target)
    print("rasterized %d SVGs in %s" % (n, target))
