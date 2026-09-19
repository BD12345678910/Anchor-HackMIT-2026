from __future__ import annotations

from dataclasses import asdict, dataclass, replace
import json
from pathlib import Path
from threading import Event, Lock, Thread
import time
from typing import Any, Optional, Protocol

import numpy as np

from .calibration import CalibrationInvalidatedError, CalibrationModel
from .gaze import (
    GazeConfiguration,
    GazeSample,
    GazeTransform,
    LandmarkGazeEstimator,
    Landmarks,
)


@dataclass(frozen=True)
class CameraDevice:
    index: int
    name: str


class FaceLandmarkDetector(Protocol):
    def detect(
        self, frame: Any
    ) -> tuple[Optional[Landmarks], float, float, float]: ...

    def close(self) -> None: ...


class CameraDeviceProbe:
    @staticmethod
    def list_devices(cv2_module: Any, max_index: int = 5) -> list[CameraDevice]:
        devices: list[CameraDevice] = []
        for index in range(max(0, max_index)):
            capture = CameraDeviceProbe.open_device(cv2_module, index)
            if capture is not None:
                devices.append(CameraDevice(index, f"Camera {index + 1}"))
                capture.release()
        return devices

    @staticmethod
    def open_device(cv2_module: Any, index: int) -> Any | None:
        backends: list[int] = []
        for name in ("CAP_DSHOW", "CAP_MSMF", "CAP_ANY"):
            backend = int(getattr(cv2_module, name, 0))
            if backend in backends:
                continue
            backends.append(backend)
            capture = cv2_module.VideoCapture(index, backend)
            if capture.isOpened():
                return capture
            capture.release()
        return None


class GazeSettingsStore:
    def __init__(self, path: Path | str) -> None:
        self._path = Path(path)

    def load(self) -> GazeConfiguration:
        if not self._path.exists():
            return GazeConfiguration()
        payload = json.loads(self._path.read_text(encoding="utf-8"))
        return GazeConfiguration(**payload)

    def save(self, configuration: GazeConfiguration) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self._path.with_suffix(self._path.suffix + ".tmp")
        temporary.write_text(
            json.dumps(asdict(configuration), separators=(",", ":")),
            encoding="utf-8",
        )
        temporary.replace(self._path)


class MediaPipeFaceMeshDetector:
    def __init__(
        self,
        cv2_module: Any = None,
        mediapipe_module: Any = None,
        model_path: Path | str | None = None,
    ) -> None:
        if cv2_module is None:
            import cv2 as cv2_module
        if mediapipe_module is None:
            import mediapipe as mediapipe_module
        self._cv2 = cv2_module
        self._mediapipe = mediapipe_module
        self._last_timestamp_ms = 0
        solutions = getattr(mediapipe_module, "solutions", None)
        if solutions is not None:
            self._mode = "legacy"
            self._landmarker = solutions.face_mesh.FaceMesh(
                static_image_mode=False,
                max_num_faces=1,
                refine_landmarks=True,
                min_detection_confidence=0.55,
                min_tracking_confidence=0.55,
            )
            return

        self._mode = "tasks"
        resolved_model = Path(model_path) if model_path is not None else (
            Path(__file__).resolve().parent / "models" / "face_landmarker.task"
        )
        if not resolved_model.is_file():
            raise RuntimeError(f"MediaPipe face model is missing: {resolved_model}")
        vision = mediapipe_module.tasks.vision
        options = vision.FaceLandmarkerOptions(
            base_options=mediapipe_module.tasks.BaseOptions(
                model_asset_path=str(resolved_model)
            ),
            running_mode=vision.RunningMode.VIDEO,
            num_faces=1,
            min_face_detection_confidence=0.55,
            min_face_presence_confidence=0.55,
            min_tracking_confidence=0.55,
            output_face_blendshapes=False,
            output_facial_transformation_matrixes=False,
        )
        self._landmarker = vision.FaceLandmarker.create_from_options(options)

    def detect(
        self, frame: Any
    ) -> tuple[Optional[Landmarks], float, float, float]:
        rgb = np.ascontiguousarray(self._cv2.cvtColor(frame, self._cv2.COLOR_BGR2RGB))
        if self._mode == "legacy":
            rgb.flags.writeable = False
            result = self._landmarker.process(rgb)
            if not result.multi_face_landmarks:
                return None, 0.0, 0.0, 0.0
            points = result.multi_face_landmarks[0].landmark
        else:
            timestamp_ms = max(
                self._last_timestamp_ms + 1,
                int(time.monotonic_ns() / 1_000_000),
            )
            self._last_timestamp_ms = timestamp_ms
            image = self._mediapipe.Image(
                image_format=self._mediapipe.ImageFormat.SRGB,
                data=rgb,
            )
            result = self._landmarker.detect_for_video(image, timestamp_ms)
            if not result.face_landmarks:
                return None, 0.0, 0.0, 0.0
            points = result.face_landmarks[0]
        landmarks = {
            index: (float(point.x), float(point.y), float(point.z))
            for index, point in enumerate(points)
        }
        pitch, yaw, roll = self._estimate_head_pose(landmarks, frame.shape)
        return landmarks, yaw, pitch, roll

    def close(self) -> None:
        self._landmarker.close()

    def _estimate_head_pose(
        self, landmarks: Landmarks, frame_shape: tuple[int, ...]
    ) -> tuple[float, float, float]:
        required = (1, 152, 33, 263, 61, 291)
        if any(index not in landmarks for index in required):
            return 0.0, 0.0, 0.0
        height, width = frame_shape[:2]
        image_points = np.asarray(
            [
                (landmarks[index][0] * width, landmarks[index][1] * height)
                for index in required
            ],
            dtype=np.float64,
        )
        model_points = np.asarray(
            [
                (0.0, 0.0, 0.0),
                (0.0, -63.6, -12.5),
                (-43.3, 32.7, -26.0),
                (43.3, 32.7, -26.0),
                (-28.9, -28.9, -24.1),
                (28.9, -28.9, -24.1),
            ],
            dtype=np.float64,
        )
        focal = float(width)
        camera = np.asarray(
            [[focal, 0.0, width / 2.0], [0.0, focal, height / 2.0], [0.0, 0.0, 1.0]],
            dtype=np.float64,
        )
        success, rotation_vector, _ = self._cv2.solvePnP(
            model_points,
            image_points,
            camera,
            np.zeros((4, 1), dtype=np.float64),
            flags=self._cv2.SOLVEPNP_ITERATIVE,
        )
        if not success:
            return 0.0, 0.0, 0.0
        rotation, _ = self._cv2.Rodrigues(rotation_vector)
        angles = self._cv2.RQDecomp3x3(rotation)[0]
        return float(angles[0]), float(angles[1]), float(angles[2])


