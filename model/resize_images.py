"""
Resize all dataset images to one shared size (average of current sizes).

- Preserves aspect ratio (letterbox / pad, never crop)
- Applies light Gaussian smoothing
- Updates matching YOLO label boxes for the letterbox transform

Usage:
  python model/resize_images.py
  python model/resize_images.py --blur 5
"""

from __future__ import annotations

import argparse
import math
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_IMAGES = ROOT / "dataset" / "Images"
DEFAULT_LABELS = ROOT / "dataset" / "YOLO_Annotations"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Resize images to average size with padding and smoothing"
    )
    parser.add_argument("--images", default=str(DEFAULT_IMAGES), help="Input images folder")
    parser.add_argument("--labels", default=str(DEFAULT_LABELS), help="YOLO labels folder")
    parser.add_argument(
        "--output-images",
        default=None,
        help="Output images folder (default: overwrite --images)",
    )
    parser.add_argument(
        "--output-labels",
        default=None,
        help="Output labels folder (default: overwrite --labels)",
    )
    parser.add_argument(
        "--pad-color",
        default="0,0,0",
        help="BGR pad color as R,G,B or B,G,R ints (default: black 0,0,0)",
    )
    parser.add_argument(
        "--blur",
        type=int,
        default=3,
        help="Gaussian blur kernel size (odd int, 0 to disable)",
    )
    parser.add_argument(
        "--width",
        type=int,
        default=None,
        help="Force target width (default: average image width)",
    )
    parser.add_argument(
        "--height",
        type=int,
        default=None,
        help="Force target height (default: average image height)",
    )
    return parser.parse_args()


def list_images(folder: Path) -> list[Path]:
    return sorted(
        p for p in folder.iterdir()
        if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS
    )


def even_size(value: float) -> int:
    """Round to nearest even integer (>= 2) for nicer neural-net dims."""
    rounded = int(round(value))
    if rounded % 2:
        rounded += 1 if value >= rounded else -1
    return max(2, rounded)


def average_size(image_paths: list[Path]) -> tuple[int, int]:
    widths: list[int] = []
    heights: list[int] = []
    for path in image_paths:
        img = cv2.imread(str(path))
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


def letterbox(
    image: np.ndarray,
    target_w: int,
    target_h: int,
    pad_color: tuple[int, int, int],
) -> tuple[np.ndarray, float, float, float]:
    """
    Resize with aspect ratio preserved, then pad to target size.

    Returns: (canvas, scale, pad_x, pad_y)
    """
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


def smooth(image: np.ndarray, blur: int) -> np.ndarray:
    if blur <= 0:
        return image
    k = blur if blur % 2 == 1 else blur + 1
    return cv2.GaussianBlur(image, (k, k), 0)


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

    # Absolute coords on source image
    abs_cx = cx * src_w
    abs_cy = cy * src_h
    abs_w = bw * src_w
    abs_h = bh * src_h

    # After scale + pad
    abs_cx = abs_cx * scale + pad_x
    abs_cy = abs_cy * scale + pad_y
    abs_w *= scale
    abs_h *= scale

    # Normalize to target canvas; clamp to valid YOLO range
    ncx = min(max(abs_cx / target_w, 0.0), 1.0)
    ncy = min(max(abs_cy / target_h, 0.0), 1.0)
    nbw = min(max(abs_w / target_w, 1e-6), 1.0)
    nbh = min(max(abs_h / target_h, 1e-6), 1.0)
    return f"{cls} {ncx:.6f} {ncy:.6f} {nbw:.6f} {nbh:.6f}"


def process_label(
    label_path: Path,
    out_label_path: Path,
    src_w: int,
    src_h: int,
    target_w: int,
    target_h: int,
    scale: float,
    pad_x: float,
    pad_y: float,
) -> None:
    if not label_path.is_file():
        return

    lines = label_path.read_text(encoding="utf-8").splitlines()
    transformed = [
        transform_yolo_line(line, src_w, src_h, target_w, target_h, scale, pad_x, pad_y)
        for line in lines
        if line.strip()
    ]
    out_label_path.parent.mkdir(parents=True, exist_ok=True)
    out_label_path.write_text("\n".join(transformed) + ("\n" if transformed else ""), encoding="utf-8")


def main() -> None:
    args = parse_args()
    images_dir = Path(args.images)
    labels_dir = Path(args.labels)
    out_images = Path(args.output_images) if args.output_images else images_dir
    out_labels = Path(args.output_labels) if args.output_labels else labels_dir
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

    out_images.mkdir(parents=True, exist_ok=True)
    out_labels.mkdir(parents=True, exist_ok=True)

    print(f"Images:  {images_dir} ({len(image_paths)} files)")
    print(f"Target:  {target_w} x {target_h} (no crop, letterbox pad)")
    print(f"Blur:    {args.blur if args.blur > 0 else 'disabled'}")
    print(f"Output:  {out_images}")
    print(f"Labels:  {out_labels}")

    for image_path in image_paths:
        image = cv2.imread(str(image_path))
        if image is None:
            print(f"Warning: could not read {image_path.name}, skipping")
            continue

        src_h, src_w = image.shape[:2]
        canvas, scale, pad_x, pad_y = letterbox(image, target_w, target_h, pad_color)
        canvas = smooth(canvas, args.blur)

        out_image_path = out_images / image_path.name
        # Keep original extension; encode quality for jpeg
        if out_image_path.suffix.lower() in {".jpg", ".jpeg"}:
            cv2.imwrite(str(out_image_path), canvas, [int(cv2.IMWRITE_JPEG_QUALITY), 95])
        else:
            cv2.imwrite(str(out_image_path), canvas)

        process_label(
            labels_dir / f"{image_path.stem}.txt",
            out_labels / f"{image_path.stem}.txt",
            src_w,
            src_h,
            target_w,
            target_h,
            scale,
            pad_x,
            pad_y,
        )
        print(f"{image_path.name}: {src_w}x{src_h} -> {target_w}x{target_h}")

    print("Done.")


if __name__ == "__main__":
    main()
