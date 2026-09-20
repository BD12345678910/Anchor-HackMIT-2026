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
    features: tuple[float, ...] = ()
    eyes: tuple["EyeRegion", ...] = ()

    @property
    def available(self) -> bool:
        return self.x is not None and self.y is not None


@dataclass(frozen=True)
class EyeRegion:
    """One eye in normalised frame coordinates: its bounding box (with margin) and where the
    iris centre sits, so the preview can show the eye crops the estimate is built from."""

    left: float
    top: float
    width: float
    height: float
    iris_x: float
    iris_y: float
    openness: float


# Gaze is estimated from the eyes only: where each iris sits inside its own eye (measured in
# an eye-aligned frame, relative to the eye's width, so moving or tilting the head does not
# masquerade as a gaze shift), how open each eye is (vertical gaze lowers the lids), and head
# pose scaled down to a minor correction. Face position/size is deliberately not a feature.
FEATURE_NAMES = (
    "left_iris_x",
    "right_iris_x",
    "left_iris_y",
    "right_iris_y",
    "left_openness",
    "right_openness",
    "head_yaw",
    "head_pitch",
)

HEAD_POSE_SCALE = 1.0 / 200.0
"""Degrees of head yaw/pitch per feature unit; small so ridge keeps head pose a minor term."""


class LandmarkGazeEstimator:
    _LEFT_CORNERS = (33, 133)
    _RIGHT_CORNERS = (362, 263)
    _LEFT_LIDS = (159, 145)
    _RIGHT_LIDS = (386, 374)
    _LEFT_IRIS = tuple(range(468, 473))
    _RIGHT_IRIS = tuple(range(473, 478))
    _EYE_MARGIN = 0.6
    _OPEN_EYE_RATIO = 0.18
    """Lid gap over eye width at which an eye counts as fully open; a relaxed eye is ~0.33."""
    # Iris offsets are a fraction of eye width; before calibration replaces the mapping, the
    # horizontal offset is used as-is (corner to corner spans the screen) and the vertical one
    # is scaled by the usual eye width / lid gap ratio.
    _UNCALIBRATED_GAIN_X = 1.0
    _UNCALIBRATED_GAIN_Y = 2.5

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

        left = self._eye(landmarks, self._LEFT_CORNERS, self._LEFT_LIDS, self._LEFT_IRIS)
        right = self._eye(landmarks, self._RIGHT_CORNERS, self._RIGHT_LIDS, self._RIGHT_IRIS)
        # Openness is the lid gap as a fraction of that eye's own width, so a face further from
        # the camera or a lower-resolution sensor does not read as closed eyes.
        openness = min(left[1].openness, right[1].openness)
        confidence = max(0.0, min(0.98, openness / self._OPEN_EYE_RATIO))
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

        left_x, left_y = left[0]
        right_x, right_y = right[0]
        mean_x = (left_x + right_x) / 2.0
        mean_y = (left_y + right_y) / 2.0
        return GazeSample(
            self._clamp(0.5 + mean_x * self._UNCALIBRATED_GAIN_X),
            self._clamp(0.5 + mean_y * self._UNCALIBRATED_GAIN_Y),
            confidence,
            True,
            timestamp_ms,
            yaw,
            pitch,
            roll,
            features=(
                left_x,
                right_x,
                left_y,
                right_y,
                left[1].openness,
                right[1].openness,
                yaw * HEAD_POSE_SCALE,
                pitch * HEAD_POSE_SCALE,
            ),
            eyes=(left[1], right[1]),
        )

    def _eye(
        self,
        landmarks: Landmarks,
        corners: tuple[int, int],
        lids: tuple[int, int],
        iris_indices: Sequence[int],
    ) -> tuple[tuple[float, float], EyeRegion]:
        """Iris offset from the eye centre, in the eye's own frame (x along the corner-to-corner
        axis, y perpendicular to it), as a fraction of the eye width; plus the eye's region."""
        first = landmarks[corners[0]]
        second = landmarks[corners[1]]
        ax = float(second[0]) - float(first[0])
        ay = float(second[1]) - float(first[1])
        width = (ax * ax + ay * ay) ** 0.5
        if width < 1e-6:
            width = 1e-6
            ax, ay = 1.0, 0.0
        ux, uy = ax / width, ay / width
        centre_x = (float(first[0]) + float(second[0])) / 2.0
        centre_y = (float(first[1]) + float(second[1])) / 2.0
        iris = self._center(landmarks, iris_indices)
        dx = iris[0] - centre_x
        dy = iris[1] - centre_y
        along = (dx * ux + dy * uy) / width
        across = (-dx * uy + dy * ux) / width
        openness = self._distance(landmarks, *lids) / width
        margin = width * self._EYE_MARGIN
        # Sized from the corner-to-corner distance rather than its horizontal component, so a
        # tilted head still gets a crop that holds the whole eye.
        region = EyeRegion(
            left=centre_x - (width + margin) / 2.0,
            top=centre_y - margin,
            width=width + margin,
            height=margin * 2.0,
            iris_x=iris[0],
            iris_y=iris[1],
            openness=openness,
        )
        return (along, across), region

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
    def _distance(landmarks: Landmarks, top: int, bottom: int) -> float:
        """Lid gap measured across the eye, so tilting the head does not read as a closing eye."""
        dx = float(landmarks[bottom][0]) - float(landmarks[top][0])
        dy = float(landmarks[bottom][1]) - float(landmarks[top][1])
        return (dx * dx + dy * dy) ** 0.5

    @staticmethod
    def _clamp(value: float) -> float:
        return max(0.0, min(1.0, value))


class GazeSmoother:
    """Median-of-recent points (kills single-frame iris jitter) followed by an exponential
    average whose weight comes from the configured smoothing. Resets when gaze is lost so a
    stale point never bleeds into the next fixation."""

    WINDOW = 5

    def __init__(self, smoothing: float) -> None:
        self._smoothing = smoothing
        self._recent: list[tuple[float, float]] = []
        self._last: Optional[tuple[float, float]] = None

    def update(self, smoothing: float) -> None:
        self._smoothing = smoothing
        self.reset()

    def reset(self) -> None:
        self._recent.clear()
        self._last = None

    def apply(self, x: float, y: float) -> tuple[float, float]:
        self._recent.append((x, y))
        if len(self._recent) > self.WINDOW:
            del self._recent[0]
        xs = sorted(point[0] for point in self._recent)
        ys = sorted(point[1] for point in self._recent)
        middle = len(xs) // 2
        median_x = xs[middle] if len(xs) % 2 else (xs[middle - 1] + xs[middle]) / 2.0
        median_y = ys[middle] if len(ys) % 2 else (ys[middle - 1] + ys[middle]) / 2.0
        if self._last is not None:
            keep = self._smoothing
            median_x = self._last[0] * keep + median_x * (1.0 - keep)
            median_y = self._last[1] * keep + median_y * (1.0 - keep)
        self._last = (median_x, median_y)
        return median_x, median_y


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

