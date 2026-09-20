from __future__ import annotations

import argparse
import asyncio
import json

from .gaze_controller import GazeController
from .server import PROTOCOL_VERSION, serve, worker_capabilities


def main() -> None:
    parser = argparse.ArgumentParser(description="Anchor local inference worker")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--token")
    parser.add_argument("--health-json", action="store_true")
    parser.add_argument("--camera-list-json", action="store_true")
    parser.add_argument("--camera-diagnose-json", action="store_true")
    arguments = parser.parse_args()
    if arguments.camera_diagnose_json:
        try:
            import cv2

            from .camera import CameraDeviceProbe

            print(json.dumps({
                "status": "ok",
                "opencv": cv2.__version__,
                "probes": CameraDeviceProbe.diagnose(cv2),
            }, separators=(",", ":")))
        except Exception as error:
            print(json.dumps({"status": "degraded", "error": str(error), "probes": []}, separators=(",", ":")))
        return
    if arguments.health_json:
        print(json.dumps({
            "status": "ok",
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": worker_capabilities(),
        }, separators=(",", ":")))
        return
    if arguments.camera_list_json:
        try:
            devices = GazeController().list_cameras()
            print(json.dumps({
                "status": "ok",
                "devices": [{"index": item.index, "name": item.name} for item in devices],
            }, separators=(",", ":")))
        except Exception as error:
            print(json.dumps({"status": "degraded", "error": str(error), "devices": []}, separators=(",", ":")))
        return
    if not arguments.token:
        parser.error("--token is required when starting the worker server")
    asyncio.run(serve(arguments.port, arguments.token))


if __name__ == "__main__":
    main()
