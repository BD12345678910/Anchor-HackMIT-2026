from __future__ import annotations

from dataclasses import dataclass, field
from typing import Sequence

import numpy as np


class CalibrationInvalidatedError(RuntimeError):
    pass


@dataclass(frozen=True)
class CalibrationSample:
    features: tuple[float, ...]
    target: tuple[float, float]


@dataclass(frozen=True)
class TargetError:
    target: tuple[float, float]
    predicted: tuple[float, float]
    error: float


def expand_features(features: Sequence[float]) -> np.ndarray:
    """Bias + raw features + pairwise products of the first four (the eye ratios), so head pose
    and iris offset can interact instead of being forced into a single plane."""
    raw = np.asarray(features, dtype=np.float64)
    eyes = raw[: min(4, len(raw))]
    cross = [eyes[i] * eyes[j] for i in range(len(eyes)) for j in range(i, len(eyes))]
    return np.concatenate(([1.0], raw, cross))


@dataclass(frozen=True)
class CalibrationModel:
    coefficients_x: tuple[float, ...]
    coefficients_y: tuple[float, ...]
    display_signature: str
    sample_count: int
    inlier_count: int
    median_error: float
    mean_error: float = 0.0
    max_error: float = 0.0
    target_count: int = 0
    target_errors: tuple[TargetError, ...] = field(default=())

    MIN_TARGETS = 5
    RIDGE = 1e-3
    OUTLIER_FACTOR = 3.0

    @classmethod
    def fit(
        cls,
        samples: Sequence[CalibrationSample],
        display_signature: str,
    ) -> "CalibrationModel":
        if not display_signature.strip():
            raise ValueError("display_signature is required")
        targets_seen = {tuple(round(v, 3) for v in item.target) for item in samples}
        if len(targets_seen) < cls.MIN_TARGETS:
            raise ValueError(
                f"at least {cls.MIN_TARGETS} distinct calibration targets are required "
                f"(have {len(targets_seen)})"
            )
        feature_count = len(samples[0].features)
        if feature_count == 0 or any(len(item.features) != feature_count for item in samples):
            raise ValueError("calibration samples must have matching feature dimensions")

        design = np.asarray([expand_features(item.features) for item in samples])
        targets = np.asarray([item.target for item in samples], dtype=np.float64)

        # Robust fit: ridge on everything, drop samples whose residual is far beyond the
        # typical one (a click made while not looking at the target), refit on the rest.
        inliers = np.arange(len(samples))
        coefficients = cls._ridge(design, targets)
        for _ in range(3):
            residuals = np.linalg.norm(design @ coefficients - targets, axis=1)
            scale = max(float(np.median(residuals[inliers])), 0.01)
            keep = np.flatnonzero(residuals <= cls.OUTLIER_FACTOR * scale)
            if len(keep) < max(cls.MIN_TARGETS, len(samples) // 2) or len(keep) == len(inliers):
                break
            inliers = keep
            coefficients = cls._ridge(design[inliers], targets[inliers])

        residuals = np.linalg.norm(design @ coefficients - targets, axis=1)
        inlier_residuals = residuals[inliers]
        predicted = np.clip(design @ coefficients, 0.0, 1.0)
        per_target: dict[tuple[float, float], list[int]] = {}
        for index, item in enumerate(samples):
            per_target.setdefault(tuple(round(v, 3) for v in item.target), []).append(index)
        target_errors = tuple(
            TargetError(
                target,
                (float(np.mean(predicted[rows, 0])), float(np.mean(predicted[rows, 1]))),
                float(np.mean(residuals[rows])),
            )
            for target, rows in per_target.items()
        )
        return cls(
            tuple(float(value) for value in coefficients[:, 0]),
            tuple(float(value) for value in coefficients[:, 1]),
            display_signature,
            len(samples),
            int(len(inliers)),
            float(np.median(inlier_residuals)),
            float(np.mean(inlier_residuals)),
            float(np.max(inlier_residuals)),
            len(per_target),
            target_errors,
        )

    @classmethod
    def _ridge(cls, design: np.ndarray, targets: np.ndarray) -> np.ndarray:
        columns = design.shape[1]
        penalty = np.eye(columns) * cls.RIDGE
        penalty[0, 0] = 0.0
        return np.linalg.solve(design.T @ design + penalty, design.T @ targets)

    def apply(
        self,
        features: Sequence[float],
        display_signature: str,
    ) -> tuple[float, float]:
        if display_signature != self.display_signature:
            raise CalibrationInvalidatedError(
                "gaze calibration was invalidated because the display geometry changed"
            )
        vector = expand_features(features)
        if len(vector) != len(self.coefficients_x):
            raise ValueError("feature dimensions do not match this calibration")
        x = float(vector @ np.asarray(self.coefficients_x))
        y = float(vector @ np.asarray(self.coefficients_y))
        return self._clamp(x), self._clamp(y)

    def to_dict(self) -> dict[str, object]:
        return {
            "coefficients_x": list(self.coefficients_x),
            "coefficients_y": list(self.coefficients_y),
            "display_signature": self.display_signature,
            "sample_count": self.sample_count,
            "inlier_count": self.inlier_count,
            "median_error": self.median_error,
            "mean_error": self.mean_error,
            "max_error": self.max_error,
            "target_count": self.target_count,
        }

    @classmethod
    def from_dict(cls, payload: dict[str, object]) -> "CalibrationModel":
        return cls(
            tuple(float(v) for v in payload["coefficients_x"]),  # type: ignore[index]
            tuple(float(v) for v in payload["coefficients_y"]),  # type: ignore[index]
            str(payload["display_signature"]),
            int(payload.get("sample_count", 0)),  # type: ignore[arg-type]
            int(payload.get("inlier_count", 0)),  # type: ignore[arg-type]
            float(payload.get("median_error", 0.0)),  # type: ignore[arg-type]
            float(payload.get("mean_error", 0.0)),  # type: ignore[arg-type]
            float(payload.get("max_error", 0.0)),  # type: ignore[arg-type]
            int(payload.get("target_count", 0)),  # type: ignore[arg-type]
        )

    @staticmethod
    def _clamp(value: float) -> float:
        return max(0.0, min(1.0, value))
