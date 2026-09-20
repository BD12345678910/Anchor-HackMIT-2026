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
    _baseline = 0.2

    def __init__(self, alpha: float = 0.35) -> None:
        if not 0 < alpha <= 1:
            raise ValueError("alpha must be in (0, 1]")
        self._alpha = alpha
        self._smoothed: float | None = None
        self._stuck_windows = 0
        self._gaze_away_windows = 0
        self._agreeing_windows = 0

    def predict(self, features: NormalizedFeatures) -> InferenceResult:
        started = time.perf_counter()
        if features.manual_report:
            return InferenceResult(1.0, 1.0, ("manual_report",), 0)

        reasons: list[str] = []
        linear = -1.35
        linear += (1 - features.app_relevance) * 2.4
        if features.app_relevance < 0.35:
            reasons.append("low_task_relevance")
        if features.app_relevance < 0.2:
            linear += 0.9
            reasons.append("off_task_window")

        # Stillness is not evidence of anything: reading, watching and thinking all look like an
        # untouched keyboard and mouse. Only movement in excess of the work counts.

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

        excess_motion = min(max(features.mouse_distance - 900.0, 0.0) / 900.0, 1.0)
        linear += excess_motion * excess_motion * 0.9
        if excess_motion >= 0.5:
            reasons.append("pointer_wandering")

        gaze_away = features.gaze_available and features.gaze_presence < 0.35
        self._gaze_away_windows = self._gaze_away_windows + 1 if gaze_away else 0
        if features.gaze_available and self._gaze_away_windows >= 3:
            linear += (1 - features.gaze_presence) * 0.8
            reasons.append("gaze_away_sustained")

        if features.key_count and features.app_relevance >= 0.5:
            linear -= min(features.key_count, 20) * 0.035

        raw = 1 / (1 + math.exp(-max(-20.0, min(20.0, linear))))
        previous = self._baseline if self._smoothed is None else self._smoothed
        self._smoothed = (self._alpha * raw) + ((1 - self._alpha) * previous)
        probability = max(0.0, min(1.0, self._smoothed))
        same_side = (raw >= 0.5) == (probability >= 0.5)
        self._agreeing_windows = self._agreeing_windows + 1 if same_side else 0
        confidence = max(
            0.0,
            min(1.0, abs(probability - 0.5) * 2 + 0.12 * min(self._agreeing_windows, 4)),
        )
        if not reasons:
            reasons.append("stable_task_activity")

        latency = max(0, int((time.perf_counter() - started) * 1_000))
        return InferenceResult(probability, confidence, tuple(reasons), latency)
