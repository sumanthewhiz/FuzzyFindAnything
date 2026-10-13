"""Generate the original app icon using only the Python standard library."""

from pathlib import Path
import math
import struct
import zlib


def png(size: int) -> bytes:
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            u, v = (x + 0.5) / size, (y + 0.5) / size
            corner_x, corner_y = max(0.18 - u, u - 0.82, 0), max(0.18 - v, v - 0.82, 0)
            alpha = 255 if math.hypot(corner_x, corner_y) < 0.18 else 0
            color = (int(133 - 52 * v), int(105 - 41 * v), int(238 - 27 * v))
            ring = abs(math.hypot(u - 0.44, v - 0.42) - 0.19) < 0.034
            handle = 0.55 < u < 0.77 and abs((v - u) + 0.015) < 0.042 and v > 0.53
            star = (abs(u - 0.77) + abs(v - 0.23) < 0.082) and (
                abs(u - 0.77) < 0.027 or abs(v - 0.23) < 0.027
            )
            if ring or handle or star:
                color = (255, 255, 255)
            rows.extend((*color, alpha))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(rows)))
        + chunk(b"IEND", b"")
    )


if __name__ == "__main__":
    sizes = [32, 48, 64, 256]
    images = [png(size) for size in sizes]
    offset = 6 + 16 * len(sizes)
    headers = []
    for size, image in zip(sizes, images):
        headers.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(image), offset))
        offset += len(image)
    destination = Path(__file__).resolve().parents[1] / "src" / "FuzzyFindAnything.App" / "Assets" / "app.ico"
    destination.parent.mkdir(exist_ok=True)
    destination.write_bytes(struct.pack("<HHH", 0, 1, len(sizes)) + b"".join(headers) + b"".join(images))
    print(destination)
