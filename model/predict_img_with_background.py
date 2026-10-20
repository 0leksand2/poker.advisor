"""
Run the imgWithBackground YOLO model on images in testing/.

Detects tables and cards, draws labeled boxes, and writes annotated
images to testing/output_img_with_background/.

Usage:
  python model/predict_img_with_background.py
  python model/predict_img_with_background.py --conf 0.35 --imgsz 2048
"""

from __future__ import annotations

import argparse
from pathlib import Path

import cv2
from ultralytics import YOLO

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MODEL = ROOT / "model" / "runs" / "imgWithBackground" / "weights" / "best.pt"
FALLBACK_MODEL = ROOT / "model" / "runs" / "imgWithBackground" / "weights" / "last.pt"
DEFAULT_SOURCE = ROOT / "testing"
DEFAULT_OUTPUT = ROOT / "testing" / "output_img_with_background"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Detect tables/cards with imgWithBackground model")
    parser.add_argument(
        "--model",
        default=None,
        help="Path to trained YOLO weights (default: imgWithBackground best.pt)",
    )
    parser.add_argument(
        "--source",
        default=str(DEFAULT_SOURCE),
        help="Folder with input images",
    )
    parser.add_argument(
        "--output",
        default=str(DEFAULT_OUTPUT),
        help="Folder for annotated images",
    )
    parser.add_argument("--conf", type=float, default=0.25, help="Confidence threshold")
    parser.add_argument("--iou", type=float, default=0.45, help="NMS IoU threshold")
    parser.add_argument("--imgsz", type=int, default=2048, help="Inference image size")
    parser.add_argument(
        "--device",
        default=None,
        help="CUDA device id, or 'cpu' (default: auto)",
    )
    return parser.parse_args()


def resolve_model(path: str | None) -> Path:
    if path is not None:
        model_path = Path(path)
        if not model_path.is_file():
            raise FileNotFoundError(f"Model weights not found: {model_path}")
        return model_path
    if DEFAULT_MODEL.is_file():
        return DEFAULT_MODEL
    if FALLBACK_MODEL.is_file():
        return FALLBACK_MODEL
    raise FileNotFoundError(
        f"Model weights not found. Tried:\n  {DEFAULT_MODEL}\n  {FALLBACK_MODEL}"
    )


def resolve_device(device: str | None) -> str:
    if device is not None:
        return device

    import torch

    return "0" if torch.cuda.is_available() else "cpu"


def list_images(source: Path) -> list[Path]:
    if not source.is_dir():
        raise FileNotFoundError(f"Source folder not found: {source}")

    images = sorted(
        p for p in source.iterdir() if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS
    )
    if not images:
        raise RuntimeError(f"No images found in {source}")
    return images


def box_color(name: str) -> tuple[int, int, int]:
    if name == "table" or name.startswith("table_"):
        return (255, 160, 0)
    if name == "active_player":
        return (0, 180, 255)
    if name == "fold_player":
        return (0, 0, 255)  # red
    if name == "dealer":
        return (0, 140, 255)  # orange
    return (0, 220, 0)


def numbered_detections(result) -> list[tuple[int, int, int, int, str, float]]:
    """Return (x1, y1, x2, y2, display_name, conf). Tables become table_1, table_2, ..."""
    boxes = result.boxes
    if boxes is None or len(boxes) == 0:
        return []

    names = result.names
    detections: list[tuple[int, int, int, int, str, float]] = []
    tables: list[tuple[int, int, int, int, float]] = []

    for box in boxes:
        x1, y1, x2, y2 = (int(v) for v in box.xyxy[0].tolist())
        cls_id = int(box.cls[0].item())
        conf = float(box.conf[0].item())
        name = names[cls_id]
        if name == "table":
            tables.append((x1, y1, x2, y2, conf))
        else:
            detections.append((x1, y1, x2, y2, name, conf))

    # Stable order: top-to-bottom, then left-to-right
    tables.sort(key=lambda t: (t[1], t[0]))
    for index, (x1, y1, x2, y2, conf) in enumerate(tables, start=1):
        detections.append((x1, y1, x2, y2, f"table_{index}", conf))

    return detections


def annotate_image(image_bgr, result):
    """Draw detection boxes and class labels onto a copy of the image."""
    annotated = image_bgr.copy()
    detections = numbered_detections(result)

    for x1, y1, x2, y2, name, conf in detections:
        label = f"{name} {conf:.2f}"
        color = box_color(name)

        cv2.rectangle(annotated, (x1, y1), (x2, y2), color, 2)

        font = cv2.FONT_HERSHEY_SIMPLEX
        font_scale = 0.4
        thickness = 1
        (text_w, text_h), baseline = cv2.getTextSize(label, font, font_scale, thickness)
        text_y = max(y1 - 8, text_h + 4)
        cv2.rectangle(
            annotated,
            (x1, text_y - text_h - 4),
            (x1 + text_w + 4, text_y + baseline),
            color,
            -1,
        )
        cv2.putText(
            annotated,
            label,
            (x1 + 2, text_y),
            font,
            font_scale,
            (0, 0, 0),
            thickness,
            cv2.LINE_AA,
        )

    return annotated


def main() -> None:
    args = parse_args()
    model_path = resolve_model(args.model)
    source = Path(args.source)
    output = Path(args.output)

    images = list_images(source)
    output.mkdir(parents=True, exist_ok=True)

    device = resolve_device(args.device)
    model = YOLO(str(model_path))

    print(f"Model:  {model_path}")
    print(f"Source: {source} ({len(images)} images)")
    print(f"Output: {output}")
    print(f"Device: {device}")
    print(f"Imgsz:  {args.imgsz}")

    for image_path in images:
        image = cv2.imread(str(image_path))
        if image is None:
            print(f"Warning: could not read {image_path.name}, skipping")
            continue

        results = model.predict(
            source=image,
            conf=args.conf,
            iou=args.iou,
            imgsz=args.imgsz,
            device=device,
            agnostic_nms=True,
            verbose=False,
        )
        result = results[0]
        annotated = annotate_image(image, result)

        out_path = output / f"{image_path.stem}_detected{image_path.suffix}"
        if out_path.exists():
            out_path.unlink()
        if not cv2.imwrite(str(out_path), annotated):
            print(f"Warning: failed to write {out_path}")
            continue

        detections = numbered_detections(result)
        n = len(detections)
        labels = [f"{name} ({conf:.2f})" for *_, name, conf in detections]
        print(f"{image_path.name} -> {out_path.name} | {n} detection(s): {', '.join(labels) or 'none'}")

    print("Done.")


if __name__ == "__main__":
    main()
