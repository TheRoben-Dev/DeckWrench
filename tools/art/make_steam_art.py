#!/usr/bin/env python3
"""Draws DeckWrench's Steam library art (capsules, hero, logo, icon) into data/steam-art/.
Clean shapes only: an open-end wrench in a ring on a dark gradient, and the wordmark.
Run: python3 tools/art/make_steam_art.py"""
import math
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "data", "steam-art")
FONT = os.path.join(ROOT, "src", "DeckWrench.App", "Assets", "Fonts", "ChakraPetch-SemiBold.ttf")
BG_TOP, BG_BOTTOM = (30, 24, 20), (10, 10, 13)
ACCENT = (245, 158, 59)
TEXT = (232, 234, 240)


def gradient(w, h):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        t = y / max(1, h - 1)
        c = tuple(int(BG_TOP[i] * (1 - t) + BG_BOTTOM[i] * t) for i in range(3))
        for x in range(w):
            px[x, y] = c
    return img


def glow(img, cx, cy, r, alpha=110):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=ACCENT + (alpha,))
    layer = layer.filter(ImageFilter.GaussianBlur(r * 0.6))
    img.paste(layer, (0, 0), layer)


def spark(d, cx, cy, r, ring=True, scale=4):
    """An open-end wrench at 45 degrees, with a thin ring around it."""
    img = d._image
    size = int(r * 2.4)
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    c = size / 2
    head = r * 0.42
    hy = c - r * 0.55  # head centre, above the middle
    ld.ellipse((c - head, hy - head, c + head, hy + head), fill=TEXT)
    hw = r * 0.15  # handle half-width
    ld.rounded_rectangle((c - hw, hy, c + hw, c + r * 0.95), radius=hw, fill=TEXT)
    ld.ellipse((c - hw * 1.5, c + r * 0.95 - hw * 3, c + hw * 1.5, c + r * 0.95), fill=TEXT)
    # the jaw: a slot cut into the top of the head
    jaw = r * 0.17
    ld.rectangle((c - jaw, hy - head * 1.1, c + jaw, hy - head * 0.05), fill=(0, 0, 0, 0))
    ld.ellipse((c - jaw, hy - jaw * 1.1, c + jaw, hy + jaw * 0.9), fill=(0, 0, 0, 0))
    # a hole near the end of the handle
    hole = hw * 0.55
    ld.ellipse((c - hole, c + r * 0.95 - hw * 1.5 - hole, c + hole, c + r * 0.95 - hw * 1.5 + hole), fill=(0, 0, 0, 0))
    layer = layer.rotate(-45, resample=Image.BICUBIC, center=(c, c))
    img.alpha_composite(layer, (int(cx - c), int(cy - c)))
    if ring:
        w = max(2, int(r * 0.07))
        rr = r * 1.18
        d.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), outline=ACCENT, width=w)


def supersample(w, h, draw, bg=True, ss=3):
    big = gradient(w * ss, h * ss).convert("RGBA") if bg else Image.new("RGBA", (w * ss, h * ss), (0, 0, 0, 0))
    draw(big, ss)
    return big.resize((w, h), Image.LANCZOS)


def wordmark(d, cx, cy, size, ss):
    f = ImageFont.truetype(FONT, int(size * ss))
    text = "DECKWRENCH"
    # wide letter spacing
    spacing = size * ss * 0.06
    widths = [d.textlength(ch, font=f) for ch in text]
    total = sum(widths) + spacing * (len(text) - 1)
    x = cx * ss - total / 2
    for ch, wch in zip(text, widths):
        d.text((x, cy * ss), ch, font=f, fill=TEXT, anchor="lm")
        x += wch + spacing


def tagline(d, cx, cy, size, ss, text="SYSTEM TOOLBOX"):
    f = ImageFont.truetype(FONT, int(size * ss))
    d.text((cx * ss, cy * ss), text, font=f, fill=ACCENT + (255,), anchor="mm")


def portrait(img, ss):
    w, h = img.size
    glow(img, w / 2, h * 0.38, w * 0.34)
    d = ImageDraw.Draw(img)
    spark(d, w / 2, h * 0.38, w * 0.2)
    wordmark(d, 300, 640, 66, ss)
    tagline(d, 300, 715, 28, ss)


def wide(img, ss):
    w, h = img.size
    glow(img, w * 0.2, h / 2, h * 0.36)
    d = ImageDraw.Draw(img)
    spark(d, w * 0.2, h / 2, h * 0.27)
    wordmark(d, 625, 195, 64, ss)
    tagline(d, 625, 262, 26, ss)


def hero(img, ss):
    w, h = img.size
    for cx, cy, r, a in [(0.18, 0.3, 0.32, 70), (0.82, 0.75, 0.4, 60), (0.5, 0.5, 0.25, 50)]:
        glow(img, w * cx, h * cy, h * r, a)
    d = ImageDraw.Draw(img)
    for cx, cy, r in [(0.12, 0.25, 0.06), (0.88, 0.7, 0.09), (0.7, 0.2, 0.035), (0.3, 0.8, 0.045)]:
        spark(d, w * cx, h * cy, h * r, ring=False)


def logo(img, ss):
    w, h = img.size
    d = ImageDraw.Draw(img)
    spark(d, w * 0.13, h / 2, h * 0.19)
    wordmark(d, 790, 330, 112, ss)
    tagline(d, 790, 440, 44, ss)


def icon(img, ss):
    w, h = img.size
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=int(w * 0.22), fill=255)
    bg = gradient(w, h).convert("RGBA")
    glow(bg, w / 2, h / 2, w * 0.3)
    d = ImageDraw.Draw(bg)
    spark(d, w / 2, h / 2, w * 0.3)
    img.paste(bg, (0, 0), mask)


def main():
    os.makedirs(OUT, exist_ok=True)
    supersample(600, 900, portrait).convert("RGB").save(os.path.join(OUT, "capsule.png"), optimize=True)
    supersample(920, 430, wide).convert("RGB").save(os.path.join(OUT, "wide.png"), optimize=True)
    supersample(1920, 620, hero, ss=2).convert("RGB").save(os.path.join(OUT, "hero.png"), optimize=True)
    supersample(1280, 720, logo, bg=False).save(os.path.join(OUT, "logo.png"), optimize=True)
    ico = supersample(256, 256, icon, bg=False)
    ico.save(os.path.join(OUT, "icon.png"), optimize=True)
    # The app's own icon (window, taskbar, Windows .exe).
    app = os.path.join(ROOT, "src", "DeckWrench.App")
    ico.save(os.path.join(app, "icon.png"), optimize=True)
    ico.save(os.path.join(app, "icon.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    main()
