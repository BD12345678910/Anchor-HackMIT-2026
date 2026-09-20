"""Checks the eye-only gaze features against a physical model of the eye rather than against
hand-written landmark fixtures: an eyeball of a known radius rotating behind a palpebral
fissure of a known width, photographed by a camera at an arbitrary distance, position and
head roll. The reference for the horizontal feature is the iris-ratio rule used by classic
webcam trackers (iris centre position between the eye corners, offset from the midpoint)."""

from __future__ import annotations

import math

import numpy as np
import pytest

from anchor_worker.calibration import CalibrationModel, CalibrationSample
from anchor_worker.gaze import LandmarkGazeEstimator

EYE_WIDTH_MM = 30.0
EYEBALL_RADIUS_MM = 12.0
APERTURE_MM = 10.0
EYE_SEPARATION_MM = 63.0
VIEWING_DISTANCE_MM = 600.0
SCREEN_WIDTH_MM = 520.0
SCREEN_HEIGHT_MM = 320.0


def _gaze_angles(screen_x: float, screen_y: float) -> tuple[float, float]:
    return (
        math.atan((screen_x - 0.5) * SCREEN_WIDTH_MM / VIEWING_DISTANCE_MM),
        math.atan((screen_y - 0.5) * SCREEN_HEIGHT_MM / VIEWING_DISTANCE_MM),
    )


def _landmarks(
    screen_x: float = 0.5,
    screen_y: float = 0.5,
    *,
    scale: float = 0.004,
    centre: tuple[float, float] = (0.5, 0.45),
    roll_degrees: float = 0.0,
) -> dict[int, tuple[float, float, float]]:
    """Face landmarks in normalised image coordinates for a head at `centre`, `scale` image
    widths per millimetre, rolled by `roll_degrees`, with both eyes aimed at the screen point."""
    yaw, pitch = _gaze_angles(screen_x, screen_y)
    iris_offset_x = EYEBALL_RADIUS_MM * math.sin(yaw)
    iris_offset_y = EYEBALL_RADIUS_MM * math.sin(pitch)
    aperture = APERTURE_MM * (1.0 - 0.35 * abs(math.sin(pitch)))
    angle = math.radians(roll_degrees)
    cos, sin = math.cos(angle), math.sin(angle)

    def place(eye_centre_mm: float, local: tuple[float, float]) -> tuple[float, float, float]:
        x_mm = eye_centre_mm + local[0]
        y_mm = local[1]
        return (
            centre[0] + scale * (x_mm * cos - y_mm * sin),
            centre[1] + scale * (x_mm * sin + y_mm * cos),
            0.0,
        )

    landmarks: dict[int, tuple[float, float, float]] = {}
    for eye_centre_mm, corners, lids, iris in (
        (-EYE_SEPARATION_MM / 2, (33, 133), (159, 145), range(468, 473)),
        (EYE_SEPARATION_MM / 2, (362, 263), (386, 374), range(473, 478)),
    ):
        landmarks[corners[0]] = place(eye_centre_mm, (-EYE_WIDTH_MM / 2, 0.0))
        landmarks[corners[1]] = place(eye_centre_mm, (EYE_WIDTH_MM / 2, 0.0))
        landmarks[lids[0]] = place(eye_centre_mm, (0.0, -aperture / 2))
        landmarks[lids[1]] = place(eye_centre_mm, (0.0, aperture / 2))
        for index in iris:
            landmarks[index] = place(eye_centre_mm, (iris_offset_x, iris_offset_y))
    return landmarks


def _features(screen_x: float, screen_y: float, **kwargs) -> tuple[float, ...]:
    return LandmarkGazeEstimator().estimate(
        _landmarks(screen_x, screen_y, **kwargs), timestamp_ms=0
    ).features


def test_iris_feature_matches_the_classic_iris_ratio_rule():
    for screen_x in (0.05, 0.3, 0.5, 0.7, 0.95):
        landmarks = _landmarks(screen_x, 0.5)
        outer, inner = landmarks[33][0], landmarks[133][0]
        reference = (landmarks[468][0] - (outer + inner) / 2) / (inner - outer)

        assert _features(screen_x, 0.5)[0] == pytest.approx(reference, abs=1e-9)


def test_features_ignore_where_the_head_is_and_how_big_or_tilted_it_looks():
    reference = _features(0.8, 0.3)

    for moved in (
        _features(0.8, 0.3, centre=(0.2, 0.7)),
        _features(0.8, 0.3, scale=0.0015),
        _features(0.8, 0.3, scale=0.009, centre=(0.7, 0.2)),
        _features(0.8, 0.3, roll_degrees=17.0),
        _features(0.8, 0.3, roll_degrees=-23.0, centre=(0.35, 0.6), scale=0.006),
    ):
        assert moved == pytest.approx(reference, abs=1e-6)


def test_features_move_monotonically_with_where_the_eye_is_aimed():
    horizontal = [_features(x, 0.5)[0] for x in (0.0, 0.25, 0.5, 0.75, 1.0)]
    vertical = [_features(0.5, y)[2] for y in (0.0, 0.25, 0.5, 0.75, 1.0)]

    assert horizontal == sorted(horizontal)
    assert vertical == sorted(vertical)
    assert horizontal[-1] - horizontal[0] > 0.2
    assert vertical[-1] - vertical[0] > 0.1


def test_uncalibrated_estimate_already_tracks_the_side_of_the_screen():
    estimator = LandmarkGazeEstimator()

    left = estimator.estimate(_landmarks(0.1, 0.5), timestamp_ms=0)
    right = estimator.estimate(_landmarks(0.9, 0.5), timestamp_ms=1)
    top = estimator.estimate(_landmarks(0.5, 0.1), timestamp_ms=2)
    bottom = estimator.estimate(_landmarks(0.5, 0.9), timestamp_ms=3)

    assert left.x < 0.45 < 0.55 < right.x
    assert top.y < 0.45 < 0.55 < bottom.y


def test_click_calibration_recovers_the_screen_point_within_two_degrees():
    """Nine clicked targets with per-frame landmark noise, then held-out points the fit never
    saw. Two degrees at 600 mm is about 4% of this screen, the accuracy webcam trackers claim."""
    generator = np.random.default_rng(7)

    def noisy(screen_x: float, screen_y: float) -> tuple[float, ...]:
        landmarks = _landmarks(
            screen_x,
            screen_y,
            centre=(0.5 + generator.normal(0, 0.01), 0.45 + generator.normal(0, 0.01)),
            scale=0.004 * (1 + generator.normal(0, 0.02)),
            roll_degrees=generator.normal(0, 2.0),
        )
        jittered = {
            index: (point[0] + generator.normal(0, 0.0004), point[1] + generator.normal(0, 0.0004), 0.0)
            for index, point in landmarks.items()
        }
        return LandmarkGazeEstimator().estimate(jittered, timestamp_ms=0).features

    grid = [(x, y) for x in (0.1, 0.5, 0.9) for y in (0.1, 0.5, 0.9)]
    samples = [
        CalibrationSample(features=noisy(x, y), target=(x, y))
        for x, y in grid
        for _ in range(6)
    ]
    model = CalibrationModel.fit(samples, "1920x1080@1.0")

    held_out = [(0.25, 0.3), (0.75, 0.3), (0.3, 0.8), (0.8, 0.75), (0.5, 0.25)]
    errors = [
        math.dist(model.apply(noisy(x, y), "1920x1080@1.0"), (x, y)) for x, y in held_out
    ]

    assert model.median_error < 0.04
    assert float(np.median(errors)) < 0.04
    assert max(errors) < 0.08
