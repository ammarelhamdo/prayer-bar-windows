"""Generates src/PrayerBar/app.ico: a gold crescent and star (same design as the tray icon)."""
import math
from pathlib import Path
from PIL import Image, ImageDraw

GOLD = (0xF2, 0xB8, 0x2E, 255)
S = 1024  # draw large, downsample for smooth edges


def star(cx, cy, outer, inner):
    pts = []
    for i in range(10):
        a = -math.pi / 2 + i * math.pi / 5
        r = outer if i % 2 == 0 else inner
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
moon = Image.new("L", (S, S), 0)
d = ImageDraw.Draw(moon)
k = S / 32
d.ellipse([2 * k, 2 * k, 30 * k, 30 * k], fill=255)
d.ellipse([10 * k, -1 * k, 36 * k, 25 * k], fill=0)
ImageDraw.Draw(moon).polygon(star(23 * k, 21 * k, 5.5 * k, 2.3 * k), fill=255)
img.paste(Image.new("RGBA", (S, S), GOLD), (0, 0), moon)

out = Path(__file__).resolve().parent.parent / "src" / "PrayerBar" / "app.ico"
img.save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print(out)
