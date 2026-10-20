"""
Long-running imgWithBackground predictor for the WPF overlay.

Reads JPEG frames from stdin (length-prefixed), writes one JSON line per frame.
Does not save screenshots or annotated images.

Protocol:
  request:  uint32 big-endian byte length + JPEG bytes
  response: single JSON line, UTF-8, ending with \\n
            {"ok": true, "detections":[{"name":"...","conf":0.9,"x1":0,"y1":0,"x2":1,"y2":1}], ...}
  stop:     uint32 length 0

Usage:
  python model/predict_img_with_background_stream.py
  python model/predict_img_with_background_stream.py --imgsz 1280 --conf 0.25
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO

from predict_img_with_background import numbered_detections, resolve_device, resolve_model

ROOT = Path(__file__).resolve().parents[1]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Stream imgWithBackground predictions over stdin/stdout")
    parser.add_argument("--model", default=None, help="Path to YOLO weights")
    parser.add_argument("--conf", type=float, default=0.25, help="Confidence threshold")
    parser.add_argument("--iou", type=float, default=0.45, help="NMS IoU threshold")
    parser.add_argument("--imgsz", type=int, default=1280, help="Inference image size")
    parser.add_argument("--device", default=None, help="CUDA device id, or 'cpu'")
    return parser.parse_args()


def read_exact(stream, size: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < size:
        chunk = stream.read(size - len(chunks))
        if not chunk:
            raise EOFError("stdin closed")
        chunks.extend(chunk)
    return bytes(chunks)


def write_json(payload: dict) -> None:
    sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def detections_to_json(detections: list[tuple[int, int, int, int, str, float]]) -> list[dict]:
    return [
        {
            "name": name,
            "conf": round(conf, 4),
            "x1": x1,
            "y1": y1,
            "x2": x2,
            "y2": y2,
        }
        for x1, y1, x2, y2, name, conf in detections
    ]


def main() -> None:
    # Keep stdout for protocol only; logs go to stderr.
    args = parse_args()
    model_path = resolve_model(args.model)
    device = resolve_device(args.device)
    model = YOLO(str(model_path))

    write_json(
        {
            "ok": True,
            "ready": True,
            "model": str(model_path),
            "device": device,
            "imgsz": args.imgsz,
        }
    )

    stdin = sys.stdin.buffer
    while True:
        header = read_exact(stdin, 4)
        (length,) = struct.unpack(">I", header)
        if length == 0:
            write_json({"ok": True, "stopped": True})
            break

        image_bytes = read_exact(stdin, length)
        image = cv2.imdecode(np.frombuffer(image_bytes, dtype=np.uint8), cv2.IMREAD_COLOR)
        if image is None:
            write_json({"ok": False, "error": "failed to decode image"})
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
        detections = numbered_detections(results[0])
        write_json(
            {
                "ok": True,
                "width": int(image.shape[1]),
                "height": int(image.shape[0]),
                "detections": detections_to_json(detections),
            }
        )


if __name__ == "__main__":
    try:
        main()
    except EOFError:
        pass
    except Exception as exc:  # noqa: BLE001 - report to client then exit
        try:
            write_json({"ok": False, "error": str(exc)})
        except Exception:
            pass
        raise
