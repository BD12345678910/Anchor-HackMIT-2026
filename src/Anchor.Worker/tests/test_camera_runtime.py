from types import SimpleNamespace

import numpy as np

from anchor_worker.camera import (
    CameraDeviceProbe,
    CameraGazeTracker,
    GazeSettingsStore,
    MediaPipeFaceMeshDetector,
)
from anchor_worker.gaze import GazeConfiguration

from test_gaze import _face_landmarks


class _Detector:
    def __init__(self, landmarks):
        self._landmarks = landmarks

    def detect(self, frame):
        del frame
        return self._landmarks, 0.0, 0.0, 0.0

    def close(self):
        return None


class _Capture:
    def __init__(self, opened, frames=True):
        self._opened = opened
        self._frames = frames
        self.released = False

    def isOpened(self):
        return self._opened

    def read(self):
        if not self._frames:
            return False, None
        return True, np.zeros((2, 2, 3), dtype=np.uint8)

    def get(self, prop):
        del prop
        return 640

    def release(self):
        self.released = True


class _Cv2Probe:
    CAP_DSHOW = 700
    CAP_ANY = 0

    def __init__(self):
        self.captures = []

    def VideoCapture(self, index, backend):
        capture = _Capture(opened=backend == self.CAP_DSHOW and index in (0, 2))
        self.captures.append(capture)
        return capture


def test_camera_probe_returns_only_openable_devices_and_releases_handles():
    cv2 = _Cv2Probe()

    devices = CameraDeviceProbe.list_devices(cv2, max_index=4)

    assert [(item.index, item.name) for item in devices] == [
        (0, "Camera 1"),
        (2, "Camera 3"),
    ]
    assert all(capture.released for capture in cv2.captures)


def test_camera_probe_falls_back_to_media_foundation_when_directshow_fails():
    class _MediaFoundationCv2:
        CAP_DSHOW = 700
        CAP_MSMF = 1400
        CAP_ANY = 0

        def __init__(self):
            self.backends = []

        def VideoCapture(self, index, backend):
            self.backends.append(backend)
            return _Capture(opened=index == 0 and backend == self.CAP_MSMF)

    cv2 = _MediaFoundationCv2()

    devices = CameraDeviceProbe.list_devices(cv2, max_index=1)

    assert [(item.index, item.name) for item in devices] == [(0, "Camera 1")]
    assert cv2.backends == [cv2.CAP_DSHOW, cv2.CAP_MSMF]


def test_camera_probe_skips_devices_that_open_but_never_deliver_frames():
    class _StuckCv2:
        CAP_DSHOW = 700
        CAP_MSMF = 1400
        CAP_ANY = 0
        CAP_PROP_FRAME_WIDTH = 3
        CAP_PROP_FRAME_HEIGHT = 4

        def VideoCapture(self, index, backend):
            del backend
            return _Capture(opened=index == 0, frames=False)

    cv2 = _StuckCv2()

    assert CameraDeviceProbe.list_devices(cv2, max_index=1) == []
    report = CameraDeviceProbe.diagnose(cv2, max_index=1)
    assert [entry["backend"] for entry in report] == ["CAP_DSHOW", "CAP_MSMF", "CAP_ANY"]
    assert all(entry["opened"] and not entry["frames"] for entry in report)
    assert report[0]["error"] == "opened but no frames"


def test_gaze_settings_round_trip_all_user_adjustable_parameters(tmp_path):
    store = GazeSettingsStore(tmp_path / "gaze.json")
    expected = GazeConfiguration(
        camera_index=2,
        mirror=False,
        rotation_degrees=270,
        offset_x=0.12,
        offset_y=-0.08,
        smoothing=0.4,
        sensitivity=1.35,
        min_confidence=0.72,
    )

    store.save(expected)

    assert store.load() == expected


def test_tracker_processes_real_landmark_output_without_neutral_fallback():
    tracker = CameraGazeTracker(
        GazeConfiguration(mirror=False, smoothing=0.0),
        detector=_Detector(_face_landmarks(iris_x=0.75, iris_y=0.25)),
    )

    sample = tracker.process_frame(object(), timestamp_ms=1234)

    assert sample.available is True
    assert sample.face_present is True
    assert 0.70 <= sample.x <= 0.80
    assert 0.20 <= sample.y <= 0.30


