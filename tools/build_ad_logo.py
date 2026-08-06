from pathlib import Path
import io
import math
import random
import struct

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "game-overrides" / "data" / "ikemen1"
SOURCE = OUT / "ad-logo-source.png"
SIZE = (1280, 720)


def to_pcx(image: Image.Image) -> bytes:
    pal = image.convert("RGB").quantize(
        colors=256,
        method=Image.Quantize.MEDIANCUT,
        dither=Image.Dither.FLOYDSTEINBERG,
    )
    buffer = io.BytesIO()
    pal.save(buffer, format="PCX")
    return buffer.getvalue()


def write_sff(path: Path, frames):
    encoded = [(0, number, 0, 0, to_pcx(frame)) for number, frame in enumerate(frames)]
    header = bytearray(512)
    header[0:12] = b"ElecbyteSpr\x00"
    header[12:16] = bytes((0, 1, 0, 1))
    struct.pack_into("<I", header, 16, 1)
    struct.pack_into("<I", header, 20, len(encoded))
    struct.pack_into("<I", header, 24, 512)
    struct.pack_into("<I", header, 28, 32)
    blocks = []
    offset = 512
    for index, (group, number, x, y, data) in enumerate(encoded):
        next_offset = 0 if index == len(encoded) - 1 else offset + 32 + len(data)
        sub = bytearray(32)
        struct.pack_into("<IIhhHHH", sub, 0, next_offset, len(data), x, y, group, number, 0)
        blocks.extend((sub, data))
        offset = next_offset
    path.write_bytes(bytes(header) + b"".join(blocks))


def make_frame(source: Image.Image, index: int, count: int) -> Image.Image:
    phase = index / count * math.tau
    pulse = 1.0 + 0.035 * math.sin(phase)
    brightness = 1.0 + 0.16 * math.sin(phase + 0.5)
    logo = ImageEnhance.Brightness(source).enhance(brightness)
    target_w = round(650 * pulse)
    target_h = round(650 * pulse)
    logo = logo.resize((target_w, target_h), Image.Resampling.LANCZOS)

    canvas = Image.new("RGBA", SIZE, (0, 0, 0, 255))
    x = (SIZE[0] - target_w) // 2
    y = (SIZE[1] - target_h) // 2
    canvas.alpha_composite(logo, (x, y))

    glow = Image.new("RGBA", SIZE, (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow, "RGBA")
    radius = 250 + round(16 * math.sin(phase))
    gd.ellipse((640-radius, 360-radius, 640+radius, 360+radius), fill=(255, 8, 12, 35))
    glow = glow.filter(ImageFilter.GaussianBlur(38))
    canvas = Image.alpha_composite(canvas, glow)

    rng = random.Random(AndrewSeed := 9800 + index)
    sparks = Image.new("RGBA", SIZE, (0, 0, 0, 0))
    sd = ImageDraw.Draw(sparks, "RGBA")
    for particle in range(54):
        base_x = rng.randrange(250, 1030)
        base_y = rng.randrange(175, 650)
        travel = (index * (3 + particle % 5)) % 90
        px = base_x + round(math.sin(phase + particle) * 18)
        py = base_y - travel
        r = 1 + particle % 4
        color = (255, 35 + particle % 90, 8, 150 + particle % 105)
        sd.ellipse((px-r, py-r, px+r, py+r), fill=color)
        sd.line((px, py, px-rng.randrange(5, 20), py+rng.randrange(4, 14)), fill=color, width=1)
    soft = sparks.filter(ImageFilter.GaussianBlur(5))
    canvas = Image.alpha_composite(canvas, soft)
    canvas = Image.alpha_composite(canvas, sparks)
    return canvas.convert("RGB")


def main():
    source = Image.open(SOURCE).convert("RGBA")
    frames = [make_frame(source, index, 16) for index in range(16)]
    write_sff(OUT / "ad-logo.sff", frames)
    frames[0].save(OUT / "ad-logo-preview.png")
    print(OUT / "ad-logo.sff")


if __name__ == "__main__":
    main()
