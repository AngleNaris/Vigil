"""Generate SystemAutorun icon.ico — purple/black, sharp right angles."""
from __future__ import annotations

import struct
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageDraw

BG = (19, 17, 22, 255)  # #131116
PRIMARY = (103, 80, 164, 255)  # #6750a4
PRIMARY_DIM = (88, 65, 143, 255)  # #58418f
ACCENT = (191, 171, 241, 255)  # #bfabf1

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def u(size: int, n: float) -> int:
    return max(1, int(round(size * n / 64.0)))


def draw_icon(size: int) -> Image.Image:
    """
    Layout (64-unit grid):
    - Full dark fill + purple outer frame
    - Left: 3 list rows (status square + bar)
    - Right: angular power cradle (U) + accent stem
    - Bottom: baseline with accent tip
    Bold strokes so 16px still reads.
    """
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Background plate
    d.rectangle([0, 0, size - 1, size - 1], fill=BG)

    # Outer frame
    fw = max(1, u(size, 4 if size <= 24 else 3))
    d.rectangle([0, 0, size - 1, size - 1], outline=PRIMARY, width=fw)

    # Inner padding
    p = fw + u(size, 4)
    L, T, R, B = p, p, size - 1 - p, size - 1 - p
    mid_x = (L + R) // 2

    # ── LEFT: task list ──
    bar_h = max(2, u(size, 6 if size <= 32 else 5))
    gap = max(1, u(size, 4 if size <= 32 else 3))
    sq = bar_h
    list_right = mid_x - u(size, 3)
    y = T + u(size, 2)
    for i in range(3):
        # status square
        d.rectangle([L, y, L + sq - 1, y + bar_h - 1], fill=ACCENT if i == 0 else PRIMARY)
        # bar
        bx0 = L + sq + u(size, 2)
        if list_right > bx0:
            d.rectangle(
                [bx0, y, list_right, y + bar_h - 1],
                fill=PRIMARY if i < 2 else PRIMARY_DIM,
            )
        y += bar_h + gap

    # ── RIGHT: power mark (right-angle U + stem) ──
    stroke = max(2, u(size, 6 if size <= 32 else 5))
    rx0 = mid_x + u(size, 2)
    rx1 = R
    ry0 = T + u(size, 3)
    ry1 = min(B - u(size, 10), T + u(size, 34))
    if ry1 <= ry0 + stroke * 2:
        ry1 = ry0 + stroke * 3

    # verticals + bottom
    d.rectangle([rx0, ry0 + stroke, rx0 + stroke - 1, ry1], fill=PRIMARY)
    d.rectangle([rx1 - stroke + 1, ry0 + stroke, rx1, ry1], fill=PRIMARY)
    d.rectangle([rx0, ry1 - stroke + 1, rx1, ry1], fill=PRIMARY)
    # top stubs
    stub = max(stroke + 1, u(size, 8))
    d.rectangle([rx0, ry0, rx0 + stub - 1, ry0 + stroke - 1], fill=PRIMARY)
    d.rectangle([rx1 - stub + 1, ry0, rx1, ry0 + stroke - 1], fill=PRIMARY)
    # stem
    cx = (rx0 + rx1) // 2
    sw = max(2, stroke - 1)
    d.rectangle(
        [cx - sw // 2, T, cx - sw // 2 + sw - 1, ry0 + u(size, 16)],
        fill=ACCENT,
    )

    # ── BOTTOM baseline ──
    bh = max(2, u(size, 5))
    d.rectangle([L, B - bh + 1, R, B], fill=PRIMARY_DIM)
    tip = max(3, u(size, 14))
    d.rectangle([R - tip + 1, B - bh + 1, R, B], fill=ACCENT)

    return img


def png_bytes(im: Image.Image) -> bytes:
    buf = BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: Path, images: list[Image.Image]) -> None:
    entries = []
    payloads = []
    for im in images:
        im = im.convert("RGBA")
        data = png_bytes(im)
        w, h = im.size
        entries.append((0 if w >= 256 else w, 0 if h >= 256 else h, len(data)))
        payloads.append(data)

    count = len(entries)
    offset = 6 + 16 * count
    chunks = [struct.pack("<HHH", 0, 1, count)]
    for (wb, hb, nbytes), _ in zip(entries, payloads):
        chunks.append(struct.pack("<BBBBHHII", wb, hb, 0, 0, 1, 32, nbytes, offset))
        offset += nbytes
    chunks.extend(payloads)
    path.write_bytes(b"".join(chunks))


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    images = [draw_icon(s) for s in SIZES]

    preview = root / "icon-preview-256.png"
    images[-1].save(preview, format="PNG")
    # also 32 for small-size check
    images[SIZES.index(32)].save(root / "icon-preview-32.png", format="PNG")

    out = root / "icon.ico"
    write_ico(out, images)
    print(f"Wrote {out} ({out.stat().st_size} bytes) sizes={list(SIZES)}")
    print(f"Wrote {preview} ({preview.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
