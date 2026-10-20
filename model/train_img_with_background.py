"""
Train a YOLO model on table screenshots pasted over full-screen backgrounds.

Dataset layout expected:
  dataset/images_with_background/*.{jpg,jpeg,png}
  dataset/YOLO_Annotations_full_screen/*.txt
  dataset/classes.txt

Run name: imgWithBackground

Usage:
  python model/train_img_with_background.py --force-prepare --epochs 100 --imgsz 1280 --batch 4 --workers 2
"""

from __future__ import annotations

import argparse
import random
import shutil
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
IMAGES_DIR = ROOT / "dataset" / "images_with_background"
LABELS_DIR = ROOT / "dataset" / "YOLO_Annotations_full_screen"
CLASSES_FILE = ROOT / "dataset" / "classes.txt"
YOLO_DATA_DIR = ROOT / "model" / "yolo_dataset_img_with_background"
DATA_YAML = ROOT / "model" / "data_img_with_background.yaml"
RUN_NAME = "imgWithBackground"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def load_class_names() -> list[str]:
    if not CLASSES_FILE.is_file():
        raise FileNotFoundError(f"Classes file not found: {CLASSES_FILE}")
    names = [line.strip() for line in CLASSES_FILE.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not names:
        raise RuntimeError(f"No class names found in {CLASSES_FILE}")
    return names


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Train YOLO on images placed over full-screen backgrounds")
    parser.add_argument("--model", default="yolov8n.pt", help="Base YOLO weights")
    parser.add_argument("--epochs", type=int, default=100, help="Training epochs")
    parser.add_argument("--imgsz", type=int, default=1280, help="Input image size")
    parser.add_argument("--batch", type=int, default=4, help="Batch size")
    parser.add_argument(
        "--workers",
        type=int,
        default=2,
        help="Dataloader workers (lower if you hit out-of-memory)",
    )
    parser.add_argument("--val-ratio", type=float, default=0.2, help="Validation split ratio")
    parser.add_argument(
        "--seed",
        type=int,
        default=None,
        help="Random seed for train/val split (default: new random seed each prepare)",
    )
    parser.add_argument(
        "--device",
        default=None,
        help="CUDA device id, or 'cpu' (default: auto-detect)",
    )
    parser.add_argument(
        "--project",
        default=str(ROOT / "model" / "runs"),
        help="Ultralytics project directory",
    )
    parser.add_argument("--name", default=RUN_NAME, help="Run name")
    parser.add_argument(
        "--prepare-only",
        action="store_true",
        help="Only build YOLO dataset + data yaml, skip training",
    )
    parser.add_argument(
        "--force-prepare",
        action="store_true",
        help="Rebuild train/val split even if the prepared dataset already exists",
    )
    parser.add_argument(
        "--resume",
        action="store_true",
        help="Resume from model/runs/imgWithBackground/weights/last.pt",
    )
    return parser.parse_args()


def collect_pairs() -> list[tuple[Path, Path]]:
    if not IMAGES_DIR.is_dir():
        raise FileNotFoundError(f"Images directory not found: {IMAGES_DIR}")
    if not LABELS_DIR.is_dir():
        raise FileNotFoundError(f"Labels directory not found: {LABELS_DIR}")

    pairs: list[tuple[Path, Path]] = []
    image_paths = sorted(
        p for p in IMAGES_DIR.iterdir()
        if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS
    )
    for image_path in image_paths:
        label_path = LABELS_DIR / f"{image_path.stem}.txt"
        if label_path.is_file():
            pairs.append((image_path, label_path))
        else:
            print(f"Warning: missing label for {image_path.name}, skipping")

    if not pairs:
        raise RuntimeError(
            f"No image/label pairs found in {IMAGES_DIR} + {LABELS_DIR}"
        )
    return pairs


def _link_or_copy(src: Path, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    if dst.exists() or dst.is_symlink():
        dst.unlink()
    try:
        dst.symlink_to(src.resolve())
    except OSError:
        shutil.copy2(src, dst)


def prepare_dataset(
    val_ratio: float,
    seed: int | None,
    force: bool = False,
) -> Path:
    if DATA_YAML.is_file() and YOLO_DATA_DIR.is_dir() and not force:
        print(f"Reusing existing YOLO dataset at {YOLO_DATA_DIR}")
        return DATA_YAML

    if seed is None:
        seed = random.randint(0, 2**31 - 1)
    print(f"Random train/val split seed: {seed}")

    class_names = load_class_names()
    pairs = collect_pairs()
    rng = random.Random(seed)
    rng.shuffle(pairs)

    val_count = max(1, int(len(pairs) * val_ratio))
    val_pairs = pairs[:val_count]
    train_pairs = pairs[val_count:]
    if not train_pairs:
        raise RuntimeError("Train split is empty; decrease --val-ratio")

    if YOLO_DATA_DIR.exists():
        shutil.rmtree(YOLO_DATA_DIR)

    splits = {"train": train_pairs, "val": val_pairs}
    for split_name, split_pairs in splits.items():
        for image_path, label_path in split_pairs:
            _link_or_copy(image_path, YOLO_DATA_DIR / "images" / split_name / image_path.name)
            _link_or_copy(label_path, YOLO_DATA_DIR / "labels" / split_name / label_path.name)

    data = {
        "path": str(YOLO_DATA_DIR.resolve()),
        "train": "images/train",
        "val": "images/val",
        "nc": len(class_names),
        "names": class_names,
    }
    DATA_YAML.parent.mkdir(parents=True, exist_ok=True)
    DATA_YAML.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")

    print(f"Images: {IMAGES_DIR}")
    print(f"Labels: {LABELS_DIR}")
    print(
        f"Prepared YOLO dataset at {YOLO_DATA_DIR} "
        f"(train={len(train_pairs)}, val={len(val_pairs)}, classes={len(class_names)})"
    )
    print(f"Wrote {DATA_YAML}")
    return DATA_YAML


def resolve_device(device: str | None) -> str:
    if device is not None:
        return device

    import torch

    if torch.cuda.is_available():
        return "0"
    print("CUDA not available; training on CPU (this will be slow).")
    return "cpu"


def train(args: argparse.Namespace) -> None:
    data_yaml = prepare_dataset(args.val_ratio, args.seed, force=args.force_prepare)
    if args.prepare_only:
        return

    from ultralytics import YOLO

    device = resolve_device(args.device)
    last_weights = Path(args.project) / args.name / "weights" / "last.pt"

    if args.resume:
        if not last_weights.is_file():
            raise FileNotFoundError(f"No checkpoint to resume: {last_weights}")
        model = YOLO(str(last_weights))
        results = model.train(resume=True, workers=args.workers, batch=args.batch)
    else:
        model = YOLO(args.model)
        results = model.train(
            data=str(data_yaml),
            epochs=args.epochs,
            imgsz=args.imgsz,
            batch=args.batch,
            workers=args.workers,
            device=device,
            project=args.project,
            name=args.name,
            exist_ok=True,
        )

    best = Path(results.save_dir) / "weights" / "best.pt"
    print(f"Training complete. Best weights: {best}")


if __name__ == "__main__":
    train(parse_args())
