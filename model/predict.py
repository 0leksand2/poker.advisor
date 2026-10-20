"""
Run the trained poker-card YOLO model on images in testing/.

For each image, detects cards, draws labeled boxes, and writes annotated
images to testing/output/.

Usage:
  python model/predict.py
  python model/predict.py --conf 0.35 --model model/runs/poker_cards/weights/best.pt
"""

from __future__ import annotations

import argparse
from pathlib import Path

import cv2
from ultralytics import YOLO

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MODEL = ROOT / "model" / "runs" / "poker_cards" / "weights" / "best.pt"
DEFAULT_SOURCE = ROOT / "testing"
DEFAULT_OUTPUT = ROOT / "testing" / "output"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Detect poker cards in testing images")
    parser.add_argument(
        "--model",
        default=str(DEFAULT_MODEL),
        help="Path to trained YOLO weights",
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
    parser.add_argument("--imgsz", type=int, default=640, help="Inference image size")
    parser.add_argument(
        "--device",
        default=None,
        help="CUDA device id, or 'cpu' (default: auto)",
    )
    return parser.parse_args()


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


def annotate_image(image_bgr, result):
    """Draw detection boxes and class labels onto a copy of the image."""
    annotated = image_bgr.copy()
    names = result.names
    boxes = result.boxes

    if boxes is None or len(boxes) == 0:
        return annotated

    for box in boxes:
        x1, y1, x2, y2 = (int(v) for v in box.xyxy[0].tolist())
        cls_id = int(box.cls[0].item())
        conf = float(box.conf[0].item())
        label = f"{names[cls_id]} {conf:.2f}"

        cv2.rectangle(annotated, (x1, y1), (x2, y2), (0, 220, 0), 2)

        font = cv2.FONT_HERSHEY_SIMPLEX
        font_scale = 0.4
        thickness = 1
        (text_w, text_h), baseline = cv2.getTextSize(label, font, font_scale, thickness)
        text_y = max(y1 - 8, text_h + 4)
        cv2.rectangle(
            annotated,
            (x1, text_y - text_h - 4),
            (x1 + text_w + 4, text_y + baseline),
            (0, 220, 0),
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
    model_path = Path(args.model)
    source = Path(args.source)
    output = Path(args.output)

    if not model_path.is_file():
        raise FileNotFoundError(f"Model weights not found: {model_path}")

    images = list_images(source)
    output.mkdir(parents=True, exist_ok=True)

    device = resolve_device(args.device)
    model = YOLO(str(model_path))

    print(f"Model:  {model_path}")
    print(f"Source: {source} ({len(images)} images)")
    print(f"Output: {output}")
    print(f"Device: {device}")

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
        # Replace the file so Windows creation time updates too.
        if out_path.exists():
            out_path.unlink()
        if not cv2.imwrite(str(out_path), annotated):
            print(f"Warning: failed to write {out_path}")
            continue

        n = 0 if result.boxes is None else len(result.boxes)
        labels = []
        if n:
            labels = [
                f"{result.names[int(b.cls[0])]} ({float(b.conf[0]):.2f})"
                for b in result.boxes
            ]
        print(f"{image_path.name} -> {out_path.name} | {n} card(s): {', '.join(labels) or 'none'}")

    print("Done.")


if __name__ == "__main__":
    main()
