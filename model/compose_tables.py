"""
Paste 1-6 dataset table images onto full-screen screenshots.

Images do not overlap. YOLO labels from each pasted table are rewritten
into the composite canvas coordinates.

Usage:
  python model/compose_tables.py
  python model/compose_tables.py --count 15000
"""

from __future__ import annotations

import argparse
import math
import random
import shutil
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
TABLES_DIR = ROOT / "dataset" / "Images"
TABLE_LABELS_DIR = ROOT / "dataset" / "YOLO_Annotations"
BACKGROUNDS_DIR = ROOT / "dataset" / "images_full_screen"
OUTPUT_IMAGES_DIR = ROOT / "dataset" / "images_with_background"
OUTPUT_LABELS_DIR = ROOT / "dataset" / "YOLO_Annotations_full_screen"
CLASSES_FILE = ROOT / "dataset" / "classes.txt"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Composite table images onto full-screen screenshots")
    parser.add_argument("--tables", default=str(TABLES_DIR))
    parser.add_argument("--table-labels", default=str(TABLE_LABELS_DIR))
    parser.add_argument("--backgrounds", default=str(BACKGROUNDS_DIR))
    parser.add_argument("--output-images", default=str(OUTPUT_IMAGES_DIR))
    parser.add_argument("--output-labels", default=str(OUTPUT_LABELS_DIR))
    parser.add_argument("--min-tables", type=int, default=1)
    parser.add_argument("--max-tables", type=int, default=6)
    parser.add_argument(
        "--count",
        type=int,
        default=None,
        help="Total composites to write (default: one per background)",
    )
    parser.add_argument("--copies", type=int, default=1, help="Composites per background when --count is not set")
    parser.add_argument("--margin", type=int, default=8, help="Minimum gap between pasted images")
    parser.add_argument("--seed", type=int, default=None)
    return parser.parse_args()


def list_images(folder: Path) -> list[Path]:
    return sorted(
        (p for p in folder.iterdir() if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS),
        key=lambda p: (int(p.stem) if p.stem.isdigit() else 10**9, p.name.lower()),
    )


def load_table_class_id() -> int:
    names = [line.strip() for line in CLASSES_FILE.read_text(encoding="utf-8").splitlines() if line.strip()]
    if "table" not in names:
        raise RuntimeError(f"'table' not found in {CLASSES_FILE}")
    return names.index("table")


def read_labels(path: Path) -> list[tuple[str, float, float, float, float]]:
    if not path.is_file():
        return []
    boxes = []
    for line in path.read_text(encoding="utf-8").splitlines():
        parts = line.split()
        if len(parts) != 5:
            continue
        cls, cx, cy, bw, bh = parts
        boxes.append((cls, float(cx), float(cy), float(bw), float(bh)))
    return boxes


def overlaps(a: tuple[int, int, int, int], b: tuple[int, int, int, int], margin: int) -> bool:
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    return not (
        ax + aw + margin <= bx
        or bx + bw + margin <= ax
        or ay + ah + margin <= by
        or by + bh + margin <= ay
    )


def grid_shape(count: int, canvas_w: int, canvas_h: int) -> tuple[int, int]:
    cols = max(1, math.ceil(math.sqrt(count * canvas_w / max(canvas_h, 1))))
    cols = min(count, cols)
    rows = math.ceil(count / cols)
    return cols, rows


def scale_bounds(count: int, fit_scale: float) -> tuple[float, float]:
    """Fewer tables stay near full size. Crowded layouts shrink to fit."""
    if count <= 1:
        lo, hi = 1.0, min(fit_scale, 1.45)
    elif count == 2:
        lo, hi = 0.92, min(fit_scale, 1.2)
    elif count == 3:
        lo, hi = 0.85, min(fit_scale, 1.05)
    else:
        lo, hi = max(0.55, fit_scale * 0.82), fit_scale
    hi = max(hi, 0.2)
    lo = min(lo, hi)
    return lo, hi


def max_uniform_scale(
    src_w: int,
    src_h: int,
    canvas_w: int,
    canvas_h: int,
    count: int,
    margin: int,
) -> float:
    cols, rows = grid_shape(count, canvas_w, canvas_h)
    cell_w = (canvas_w - margin * (cols + 1)) / cols
    cell_h = (canvas_h - margin * (rows + 1)) / rows
    if cell_w <= 2 or cell_h <= 2:
        return 0.15
    return min(cell_w / src_w, cell_h / src_h)


