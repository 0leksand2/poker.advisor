"""
Normalize dataset/Images:
  - sort into a stable order
  - rename to 1.png, 2.png, ...
  - resize all to one average size (letterbox, no crop)
  - write lossless PNG
  - remap matching YOLO labels to the new names / letterbox

Usage:
  python model/normalize_images.py
"""

from __future__ import annotations

import argparse
import math
import shutil
import tempfile
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_IMAGES = ROOT / "dataset" / "Images"
DEFAULT_LABELS = ROOT / "dataset" / "YOLO_Annotations"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Renumber, resize, and convert images to PNG")
    parser.add_argument("--images", default=str(DEFAULT_IMAGES))
    parser.add_argument("--labels", default=str(DEFAULT_LABELS))
    parser.add_argument("--pad-color", default="0,0,0")
    parser.add_argument("--width", type=int, default=None)
    parser.add_argument("--height", type=int, default=None)
    return parser.parse_args()


def sort_key(path: Path) -> tuple:
    stem = path.stem
    if stem.isdigit():
        return (0, int(stem), path.suffix.lower())
    return (1, stem.lower(), path.suffix.lower())


def list_images(folder: Path) -> list[Path]:
    return sorted(
        (p for p in folder.iterdir() if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS),
        key=sort_key,
    )


def even_size(value: float) -> int:
    rounded = int(round(value))
    if rounded % 2:
        rounded += 1 if value >= rounded else -1
    return max(2, rounded)


def average_size(image_paths: list[Path]) -> tuple[int, int]:
    widths: list[int] = []
    heights: list[int] = []
    for path in image_paths:
        img = cv2.imread(str(path), cv2.IMREAD_UNCHANGED)
        if img is None:
            continue
        h, w = img.shape[:2]
        widths.append(w)
        heights.append(h)
    if not widths:
        raise RuntimeError("No readable images found")
    return even_size(sum(widths) / len(widths)), even_size(sum(heights) / len(heights))


def parse_color(text: str) -> tuple[int, int, int]:
    parts = [int(p.strip()) for p in text.split(",")]
    if len(parts) != 3:
        raise ValueError("--pad-color must be three ints, e.g. 0,0,0")
    return parts[0], parts[1], parts[2]


def to_bgr(image: np.ndarray) -> np.ndarray:
    if image.ndim == 2:
        return cv2.cvtColor(image, cv2.COLOR_GRAY2BGR)
    if image.shape[2] == 4:
        return cv2.cvtColor(image, cv2.COLOR_BGRA2BGR)
    return image


def letterbox(
    image: np.ndarray,
    target_w: int,
    target_h: int,
    pad_color: tuple[int, int, int],
) -> tuple[np.ndarray, float, float, float]:
    h, w = image.shape[:2]
    scale = min(target_w / w, target_h / h)
    new_w = max(1, int(round(w * scale)))
    new_h = max(1, int(round(h * scale)))
    interpolation = cv2.INTER_AREA if scale < 1.0 else cv2.INTER_CUBIC
    resized = cv2.resize(image, (new_w, new_h), interpolation=interpolation)

    canvas = np.full((target_h, target_w, 3), pad_color, dtype=np.uint8)
    pad_x = (target_w - new_w) / 2.0
    pad_y = (target_h - new_h) / 2.0
    x0 = int(math.floor(pad_x))
    y0 = int(math.floor(pad_y))
    canvas[y0 : y0 + new_h, x0 : x0 + new_w] = resized
    return canvas, scale, pad_x, pad_y


def transform_yolo_line(
    line: str,
    src_w: int,
    src_h: int,
    target_w: int,
    target_h: int,
    scale: float,
    pad_x: float,
    pad_y: float,
) -> str:
    parts = line.strip().split()
    if len(parts) != 5:
        return line.strip()

    cls, cx, cy, bw, bh = parts[0], float(parts[1]), float(parts[2]), float(parts[3]), float(parts[4])
    abs_cx = cx * src_w * scale + pad_x
    abs_cy = cy * src_h * scale + pad_y
    abs_w = bw * src_w * scale
    abs_h = bh * src_h * scale
    ncx = min(max(abs_cx / target_w, 0.0), 1.0)
    ncy = min(max(abs_cy / target_h, 0.0), 1.0)
    nbw = min(max(abs_w / target_w, 1e-6), 1.0)
    nbh = min(max(abs_h / target_h, 1e-6), 1.0)
    return f"{cls} {ncx:.6f} {ncy:.6f} {nbw:.6f} {nbh:.6f}"


def main() -> None:
    args = parse_args()
    images_dir = Path(args.images)
    labels_dir = Path(args.labels)
    pad_color = parse_color(args.pad_color)

    if not images_dir.is_dir():
        raise FileNotFoundError(f"Images folder not found: {images_dir}")

    image_paths = list_images(images_dir)
    if not image_paths:
        raise RuntimeError(f"No images found in {images_dir}")

    if args.width is not None and args.height is not None:
        target_w, target_h = even_size(args.width), even_size(args.height)
    else:
        avg_w, avg_h = average_size(image_paths)
        target_w = even_size(args.width) if args.width is not None else avg_w
        target_h = even_size(args.height) if args.height is not None else avg_h

    print(f"Images:  {images_dir} ({len(image_paths)} files)")
    print(f"Target:  {target_w} x {target_h} (letterbox, lossless PNG)")
    print(f"Labels:  {labels_dir}")

    with tempfile.TemporaryDirectory(prefix="poker_normalize_") as tmp:
        tmp_images = Path(tmp) / "images"
        tmp_labels = Path(tmp) / "labels"
        tmp_images.mkdir()
        tmp_labels.mkdir()

        labeled = 0
        for index, image_path in enumerate(image_paths, start=1):
            image = cv2.imread(str(image_path), cv2.IMREAD_UNCHANGED)
            if image is None:
                raise RuntimeError(f"Could not read {image_path}")

            image = to_bgr(image)
            src_h, src_w = image.shape[:2]
            canvas, scale, pad_x, pad_y = letterbox(image, target_w, target_h, pad_color)

            out_name = f"{index}.png"
            out_image = tmp_images / out_name
            ok = cv2.imwrite(str(out_image), canvas, [int(cv2.IMWRITE_PNG_COMPRESSION), 3])
            if not ok:
                raise RuntimeError(f"Failed to write {out_image}")

            src_label = labels_dir / f"{image_path.stem}.txt"
            if src_label.is_file():
                lines = [
                    transform_yolo_line(line, src_w, src_h, target_w, target_h, scale, pad_x, pad_y)
                    for line in src_label.read_text(encoding="utf-8").splitlines()
                    if line.strip()
                ]
                (tmp_labels / f"{index}.txt").write_text(
                    "\n".join(lines) + ("\n" if lines else ""),
                    encoding="utf-8",
                )
                labeled += 1

            print(f"{image_path.name} -> {out_name}: {src_w}x{src_h} -> {target_w}x{target_h}")

        # Replace folders atomically-ish: clear then copy back
        for path in list_images(images_dir):
            path.unlink()
        if labels_dir.is_dir():
            for path in labels_dir.glob("*.txt"):
                if path.name.lower() != "classes.txt":
                    path.unlink()
        else:
            labels_dir.mkdir(parents=True, exist_ok=True)

        for path in sorted(tmp_images.iterdir()):
            shutil.copy2(path, images_dir / path.name)
        for path in sorted(tmp_labels.iterdir()):
            shutil.copy2(path, labels_dir / path.name)

    print(f"Done. {len(image_paths)} images as 1.png..{len(image_paths)}.png, {labeled} labels remapped.")


if __name__ == "__main__":
    main()
