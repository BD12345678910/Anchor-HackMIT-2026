from __future__ import annotations

from dataclasses import dataclass, replace
from typing import Mapping, Optional, Sequence


Landmark = Sequence[float]
Landmarks = Mapping[int, Landmark]


@dataclass(frozen=True)
class GazeConfiguration:
    camera_index: int = 0
    mirror: bool = True
    rotation_degrees: int = 0
    offset_x: float = 0.0
    offset_y: float = 0.0
    smoothing: float = 0.65
    sensitivity: float = 1.0
    min_confidence: float = 0.45

    def __post_init__(self) -> None:
        if self.camera_index < 0:
            raise ValueError("camera_index must be non-negative")
        if self.rotation_degrees not in (0, 90, 180, 270):
            raise ValueError("rotation_degrees must be 0, 90, 180, or 270")
        if not 0.0 <= self.smoothing <= 0.95:
            raise ValueError("smoothing must be between 0 and 0.95")
        if not 0.25 <= self.sensitivity <= 3.0:
            raise ValueError("sensitivity must be between 0.25 and 3")
        if not 0.0 <= self.min_confidence <= 1.0:
            raise ValueError("min_confidence must be between 0 and 1")


@dataclass(frozen=True)
class GazeSample:
    x: Optional[float]
    y: Optional[float]
    confidence: float
    face_present: bool
    timestamp_ms: int
    yaw: float = 0.0
    pitch: float = 0.0
    roll: float = 0.0
    preview_jpeg: bytes = b""

    @property
    def available(self) -> bool:
        return self.x is not None and self.y is not None


class LandmarkGazeEstimator:
    _LEFT_CORNERS = (33, 133)
    _RIGHT_CORNERS = (362, 263)
    _LEFT_LIDS = (159, 145)
    _RIGHT_LIDS = (386, 374)
    _LEFT_IRIS = tuple(range(468, 473))
    _RIGHT_IRIS = tuple(range(473, 478))

    def estimate(
        self,
        landmarks: Optional[Landmarks],
        timestamp_ms: int,
        *,
        yaw: float = 0.0,
        pitch: float = 0.0,
        roll: float = 0.0,
    ) -> GazeSample:
        if not landmarks or not self._has_required_landmarks(landmarks):
            return GazeSample(None, None, 0.0, False, timestamp_ms)

        left_open = self._distance_y(landmarks, *self._LEFT_LIDS)
        right_open = self._distance_y(landmarks, *self._RIGHT_LIDS)
        openness = min(left_open, right_open)
        confidence = max(0.0, min(0.98, openness / 0.05))
        if confidence < 0.35:
            return GazeSample(
                None,
                None,
                confidence,
                True,
                timestamp_ms,
                yaw,
                pitch,
                roll,
            )

        left_iris = self._center(landmarks, self._LEFT_IRIS)
        right_iris = self._center(landmarks, self._RIGHT_IRIS)
        left_x = self._ratio(
            left_iris[0],
            float(landmarks[self._LEFT_CORNERS[0]][0]),
            float(landmarks[self._LEFT_CORNERS[1]][0]),
        )
        right_x = self._ratio(
            right_iris[0],
            float(landmarks[self._RIGHT_CORNERS[0]][0]),
            float(landmarks[self._RIGHT_CORNERS[1]][0]),
        )
        left_y = self._ratio(
            left_iris[1],
            float(landmarks[self._LEFT_LIDS[0]][1]),
            float(landmarks[self._LEFT_LIDS[1]][1]),
        )
        right_y = self._ratio(
            right_iris[1],
            float(landmarks[self._RIGHT_LIDS[0]][1]),
            float(landmarks[self._RIGHT_LIDS[1]][1]),
        )
        return GazeSample(
            self._clamp((left_x + right_x) / 2.0),
            self._clamp((left_y + right_y) / 2.0),
            confidence,
            True,
            timestamp_ms,
            yaw,
            pitch,
            roll,
        )

    def _has_required_landmarks(self, landmarks: Landmarks) -> bool:
        required = (
            self._LEFT_CORNERS
            + self._RIGHT_CORNERS
            + self._LEFT_LIDS
            + self._RIGHT_LIDS
            + self._LEFT_IRIS
            + self._RIGHT_IRIS
        )
        return all(index in landmarks and len(landmarks[index]) >= 2 for index in required)

    @staticmethod
    def _center(landmarks: Landmarks, indices: Sequence[int]) -> tuple[float, float]:
        count = len(indices)
        return (
            sum(float(landmarks[index][0]) for index in indices) / count,
            sum(float(landmarks[index][1]) for index in indices) / count,
        )

    @staticmethod
    def _distance_y(landmarks: Landmarks, top: int, bottom: int) -> float:
        return abs(float(landmarks[bottom][1]) - float(landmarks[top][1]))

    @staticmethod
    def _ratio(value: float, start: float, end: float) -> float:
        distance = end - start
        return 0.5 if abs(distance) < 1e-8 else (value - start) / distance

    @staticmethod
    def _clamp(value: float) -> float:
        return max(0.0, min(1.0, value))


class GazeTransform:
    def __init__(self, configuration: GazeConfiguration) -> None:
        self._configuration = configuration
        self._last: Optional[tuple[float, float]] = None

    @property
    def configuration(self) -> GazeConfiguration:
        return self._configuration

    def update(self, configuration: GazeConfiguration) -> None:
        self._configuration = configuration
        self._last = None

    def apply(self, sample: GazeSample) -> GazeSample:
        configuration = self._configuration
        if (
            not sample.available
            or not sample.face_present
            or sample.confidence < configuration.min_confidence
        ):
            self._last = None
            return replace(sample, x=None, y=None)

        x = float(sample.x)
        y = float(sample.y)
        if configuration.mirror:
            x = 1.0 - x
        if configuration.rotation_degrees == 90:
            x, y = 1.0 - y, x
        elif configuration.rotation_degrees == 180:
            x, y = 1.0 - x, 1.0 - y
        elif configuration.rotation_degrees == 270:
            x, y = y, 1.0 - x

        x = 0.5 + ((x - 0.5) * configuration.sensitivity) + configuration.offset_x
        y = 0.5 + ((y - 0.5) * configuration.sensitivity) + configuration.offset_y
        x = self._clamp(x)
        y = self._clamp(y)

        if self._last is not None:
            keep = configuration.smoothing
            x = (self._last[0] * keep) + (x * (1.0 - keep))
            y = (self._last[1] * keep) + (y * (1.0 - keep))
        self._last = (x, y)
        return replace(sample, x=x, y=y)

    @staticmethod
    def _clamp(value: float) -> float:
        return max(0.0, min(1.0, value))

