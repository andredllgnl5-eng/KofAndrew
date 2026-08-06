from pathlib import Path
import io
import struct

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "game-overrides" / "data" / "ikemen1" / "andrew"
SOURCE = OUT / "loading-fire-base.png"


def cover(image: Image.Image, size=(1280, 720)) -> Image.Image:
    image = image.convert("RGB")
    scale = max(size[0] / image.width, size[1] / image.height)
    resized = image.resize(
        (round(image.width * scale), round(image.height * scale)),
        Image.Resampling.LANCZOS,
    )
    left = (resized.width - size[0]) // 2
    top = (resized.height - size[1]) // 2
    return resized.crop((left, top, left + size[0], top + size[1]))


def fit_font(draw, text, font_path, maximum_width, start_size):
    size = start_size
    while size > 30:
        font = ImageFont.truetype(str(font_path), size)
        if draw.textbbox((0, 0), text, font=font, stroke_width=4)[2] <= maximum_width:
            return font
        size -= 2
    return ImageFont.truetype(str(font_path), size)


def build_image() -> Image.Image:
    base = cover(Image.open(SOURCE)).convert("RGBA")
    shade = Image.new("RGBA", base.size, (0, 0, 0, 0))
    ImageDraw.Draw(shade).ellipse((180, 165, 1100, 570), fill=(0, 0, 0, 150))
    shade = shade.filter(ImageFilter.GaussianBlur(95))
    base = Image.alpha_composite(base, shade)

    font_path = ROOT / "KofOnlineLauncher" / "Assets" / "OpenSans-BoldItalic.ttf"
    if not font_path.exists():
        font_path = Path("C:/Windows/Fonts/arialbd.ttf")
    draw = ImageDraw.Draw(base)
    title = "KofAndrew...."
    font = fit_font(draw, title, font_path, 1040, 126)
    center = (640, 350)

    glow = Image.new("RGBA", base.size, (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    for width, alpha in ((24, 40), (15, 80), (8, 145)):
        glow_draw.text(center, title, font=font, anchor="mm", fill=(255, 20, 35, alpha),
                       stroke_width=width, stroke_fill=(255, 0, 25, alpha))
    glow = glow.filter(ImageFilter.GaussianBlur(12))
    base = Image.alpha_composite(base, glow)
    draw = ImageDraw.Draw(base)
    draw.text(center, title, font=font, anchor="mm", fill=(220, 18, 38, 255),
              stroke_width=6, stroke_fill=(60, 0, 8, 255))
    draw.text((640, 445), "MADE BY ANDREW", font=ImageFont.truetype(str(font_path), 27),
              anchor="mm", fill=(255, 170, 80, 245), stroke_width=2, stroke_fill=(50, 0, 8, 255))
    return base.convert("RGB")


def to_pcx(image: Image.Image) -> bytes:
    pal = image.quantize(colors=256, method=Image.Quantize.MEDIANCUT,
                         dither=Image.Dither.FLOYDSTEINBERG)
    buffer = io.BytesIO()
    pal.save(buffer, format="PCX")
    return buffer.getvalue()


def write_sff(path: Path, image: Image.Image):
    data = to_pcx(image)
    header = bytearray(512)
    header[0:12] = b"ElecbyteSpr\x00"
    header[12:16] = bytes((0, 1, 0, 1))
    struct.pack_into("<I", header, 16, 1)
    struct.pack_into("<I", header, 20, 1)
    struct.pack_into("<I", header, 24, 512)
    struct.pack_into("<I", header, 28, 32)
    sub = bytearray(32)
    struct.pack_into("<IIhhHHH", sub, 0, 0, len(data), 0, 0, 0, 0, 0)
    path.write_bytes(bytes(header) + bytes(sub) + data)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    image = build_image()
    image.save(OUT / "loading-preview.png")
    write_sff(OUT / "loading.sff", image)
    (OUT / "loading.def").write_text(
        """[SceneDef]
spr = loading.sff
startscene = 0

[Scene 0]
fadein.time = 8
fadeout.time = 8
clearcolor = 0,0,0
layer0.anim = 0
layer0.offset = 0,0
end.time = 999999

[Begin Action 0]
0,0, 0,0, -1
""",
        encoding="utf-8",
    )
    print(OUT / "loading.sff")


if __name__ == "__main__":
    main()
