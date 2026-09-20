import pytest

from anchor_worker.gaze import (
    FEATURE_NAMES,
    GazeConfiguration,
    GazeSample,
    GazeTransform,
    LandmarkGazeEstimator,
)


def _face_landmarks(iris_x: float = 0.5, iris_y: float = 0.5):
    landmarks = {
        33: (0.30, 0.42, 0.0),
        133: (0.45, 0.42, 0.0),
        159: (0.375, 0.39, 0.0),
        145: (0.375, 0.45, 0.0),
        362: (0.55, 0.42, 0.0),
        263: (0.70, 0.42, 0.0),
        386: (0.625, 0.39, 0.0),
        374: (0.625, 0.45, 0.0),
    }
    left_x = 0.30 + (0.15 * iris_x)
    right_x = 0.55 + (0.15 * iris_x)
    eye_y = 0.39 + (0.06 * iris_y)
    for index in range(468, 473):
        landmarks[index] = (left_x, eye_y, 0.0)
    for index in range(473, 478):
        landmarks[index] = (right_x, eye_y, 0.0)
    return landmarks


def test_landmark_estimator_returns_unavailable_instead_of_neutral_fake():
    sample = LandmarkGazeEstimator().estimate(None, timestamp_ms=100)

    assert sample.face_present is False
    assert sample.x is None
    assert sample.y is None
    assert sample.confidence == 0.0


def test_landmark_estimator_changes_with_iris_position():
    estimator = LandmarkGazeEstimator()

    left = estimator.estimate(_face_landmarks(iris_x=0.2), timestamp_ms=100)
    right = estimator.estimate(_face_landmarks(iris_x=0.8), timestamp_ms=110)

    assert left.face_present is True
    assert right.face_present is True
    assert left.confidence >= 0.7
    assert right.confidence >= 0.7
    assert left.x == pytest.approx(0.2, abs=0.04)
    assert right.x == pytest.approx(0.8, abs=0.04)
    assert right.x - left.x >= 0.5


def test_landmark_estimator_rejects_closed_or_unreliable_eyes():
    landmarks = _face_landmarks()
    landmarks[145] = (0.375, 0.394, 0.0)
    landmarks[374] = (0.625, 0.394, 0.0)

    sample = LandmarkGazeEstimator().estimate(landmarks, timestamp_ms=100)

    assert sample.face_present is True
    assert sample.x is None
    assert sample.y is None
    assert sample.confidence < 0.35


def test_transform_applies_mirror_rotation_sensitivity_and_offsets():
    transform = GazeTransform(
        GazeConfiguration(
            mirror=True,
            rotation_degrees=90,
            sensitivity=1.2,
            offset_x=0.05,
            offset_y=-0.05,
            smoothing=0.0,
            min_confidence=0.5,
        )
    )
    raw = GazeSample(
        x=0.25,
        y=0.40,
        confidence=0.9,
        face_present=True,
        timestamp_ms=100,
    )

    adjusted = transform.apply(raw)

    assert adjusted.x == pytest.approx(0.67, abs=0.001)
    assert adjusted.y == pytest.approx(0.75, abs=0.001)


def test_transform_smooths_valid_samples_and_rejects_low_confidence():
    transform = GazeTransform(
        GazeConfiguration(mirror=False, smoothing=0.75, min_confidence=0.6)
    )
    first = transform.apply(GazeSample(0.2, 0.3, 0.9, True, 100))
    second = transform.apply(GazeSample(1.0, 0.9, 0.9, True, 110))
    unavailable = transform.apply(GazeSample(0.5, 0.5, 0.2, True, 120))

    assert first.x == pytest.approx(0.2)
    assert first.y == pytest.approx(0.3)
    assert second.x == pytest.approx(0.4)
    assert second.y == pytest.approx(0.45)
    assert unavailable.x is None
    assert unavailable.y is None


def _shift(landmarks, dx: float, dy: float):
    return {index: (x + dx, y + dy, z) for index, (x, y, z) in landmarks.items()}


def test_eye_features_ignore_where_the_face_is_in_the_frame():
    estimator = LandmarkGazeEstimator()

    centred = estimator.estimate(_face_landmarks(iris_x=0.7, iris_y=0.4), timestamp_ms=1)
    moved = estimator.estimate(
        _shift(_face_landmarks(iris_x=0.7, iris_y=0.4), dx=0.2, dy=-0.15), timestamp_ms=2
    )

    assert moved.features == pytest.approx(centred.features, abs=1e-9)
    assert moved.x == pytest.approx(centred.x)
    assert moved.y == pytest.approx(centred.y)
    assert len(centred.features) == len(FEATURE_NAMES)
    assert "face_x" not in FEATURE_NAMES


def test_eye_regions_follow_each_eye_and_mark_the_iris():
    sample = LandmarkGazeEstimator().estimate(_face_landmarks(iris_x=0.8), timestamp_ms=1)

    assert len(sample.eyes) == 2
    left, right = sample.eyes
    assert left.left <= 0.30 and left.left + left.width >= 0.45
    assert right.left <= 0.55 and right.left + right.width >= 0.70
    assert left.top <= left.iris_y <= left.top + left.height
    assert left.iris_x == pytest.approx(0.42, abs=1e-6)
    assert right.iris_x == pytest.approx(0.67, abs=1e-6)
    assert left.openness == pytest.approx(0.4, abs=1e-6)
