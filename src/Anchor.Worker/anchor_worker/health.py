from __future__ import annotations

from importlib import import_module
from typing import Callable


def detect_capabilities(
    importer: Callable[[str], object] = import_module,
) -> dict[str, dict[str, object]]:
    try:
        importer("cv2")
        importer("mediapipe")
    except (ImportError, ModuleNotFoundError) as error:
        reason = f"optional vision dependencies unavailable: {type(error).__name__}"
        return {
            "camera": {"available": False, "reason": reason},
            "visual_analysis": {"available": False, "reason": reason},
        }

    return {
        "camera": {"available": True, "reason": ""},
        "visual_analysis": {"available": True, "reason": ""},
    }