def test_tracker_reports_missing_face_as_unavailable():
    tracker = CameraGazeTracker(
        GazeConfiguration(mirror=False),
        detector=_Detector(None),
    )

    sample = tracker.process_frame(object(), timestamp_ms=1234)

    assert sample.face_present is False
    assert sample.x is None
    assert sample.y is None


class _SequenceDetector:
    def __init__(self, landmark_sets):
        self._sets = list(landmark_sets)

    def detect(self, frame):
        del frame
        landmarks = self._sets.pop(0) if len(self._sets) > 1 else self._sets[0]
        return landmarks, 0.0, 0.0, 0.0

    def close(self):
        return None


def test_fixation_sampling_rejects_moving_eyes_and_accepts_steady_ones():
    moving = CameraGazeTracker(
        GazeConfiguration(mirror=False, smoothing=0.0),
        detector=_SequenceDetector(
            [_face_landmarks(iris_x=x, iris_y=0.5) for x in (0.2, 0.5, 0.8, 0.3)]
        ),
    )
    for step in range(4):
        moving.process_frame(object(), timestamp_ms=step * 50)
    try:
        moving.sample_fixation()
    except RuntimeError as error:
        assert "moving" in str(error)
    else:
        raise AssertionError("moving eyes must not be accepted as a calibration fixation")

    steady = CameraGazeTracker(
        GazeConfiguration(mirror=False, smoothing=0.0),
        detector=_Detector(_face_landmarks(iris_x=0.75, iris_y=0.25)),
    )
    steady.process_frame(object(), timestamp_ms=5000)
    try:
        steady.sample_fixation()
    except RuntimeError as error:
        assert "no steady gaze" in str(error)
    for step in range(1, 4):
        steady.process_frame(object(), timestamp_ms=5000 + step * 50)

    features = steady.sample_fixation()

    assert len(features) == 8
    assert 0.20 <= features[0] <= 0.30


def test_calibrated_tracker_maps_raw_features_through_the_fitted_model():
    from anchor_worker.calibration import CalibrationModel, CalibrationSample

    tracker = CameraGazeTracker(
        GazeConfiguration(mirror=True, offset_x=0.3, sensitivity=2.0, smoothing=0.0),
        detector=_Detector(_face_landmarks(iris_x=0.75, iris_y=0.25)),
    )
    raw = tracker.process_frame(object(), timestamp_ms=1)
    features = raw.features
    # Training points: the observed features map to the screen centre; shifted iris ratios
    # map elsewhere, so the fit must reproduce the centre for the observed vector.
    samples = [
        CalibrationSample(
            (features[0] + 0.1 * i, features[1] + 0.1 * i, *features[2:]),
            (0.5 + 0.08 * i, 0.5 - 0.05 * i),
        )
        for i in range(-3, 4)
    ]
    model = CalibrationModel.fit(samples, "1280x720@100")
    tracker.set_calibration(model, "1280x720@100")

    calibrated = tracker.process_frame(object(), timestamp_ms=2)

    assert calibrated.available is True
    assert abs(calibrated.x - 0.5) < 0.05
    assert abs(calibrated.y - 0.5) < 0.05
    assert tracker.is_calibrated is True


def test_mediapipe_detector_supports_current_tasks_api_without_legacy_solutions(tmp_path):
    model = tmp_path / "face_landmarker.task"
    model.write_bytes(b"model")
    created = {}

    class _Landmarker:
        @classmethod
        def create_from_options(cls, options):
            created["options"] = options
            return cls()

        def close(self):
            created["closed"] = True

    class _Options:
        def __init__(self, **kwargs):
            self.values = kwargs

    fake_mediapipe = SimpleNamespace(
        tasks=SimpleNamespace(
            BaseOptions=lambda **kwargs: kwargs,
            vision=SimpleNamespace(
                FaceLandmarker=_Landmarker,
                FaceLandmarkerOptions=_Options,
                RunningMode=SimpleNamespace(VIDEO="video"),
            ),
        )
    )

    detector = MediaPipeFaceMeshDetector(
        cv2_module=SimpleNamespace(),
        mediapipe_module=fake_mediapipe,
        model_path=model,
    )
    detector.close()

    assert created["options"].values["running_mode"] == "video"
    assert created["closed"] is True
