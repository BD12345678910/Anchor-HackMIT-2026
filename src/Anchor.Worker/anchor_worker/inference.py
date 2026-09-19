from __future__ import annotations

from dataclasses import dataclass
import math
import time

from .features import NormalizedFeatures


@dataclass(frozen=True, slots=True)
class InferenceResult:
    distraction_probability: float
    confidence: float
    reason_codes: tuple[str, ...]
    latency_ms: int


class AttentionInference:
    def __init__(self, alpha: float = 0.35) -> None:
        if not 0 < alpha <= 1:
            raise ValueError("alpha must be in (0, 1]")
        self._alpha = alpha
        self._smoothed: float | None = None
        self._stuck_windows = 0

    def predict(self, features: NormalizedFeatures) -> InferenceResult:
        started = time.perf_counter()
        if features.manual_report:
            return InferenceResult(1.0, 1.0, ("manual_report",), 0)

        reasons: list[str] = []
        linear = -1.35
        linear += (1 - features.app_relevance) * 2.4
        if features.app_relevance < 0.35:
            reasons.append("low_task_relevance")

        linear += min(features.idle_seconds, 30) * 0.055
        if features.idle_seconds >= 5:
            reasons.append("idle_pause")

        linear += min(features.app_switch_count, 8) * 0.24
        if features.app_switch_count >= 3:
            reasons.append("rapid_switching")

        linear += min(features.scroll_reversal_count, 10) * 0.13
        if features.scroll_reversal_count >= 4:
            reasons.append("scroll_loop")
        appears_stuck = (
            features.app_relevance >= 0.55
            and features.scroll_reversal_count >= 8
            and features.idle_seconds < 8
        )
        self._stuck_windows = self._stuck_windows + 1 if appears_stuck else 0
        if self._stuck_windows >= 2:
            reasons.append("stuck_phrase")

        linear += min(features.mouse_distance, 1_000) * 0.0012
        if features.mouse_distance >= 250:
            reasons.append("pointer_wandering")

        if features.gaze_available:
            linear += (1 - features.gaze_presence) * 0.8
            if features.gaze_presence < 0.35:
                reasons.append("gaze_absent")

        if features.key_count and features.app_relevance >= 0.5:
            linear -= min(features.key_count, 20) * 0.035

        raw = 1 / (1 + math.exp(-max(-20.0, min(20.0, linear))))
        self._smoothed = (
            raw
            if self._smoothed is None
            else (self._alpha * raw) + ((1 - self._alpha) * self._smoothed)
        )
        probability = max(0.0, min(1.0, self._smoothed))
        confidence = max(0.0, min(1.0, abs(probability - 0.5) * 2))
        if not reasons:
            reasons.append("stable_task_activity")

        latency = max(0, int((time.perf_counter() - started) * 1_000))
        return InferenceResult(probability, confidence, tuple(reasons), latency)
