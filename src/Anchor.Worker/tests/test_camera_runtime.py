from types import SimpleNamespace

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
    def __init__(self, opened):
        self._opened = opened
        self.released = False

    def isOpened(self):
        return self._opened

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