class CameraGazeTracker:
    def __init__(
        self,
        configuration: GazeConfiguration,
        *,
        detector: Optional[FaceLandmarkDetector] = None,
        calibration: Optional[CalibrationModel] = None,
        display_signature: str = "",
    ) -> None:
        self._configuration = configuration
        self._detector = detector
        self._owns_detector = detector is None
        self._calibration = calibration
        self._display_signature = display_signature
        self._estimator = LandmarkGazeEstimator()
        self._transform = GazeTransform(configuration)
        self._capture: Any = None
        self._cv2: Any = None
        self._thread: Optional[Thread] = None
        self._stop = Event()
        self._lock = Lock()
        self._latest = GazeSample(None, None, 0.0, False, 0)

    @property
    def is_running(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def configure(self, configuration: GazeConfiguration) -> None:
        self._configuration = configuration
        self._transform.update(configuration)

    def set_calibration(
        self,
        calibration: Optional[CalibrationModel],
        display_signature: str,
    ) -> None:
        self._calibration = calibration
        self._display_signature = display_signature

    def start(self) -> None:
        if self.is_running:
            return
        import cv2

        self._cv2 = cv2
        if self._detector is None:
            self._detector = MediaPipeFaceMeshDetector(cv2_module=cv2)
        self._capture = CameraDeviceProbe.open_device(
            cv2,
            self._configuration.camera_index,
        )
        if self._capture is None:
            raise RuntimeError(
                f"camera {self._configuration.camera_index + 1} could not be opened"
            )
        self._stop.clear()
        self._thread = Thread(target=self._capture_loop, name="anchor-gaze", daemon=True)
        self._thread.start()

    def read(self) -> GazeSample:
        with self._lock:
            return self._latest

    def process_frame(self, frame: Any, timestamp_ms: int) -> GazeSample:
        if self._detector is None:
            raise RuntimeError("gaze detector is not initialized")
        landmarks, yaw, pitch, roll = self._detector.detect(frame)
        raw = self._estimator.estimate(
            landmarks,
            timestamp_ms,
            yaw=yaw,
            pitch=pitch,
            roll=roll,
        )
        adjusted = self._transform.apply(raw)
        if adjusted.available and self._calibration is not None:
            try:
                x, y = self._calibration.apply(
                    (float(adjusted.x), float(adjusted.y), yaw, pitch),
                    self._display_signature,
                )
                adjusted = replace(adjusted, x=x, y=y)
            except CalibrationInvalidatedError:
                self._calibration = None
        return adjusted

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        if self._capture is not None:
            self._capture.release()
            self._capture = None
        if self._detector is not None and self._owns_detector:
            self._detector.close()
            self._detector = None

    def _capture_loop(self) -> None:
        assert self._capture is not None
        while not self._stop.is_set():
            success, frame = self._capture.read()
            timestamp_ms = int(time.time() * 1000)
            if not success or frame is None:
                sample = GazeSample(None, None, 0.0, False, timestamp_ms)
                time.sleep(0.03)
            else:
                sample = self.process_frame(frame, timestamp_ms)
                sample = replace(sample, preview_jpeg=self._encode_preview(frame, sample))
            with self._lock:
                self._latest = sample

    def _encode_preview(self, frame: Any, sample: GazeSample) -> bytes:
        if self._cv2 is None:
            return b""
        preview = frame.copy()
        if self._configuration.mirror:
            preview = self._cv2.flip(preview, 1)
        if self._configuration.rotation_degrees == 90:
            preview = self._cv2.rotate(preview, self._cv2.ROTATE_90_CLOCKWISE)
        elif self._configuration.rotation_degrees == 180:
            preview = self._cv2.rotate(preview, self._cv2.ROTATE_180)
        elif self._configuration.rotation_degrees == 270:
            preview = self._cv2.rotate(preview, self._cv2.ROTATE_90_COUNTERCLOCKWISE)
        if sample.available:
            height, width = preview.shape[:2]
            self._cv2.circle(
                preview,
                (int(float(sample.x) * width), int(float(sample.y) * height)),
                12,
                (80, 210, 120),
                3,
            )
        success, encoded = self._cv2.imencode(".jpg", preview)
        return encoded.tobytes() if success else b""
