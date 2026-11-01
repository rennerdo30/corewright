"""
Builds the installer artwork from the logo (owner, 2026-11-02: "installer does not use our logo for the exe" and
"also default, no logo or banner used"). Run: python scripts/build-installer-art.py (needs Pillow).

- installer/art/setup.ico: classic bitmap frames for 16-48 px (Explorer and the NSIS stub handle them everywhere)
  plus a PNG frame for 256 px.
- installer/art/welcome.bmp: the welcome/finish side image, 2x the MUI size (164x314) so it stays sharp on HiDPI.
- installer/art/header.bmp: the header image on the inner pages, 2x the MUI size (150x57).
"""
import io
import pathlib
import struct

from PIL import Image, ImageDraw, ImageFont

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / 'installer' / 'art'
OUT.mkdir(parents=True, exist_ok=True)
LOGO = Image.open(ROOT / 'assets' / 'corewright-256.png').convert('RGBA')
SMALL = Image.open(ROOT / 'assets' / 'corewright-16.png').convert('RGBA')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'

# Website palette (dark): ink background, accent, core colours.
BG = (18, 21, 29)
PANEL = (27, 31, 42)
LINE = (44, 50, 66)
INK = (232, 235, 241)
INK2 = (165, 172, 189)
ACCENT = (124, 139, 255)
GREEN = (46, 230, 168)


def bmp_frame(img: Image.Image) -> bytes:
    """32-bit BMP icon frame: BITMAPINFOHEADER (double height), bottom-up BGRA rows, then the AND mask."""
    w, h = img.size
    px = img.load()
    rows = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            rows += bytes((b, g, r, a))
    mask_stride = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(mask_stride)
        for x in range(w):
            if px[x, y][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(rows) + len(mask), 0, 0, 0, 0)
    return header + bytes(rows) + bytes(mask)


def icon():
    frames = []
    for size in (16, 20, 24, 32, 40, 48, 64):
        src = SMALL if size <= 24 else LOGO
        frames.append((size, bmp_frame(src.resize((size, size), Image.LANCZOS))))
    png = io.BytesIO()
    LOGO.save(png, format='PNG')
    frames.append((256, png.getvalue()))
    head = struct.pack('<HHH', 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries, data = b'', b''
    for size, blob in frames:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(blob), offset + len(data))
        data += blob
    (OUT / 'setup.ico').write_bytes(head + entries + data)


def core_grid(draw: ImageDraw.ImageDraw, x0: int, y0: int, cols: int, rows: int, cell: int, gap: int, seed: int, hot: set):
    """A chiplet of cores, each with two thread bars, like the website hero."""
    s = seed
    for r in range(rows):
        for c in range(cols):
            s = (s * 1664525 + 1013904223) & 0xFFFFFFFF
            x, y = x0 + c * (cell + gap), y0 + r * (cell + gap)
            draw.rounded_rectangle((x, y, x + cell, y + cell), radius=6, fill=PANEL, outline=LINE, width=2)
            for t in range(2):
                load = ((s >> (t * 8)) & 0xFF) / 255
                if (r, c) in hot:
                    load = 0.75 + load * 0.25
                else:
                    load = 0.08 + load * 0.35
                bx0 = x + 7 + t * (cell - 14) // 2 + (3 if t else 0)
                bx1 = bx0 + (cell - 14) // 2 - 3
                top = y + cell - 7 - int((cell - 14) * load)
                color = ACCENT if (r, c) in hot else (70, 78, 102)
                draw.rounded_rectangle((bx0, top, bx1, y + cell - 7), radius=2, fill=color)


def welcome():
    w, h = 328, 628
    img = Image.new('RGB', (w, h), BG)
    d = ImageDraw.Draw(img)
    # Package outline with pins, like a CPU seen from above.
    d.rounded_rectangle((20, 252, w - 20, h - 36), radius=14, outline=(32, 74, 56), width=3, fill=(16, 26, 24))
    for i in range(24, w - 24, 14):
        d.rectangle((i, 244, i + 6, 249), fill=(150, 140, 60))
        d.rectangle((i, h - 33, i + 6, h - 28), fill=(150, 140, 60))
    core_grid(d, 36, 270, 4, 4, 56, 12, seed=7, hot={(0, 0), (0, 1), (1, 0), (1, 1), (2, 1)})
    logo = LOGO.resize((112, 112), Image.LANCZOS)
    img.paste(logo, ((w - 112) // 2, 44), logo)
    title = ImageFont.truetype(FONT, 40)
    sub = ImageFont.truetype(FONT, 19)
    d.text((w // 2, 176), 'Corewright', font=title, fill=INK, anchor='mm')
    d.text((w // 2, 212), 'Every process on the right core', font=sub, fill=INK2, anchor='mm')
    img.save(OUT / 'welcome.bmp')


def header():
    w, h = 300, 114
    img = Image.new('RGB', (w, h), (255, 255, 255))
    d = ImageDraw.Draw(img)
    logo = LOGO.resize((72, 72), Image.LANCZOS)
    img.paste(logo, (w - 72 - 16, (h - 72) // 2), logo)
    img.save(OUT / 'header.bmp')


icon()
welcome()
header()
print('installer art written to', OUT)
