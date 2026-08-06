from pathlib import Path
import io
import struct
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
ASSETS = [
    ("server-list-template.png", "server-list.sff", 1200),
    ("create-room-template.png", "create-room.sff", 1210),
]

for source_name, output_name, group in ASSETS:
    asset = ROOT / "game-overrides" / "data" / "ikemen1" / "andrew" / source_name
    output = asset.with_name(output_name)
    image = Image.open(asset).convert("RGB").resize((1280, 720), Image.Resampling.LANCZOS)
    pal = image.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.FLOYDSTEINBERG)
    buffer = io.BytesIO()
    pal.save(buffer, format="PCX")
    data = buffer.getvalue()
    header = bytearray(512)
    header[0:12] = b"ElecbyteSpr\x00"
    header[12:16] = bytes((0, 1, 0, 1))
    struct.pack_into("<I", header, 16, 1)
    struct.pack_into("<I", header, 20, 1)
    struct.pack_into("<I", header, 24, 512)
    struct.pack_into("<I", header, 28, 32)
    sub = bytearray(32)
    struct.pack_into("<IIhhHHH", sub, 0, 0, len(data), 0, 0, group, 0, 0)
    output.write_bytes(bytes(header) + bytes(sub) + data)
    print(output)
