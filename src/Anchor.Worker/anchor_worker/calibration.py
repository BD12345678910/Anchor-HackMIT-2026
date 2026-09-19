from __future__ import annotations

from dataclasses import dataclass
from itertools import combinations
from typing import Sequence

import numpy as np


class CalibrationInvalidatedError(RuntimeError):
    pass


@dataclass(frozen=True)
class CalibrationSample:
    features: tuple[float, ...]
    target: tuple[float, float]


@dataclass(frozen=True)
class CalibrationModel:
    coefficients_x: tuple[float, ...]
    coefficients_y: tuple[float, ...]
    display_signature: str
    sample_count: int
    inlier_count: int
    median_error: float

    @classmethod
    def fit(
        cls,
        samples: Sequence[CalibrationSample],
        display_signature: str,
    ) -> "CalibrationModel":
        if len(samples) < 9:
            raise ValueError("at least nine calibration samples are required")
        if not display_signature.strip():
            raise ValueError("display_signature is required")
        feature_count = len(samples[0].features)
        if feature_count == 0 or any(len(item.features) != feature_count for item in samples):
            raise ValueError("calibration samples must have matching feature dimensions")

        design = np.asarray(
            [[1.0, *item.features] for item in samples],
            dtype=np.float64,
        )
        targets = np.asarray([item.target for item in samples], dtype=np.float64)
        best_inliers = np.asarray([], dtype=np.int64)
        best_median = float("inf")
        subset_size = min(max(3, int(np.linalg.matrix_rank(design))), len(samples) - 1)

        for subset in combinations(range(len(samples)), subset_size):
            subset_design = design[list(subset)]
            if np.linalg.matrix_rank(subset_design) < np.linalg.matrix_rank(design):
                continue
            coefficients, *_ = np.linalg.lstsq(subset_design, targets[list(subset)], rcond=None)
            residuals = np.linalg.norm((design @ coefficients) - targets, axis=1)
            inliers = np.flatnonzero(residuals <= 0.04)
            if len(inliers) < subset_size:
                continue
            median = float(np.median(residuals[inliers]))
            if len(inliers) > len(best_inliers) or (
                len(inliers) == len(best_inliers) and median < best_median
            ):
                best_inliers = inliers
                best_median = median

        coefficients, *_ = np.linalg.lstsq(
            design[best_inliers],
            targets[best_inliers],
            rcond=None,
        )
        final_residuals = np.linalg.norm(
            (design[best_inliers] @ coefficients) - targets[best_inliers],
            axis=1,
        )
        return cls(
            tuple(float(value) for value in coefficients[:, 0]),
            tuple(float(value) for value in coefficients[:, 1]),
            display_signature,
            len(samples),
            len(best_inliers),
            float(np.median(final_residuals)),
        )

    def apply(
        self,
        features: tuple[float, ...],
        display_signature: str,
    ) -> tuple[float, float]:
        if display_signature != self.display_signature:
            raise CalibrationInvalidatedError(
                "gaze calibration was invalidated because the display geometry changed"
            )
        vector = np.asarray([1.0, *features], dtype=np.float64)
        if len(vector) != len(self.coefficients_x):
            raise ValueError("feature dimensions do not match this calibration")
        x = float(vector @ np.asarray(self.coefficients_x))
        y = float(vector @ np.asarray(self.coefficients_y))
        return self._clamp(x), self._clamp(y)

    @staticmethod
    def _clamp(value: float) -> float:
        return max(0.0, min(1.0, value))
