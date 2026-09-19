from __future__ import annotations

from dataclasses import dataclass, fields
import math
from typing import Any, Mapping


def _finite_non_negative(value: Any) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return 0.0
    return number if math.isfinite(number) and number >= 0 else 0.0


def _count(value: Any) -> int:
    return int(_finite_non_negative(value))


def _score(value: Any, default: float = 0.5) -> float:
    if value is None:
        return default
    return min(1.0, _finite_non_negative(value))


@dataclass(frozen=True, slots=True)
class NormalizedFeatures:
    key_count: int = 0
    mouse_distance: float = 0.0
    idle_seconds: float = 0.0
    app_relevance: float = 0.5
    gaze_presence: float = 0.5
    app_switch_count: int = 0
    scroll_reversal_count: int = 0
    gaze_available: bool = False
    manual_report: bool = False

    def numeric_values(self) -> tuple[float, ...]:
        return tuple(
            float(getattr(self, item.name))
            for item in fields(self)
            if item.name not in {"gaze_available", "manual_report"}
        )


def normalize_features(values: Mapping[str, Any]) -> NormalizedFeatures:
    return NormalizedFeatures(
        key_count=_count(values.get("key_count")),
        mouse_distance=_finite_non_negative(values.get("mouse_distance")),
        idle_seconds=_finite_non_negative(values.get("idle_seconds")),
        app_relevance=_score(values.get("app_relevance")),
        gaze_presence=_score(values.get("gaze_presence")),
        app_switch_count=_count(values.get("app_switch_count")),
        scroll_reversal_count=_count(values.get("scroll_reversal_count")),
        gaze_available=bool(values.get("gaze_available", False)),
        manual_report=bool(values.get("manual_report", False)),
    )