def place_tables(
    sources: list[tuple[Path, int, int]],
    canvas_w: int,
    canvas_h: int,
    margin: int,
    rng: random.Random,
) -> list[tuple[int, int, int, int]] | None:
    """Return paste rects (x, y, w, h) aligned with sources, or None."""
    count = len(sources)
    placed: list[tuple[int, int, int, int]] = []
    for src_w, src_h in ((w, h) for _, w, h in sources):
        cap = max_uniform_scale(src_w, src_h, canvas_w, canvas_h, count, margin)
        if cap <= 0.05:
            return None
        lo, hi = scale_bounds(count, cap)
        ok = False
        for _ in range(100):
            scale = rng.uniform(lo, hi)
            pw = max(2, int(round(src_w * scale)))
            ph = max(2, int(round(src_h * scale)))
            if pw + margin >= canvas_w or ph + margin >= canvas_h:
                continue
            x = rng.randint(margin, canvas_w - pw - margin)
            y = rng.randint(margin, canvas_h - ph - margin)
            rect = (x, y, pw, ph)
            if all(not overlaps(rect, other, margin) for other in placed):
                placed.append(rect)
                ok = True
                break
        if not ok:
            return None
    return placed


def place_tables_grid(
    sources: list[tuple[Path, int, int]],
    canvas_w: int,
    canvas_h: int,
    margin: int,
    rng: random.Random,
) -> list[tuple[int, int, int, int]]:
    count = len(sources)
    cols, rows = grid_shape(count, canvas_w, canvas_h)
    cell_w = canvas_w / cols
    cell_h = canvas_h / rows
    rects: list[tuple[int, int, int, int]] = []
    for index, (_, src_w, src_h) in enumerate(sources):
        col = index % cols
        row = index // cols
        cap = min(
            (cell_w - margin * 2) / src_w,
            (cell_h - margin * 2) / src_h,
        )
        lo, hi = scale_bounds(count, cap)
        scale = rng.uniform(lo, hi)
        pw = max(2, int(round(src_w * scale)))
        ph = max(2, int(round(src_h * scale)))
        pw = min(pw, max(2, int(cell_w) - margin * 2))
        ph = min(ph, max(2, int(cell_h) - margin * 2))
        x0 = int(col * cell_w) + margin
        y0 = int(row * cell_h) + margin
        x1 = int((col + 1) * cell_w) - margin - pw
        y1 = int((row + 1) * cell_h) - margin - ph
        x = x0 if x1 <= x0 else rng.randint(x0, x1)
        y = y0 if y1 <= y0 else rng.randint(y0, y1)
        rects.append((x, y, pw, ph))
    return rects


def transform_box(
    box: tuple[str, float, float, float, float],
    src_w: int,
    src_h: int,
    paste: tuple[int, int, int, int],
    canvas_w: int,
    canvas_h: int,
) -> str | None:
    cls, cx, cy, bw, bh = box
    px, py, pw, ph = paste
    abs_cx = px + cx * pw
    abs_cy = py + cy * ph
    abs_w = bw * pw
    abs_h = bh * ph
    x1 = max(0.0, abs_cx - abs_w / 2)
    y1 = max(0.0, abs_cy - abs_h / 2)
    x2 = min(float(canvas_w), abs_cx + abs_w / 2)
    y2 = min(float(canvas_h), abs_cy + abs_h / 2)
    if x2 - x1 < 1 or y2 - y1 < 1:
        return None
    ncx = ((x1 + x2) / 2) / canvas_w
    ncy = ((y1 + y2) / 2) / canvas_h
    nbw = (x2 - x1) / canvas_w
    nbh = (y2 - y1) / canvas_h
    return f"{cls} {ncx:.6f} {ncy:.6f} {nbw:.6f} {nbh:.6f}"


def full_table_box(paste: tuple[int, int, int, int], canvas_w: int, canvas_h: int, table_id: int) -> str:
    px, py, pw, ph = paste
    return transform_box((str(table_id), 0.5, 0.5, 1.0, 1.0), 1, 1, paste, canvas_w, canvas_h) or ""


