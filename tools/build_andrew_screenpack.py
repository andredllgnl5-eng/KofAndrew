from pathlib import Path
import io
import math
import random
import struct

from PIL import Image, ImageEnhance, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "KofOnlineLauncher" / "Assets" / "kyo-orochi-andrew-background.png"
MENU_SOURCE = ROOT / "game-overrides" / "data" / "ikemen1" / "andrew" / "menu-fire-base.png"
OUT = ROOT / "game-overrides" / "data" / "ikemen1" / "andrew"


def cover(image: Image.Image, size=(1280, 720)) -> Image.Image:
    image = image.convert("RGB")
    scale = max(size[0] / image.width, size[1] / image.height)
    resized = image.resize((round(image.width * scale), round(image.height * scale)), Image.Resampling.LANCZOS)
    left = (resized.width - size[0]) // 2
    top = (resized.height - size[1]) // 2
    return resized.crop((left, top, left + size[0], top + size[1]))


def base_variant(image: Image.Image, select=False) -> Image.Image:
    image = ImageEnhance.Contrast(image).enhance(1.12)
    image = ImageEnhance.Color(image).enhance(1.15)
    draw = ImageDraw.Draw(image, "RGBA")
    if select:
        draw.rectangle((0, 0, 1280, 720), fill=(5, 2, 10, 62))
        draw.rectangle((300, 70, 980, 650), fill=(2, 2, 8, 74))
    else:
        draw.rectangle((0, 0, 690, 720), fill=(0, 0, 4, 70))
    font_path = Path("C:/Windows/Fonts/arialbd.ttf")
    signature_font = ImageFont.truetype(str(font_path), 22)
    title_font = ImageFont.truetype(str(font_path), 38)
    draw.text((1238, 687), "Made By Andrew", font=signature_font, anchor="rs", fill=(255, 194, 67, 245), stroke_width=2, stroke_fill=(25, 0, 8, 235))
    draw.text((42, 45), "KOF OROCHI ONLINE" if not select else "SELECT YOUR FIGHTER", font=title_font, fill=(255, 220, 160, 245), stroke_width=3, stroke_fill=(76, 0, 22, 245))
    return image


def menu_variant(image: Image.Image) -> Image.Image:
    image = ImageEnhance.Contrast(image).enhance(1.08)
    image = ImageEnhance.Color(image).enhance(1.12)
    draw = ImageDraw.Draw(image, "RGBA")
    title_font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 47)
    signature_font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 20)
    draw.text((640, 54), "KOF MUGEN ONLINE", font=title_font, anchor="ma",
              fill=(244, 236, 222, 255), stroke_width=4, stroke_fill=(115, 0, 12, 255))
    # Os textos permanecem nativos e clicáveis; somente as molduras são rasterizadas.
    for index in range(8):
        y0 = 122 + index * 68
        y1 = y0 + 55
        draw.rounded_rectangle((338, y0, 942, y1), radius=8,
                               fill=(7, 7, 9, 218), outline=(126, 12, 20, 245), width=2)
        draw.line((365, y1 - 3, 915, y1 - 3), fill=(230, 27, 37, 130), width=1)
    draw.text((1245, 696), "Made By Andrew", font=signature_font, anchor="rs",
              fill=(255, 214, 151, 245), stroke_width=2, stroke_fill=(45, 0, 5, 240))
    return image


def energy_frame(seed: int, select=False) -> Image.Image:
    rng = random.Random(seed)
    layer = Image.new("RGB", (1280, 720), (0, 0, 0))
    sharp = ImageDraw.Draw(layer)
    glow = Image.new("RGBA", (1280, 720), (0, 0, 0, 0))
    g = ImageDraw.Draw(glow, "RGBA")
    for _ in range(125 if select else 165):
        x = rng.randrange(0, 1280)
        y = rng.randrange(0, 720)
        r = rng.randrange(2, 7)
        color = (255, rng.randrange(35, 105), rng.randrange(55, 155))
        sharp.ellipse((x-r, y-r, x+r, y+r), fill=color)
        sharp.line((x-rng.randrange(10, 34), y+rng.randrange(4, 16), x+rng.randrange(4, 15), y), fill=color, width=max(1, r//2))
        g.ellipse((x-r*4, y-r*4, x+r*4, y+r*4), fill=(*color, 115))
    for i in range(5):
        x = 930 + i * 58 + rng.randrange(-30, 30)
        y = 180 + rng.randrange(-30, 100)
        r = 55 + rng.randrange(0, 45)
        color = (150 + i * 15, 20, 255, 45)
        g.ellipse((x-r, y-r, x+r, y+r), fill=color)
    glow = glow.filter(ImageFilter.GaussianBlur(10))
    layer = Image.alpha_composite(layer.convert("RGBA"), glow).convert("RGB")
    return layer


def to_pcx(image: Image.Image) -> bytes:
    # Perceptual adaptive palette keeps the key art readable in SFF v1.
    pal = image.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.FLOYDSTEINBERG)
    buffer = io.BytesIO()
    pal.save(buffer, format="PCX")
    return buffer.getvalue()


def write_sff(path: Path, sprites):
    encoded = [(group, number, x, y, to_pcx(image)) for group, number, x, y, image in sprites]
    header = bytearray(512)
    header[0:12] = b"ElecbyteSpr\x00"
    # SFF v1 stores the version in reverse byte order: 0.1.0.1.
    header[12:16] = bytes((0, 1, 0, 1))
    groups = len({group for group, *_ in encoded})
    struct.pack_into("<I", header, 16, groups)
    struct.pack_into("<I", header, 20, len(encoded))
    struct.pack_into("<I", header, 24, 512)
    struct.pack_into("<I", header, 28, 32)
    header[32] = 0

    blocks = []
    offset = 512
    for idx, (group, number, x, y, data) in enumerate(encoded):
        next_offset = 0 if idx == len(encoded) - 1 else offset + 32 + len(data)
        sub = bytearray(32)
        struct.pack_into("<IIhhHHH", sub, 0, next_offset, len(data), x, y, group, number, 0)
        sub[18] = 0
        blocks.extend((sub, data))
        offset = next_offset
    path.write_bytes(bytes(header) + b"".join(blocks))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    source = cover(Image.open(SOURCE))
    title = menu_variant(cover(Image.open(MENU_SOURCE)))
    select = base_variant(source.copy(), select=True)
    overlays = [energy_frame(AndrewSeed, False) for AndrewSeed in (98, 1998, 2026, 777)]
    select_overlays = [energy_frame(AndrewSeed, True) for AndrewSeed in (198, 2998, 3026, 888)]
    write_sff(OUT / "titlebg.sff", [(1000, 0, 0, 0, title)] + [(1001, i, 0, 0, frame) for i, frame in enumerate(overlays)])
    write_sff(OUT / "selectbg.sff", [(1100, 0, 0, 0, select)] + [(1101, i, 0, 0, frame) for i, frame in enumerate(select_overlays)])
    title.save(OUT / "title-preview.jpg", quality=92)
    select.save(OUT / "select-preview.jpg", quality=92)
    print(OUT / "titlebg.sff")
    print(OUT / "selectbg.sff")


if __name__ == "__main__":
    main()