def paste_image(canvas: np.ndarray, image: np.ndarray, rect: tuple[int, int, int, int]) -> None:
    x, y, w, h = rect
    resized = cv2.resize(image, (w, h), interpolation=cv2.INTER_AREA if w < image.shape[1] else cv2.INTER_CUBIC)
    canvas[y : y + h, x : x + w] = resized


def clear_folder(folder: Path) -> None:
    if folder.exists():
        shutil.rmtree(folder)
    folder.mkdir(parents=True, exist_ok=True)


def main() -> None:
    args = parse_args()
    if args.min_tables < 1 or args.max_tables < args.min_tables:
        raise ValueError("--min-tables / --max-tables are invalid")

    tables_dir = Path(args.tables)
    labels_dir = Path(args.table_labels)
    backgrounds_dir = Path(args.backgrounds)
    out_images = Path(args.output_images)
    out_labels = Path(args.output_labels)
    table_id = load_table_class_id()

    backgrounds = list_images(backgrounds_dir)
    if not backgrounds:
        raise RuntimeError(f"No backgrounds in {backgrounds_dir}")

    sources: list[tuple[np.ndarray, list[tuple[str, float, float, float, float]], int, int, str]] = []
    for path in list_images(tables_dir):
        label_path = labels_dir / f"{path.stem}.txt"
        if not label_path.is_file():
            continue
        image = cv2.imread(str(path))
        if image is None:
            continue
        src_h, src_w = image.shape[:2]
        sources.append((image, read_labels(label_path), src_w, src_h, path.stem))
    if not sources:
        raise RuntimeError(f"No labeled table images in {tables_dir}")

    loaded_backgrounds: list[tuple[np.ndarray, str]] = []
    for background_path in backgrounds:
        background = cv2.imread(str(background_path))
        if background is None:
            print(f"Warning: could not read {background_path.name}")
            continue
        loaded_backgrounds.append((background, background_path.stem))
    if not loaded_backgrounds:
        raise RuntimeError(f"No readable backgrounds in {backgrounds_dir}")

    total = args.count if args.count is not None else len(loaded_backgrounds) * args.copies
    if total < 1:
        raise ValueError("--count must be at least 1")

    rng = random.Random(args.seed)
    deck: list[int] = []
    clear_folder(out_images)
    clear_folder(out_labels)

    written = 0
    for index in range(total):
        background, background_stem = loaded_backgrounds[rng.randrange(len(loaded_backgrounds))]
        canvas_h, canvas_w = background.shape[:2]
        table_count = rng.randint(args.min_tables, min(args.max_tables, len(sources)))
        if len(deck) < table_count:
            refill = [i for i in range(len(sources)) if i not in deck]
            rng.shuffle(refill)
            deck.extend(refill)
        chosen = [sources[deck.pop()] for _ in range(table_count)]
        place_sources = [(Path(stem), src_w, src_h) for _, _, src_w, src_h, stem in chosen]
        rects = place_tables(place_sources, canvas_w, canvas_h, args.margin, rng)
        if rects is None:
            rects = place_tables_grid(place_sources, canvas_w, canvas_h, args.margin, rng)

        canvas = background.copy()
        lines: list[str] = []
        for (image, boxes, src_w, src_h, _stem), rect in zip(chosen, rects):
            paste_image(canvas, image, rect)
            has_table = any(int(box[0]) == table_id for box in boxes)
            for box in boxes:
                transformed = transform_box(box, src_w, src_h, rect, canvas_w, canvas_h)
                if transformed:
                    lines.append(transformed)
            if not has_table:
                synthetic = full_table_box(rect, canvas_w, canvas_h, table_id)
                if synthetic:
                    lines.append(synthetic)

        if args.count is None and args.copies == 1:
            stem = background_stem
        elif args.count is None:
            stem = f"{background_stem}_{index + 1}"
        else:
            stem = str(index + 1)
        image_path = out_images / f"{stem}.png"
        label_path = out_labels / f"{stem}.txt"
        cv2.imwrite(str(image_path), canvas, [int(cv2.IMWRITE_PNG_COMPRESSION), 1])
        label_path.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
        written += 1
        if written == 1 or written % 500 == 0 or written == total:
            print(f"{written}/{total} {image_path.name} | {table_count} tables, {len(lines)} boxes")

    print(f"Done. {written} composites in {out_images}")
    print(f"Labels: {out_labels}")


if __name__ == "__main__":
    main()
