from __future__ import annotations

from collections import deque
from dataclasses import asdict, dataclass, replace
import json
from pathlib import Path
from threading import Event, Lock, Thread
import time
from typing import Any, Deque, Optional, Protocol

import numpy as np

from .calibration import CalibrationInvalidatedError, CalibrationModel
from .gaze import (
    GazeConfiguration,
    GazeSample,
    GazeSmoother,
    GazeTransform,
    LandmarkGazeEstimator,
    Landmarks,
)


@dataclass(frozen=True)
class WebcamRecordingStatus:
    recording: bool
    video_path: str
    frame_count: int
    elapsed_seconds: float
    error: str = ""


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
    def list_devices(cv2_module: Any, max_index: int = 8) -> list[CameraDevice]:
        """Probes indices in order and stops after `EMPTY_INDEX_LIMIT` consecutive indices
        that no backend can even open, since Windows numbers cameras contiguously. This keeps
        the probe well inside the RPC deadline on machines without a camera."""
        devices: list[CameraDevice] = []
        empty_run = 0
        for index in range(max(0, max_index)):
            opened_any = False
            usable = False
            for _, capture, delivers_frames, _ in CameraDeviceProbe.try_backends(cv2_module, index):
                if capture is None:
                    continue
                opened_any = True
                capture.release()
                if delivers_frames:
                    usable = True
                    break
            if usable:
                devices.append(CameraDevice(index, f"Camera {index + 1}"))
            empty_run = 0 if opened_any else empty_run + 1
            if empty_run >= CameraDeviceProbe.EMPTY_INDEX_LIMIT:
                break
        return devices

    BACKENDS = ("CAP_DSHOW", "CAP_MSMF", "CAP_ANY")
    EMPTY_INDEX_LIMIT = 2

    @staticmethod
    def open_device(cv2_module: Any, index: int) -> Any | None:
        for _, capture, delivers_frames, _ in CameraDeviceProbe.try_backends(cv2_module, index):
            if capture is not None and delivers_frames:
                return capture
            if capture is not None:
                capture.release()
        return None

    @staticmethod
    def try_backends(cv2_module: Any, index: int, attempts: int = 5):
        """Yields (backend name, capture or None, frame delivered, error) per backend.

        `isOpened()` alone is not enough on Windows: a device held by another app or
        blocked by the camera privacy setting can open yet never deliver a frame.
        """
        seen: list[int] = []
        for name in CameraDeviceProbe.BACKENDS:
            backend = int(getattr(cv2_module, name, 0))
            if backend in seen:
                continue
            seen.append(backend)
            try:
                capture = cv2_module.VideoCapture(index, backend)
            except Exception as error:  # noqa: BLE001 - reported to the caller
                yield name, None, False, str(error)
                continue
            if not capture.isOpened():
                capture.release()
                yield name, None, False, "not opened"
                continue
            delivered = False
            for _ in range(max(1, attempts)):
                success, frame = capture.read()
                if success and frame is not None and frame.size > 0:
                    delivered = True
                    break
                time.sleep(0.1)
            yield name, capture, delivered, "" if delivered else "opened but no frames"

    @staticmethod
    def diagnose(cv2_module: Any, max_index: int = 8) -> list[dict[str, Any]]:
        report: list[dict[str, Any]] = []
        for index in range(max(0, max_index)):
            for name, capture, delivered, error in CameraDeviceProbe.try_backends(cv2_module, index, attempts=3):
                entry: dict[str, Any] = {
                    "index": index,
                    "backend": name,
                    "opened": capture is not None,
                    "frames": delivered,
                    "error": error,
                }
                if capture is not None:
                    entry["width"] = int(capture.get(cv2_module.CAP_PROP_FRAME_WIDTH))
                    entry["height"] = int(capture.get(cv2_module.CAP_PROP_FRAME_HEIGHT))
                    capture.release()
                report.append(entry)
        return report


class GazeSettingsStore:
    def __init__(self, path: Path | str) -> None:
        self._path = Path(path)

    @property
    def path(self) -> Path:
        return self._path

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
        self._smoother = GazeSmoother(configuration.smoothing)
        self._capture: Any = None
        self._cv2: Any = None
        self._thread: Optional[Thread] = None
        self._stop = Event()
        self._lock = Lock()
        self._latest = GazeSample(None, None, 0.0, False, 0)
        self._recent: Deque[tuple[int, tuple[float, ...]]] = deque(maxlen=self.HISTORY_FRAMES)
        self._writer: Any = None
        self._recording_path = ""
        self._recording_started = 0.0
        self._recording_frames = 0
        self._recording_error = ""

    HISTORY_FRAMES = 60
    # Eye-ratio spread (across frames in the sampling window) above which the user was not
    # holding a fixation, so the click cannot be trusted as ground truth.
    MAX_FIXATION_SPREAD = 0.06

    @property
    def is_running(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    @property
    def is_calibrated(self) -> bool:
        return self._calibration is not None

    def configure(self, configuration: GazeConfiguration) -> None:
        self._configuration = configuration
        self._transform.update(configuration)
        self._smoother.update(configuration.smoothing)

    def set_calibration(
        self,
        calibration: Optional[CalibrationModel],
        display_signature: str,
    ) -> None:
        self._calibration = calibration
        self._display_signature = display_signature
        self._smoother.reset()

    def sample_fixation(self, window_ms: int = 400, min_frames: int = 3) -> tuple[float, ...]:
        """Averages the raw gaze features seen during the last `window_ms` (the moment the user
        clicked a calibration target). Refuses when too few confident frames exist or the eyes
        were moving, so an unsteady click never enters the fit."""
        with self._lock:
            latest_ms = self._recent[-1][0] if self._recent else 0
            frames = [features for at, features in self._recent if latest_ms - at <= window_ms]
        if len(frames) < min_frames:
            raise RuntimeError(
                "no steady gaze was seen at the moment of the click; look at the target and click again"
            )
        matrix = np.asarray(frames, dtype=np.float64)
        spread = float(np.max(np.ptp(matrix[:, :4], axis=0)))
        if spread > self.MAX_FIXATION_SPREAD:
            raise RuntimeError(
                "eyes were still moving when you clicked; hold your gaze on the target, then click"
            )
        return tuple(float(value) for value in np.median(matrix, axis=0))

    def start_recording(self, output_directory: str) -> WebcamRecordingStatus:
        if not self.is_running or self._capture is None or self._cv2 is None:
            raise RuntimeError("open the webcam before recording")
        with self._lock:
            if self._writer is not None:
                return self._recording_status_locked()
            directory = Path(output_directory)
            directory.mkdir(parents=True, exist_ok=True)
            width = int(self._capture.get(self._cv2.CAP_PROP_FRAME_WIDTH)) or 640
            height = int(self._capture.get(self._cv2.CAP_PROP_FRAME_HEIGHT)) or 480
            fps = float(self._capture.get(self._cv2.CAP_PROP_FPS)) or 0.0
            if not 5.0 <= fps <= 60.0:
                fps = 20.0
            path = directory / time.strftime("webcam-%Y%m%d-%H%M%S.mp4")
            writer = self._cv2.VideoWriter(
                str(path), self._cv2.VideoWriter_fourcc(*"mp4v"), fps, (width, height)
            )
            if not writer.isOpened():
                raise RuntimeError(f"could not create {path}")
            self._writer = writer
            self._recording_path = str(path)
            self._recording_started = time.monotonic()
            self._recording_frames = 0
            self._recording_error = ""
            return self._recording_status_locked()

    def stop_recording(self) -> WebcamRecordingStatus:
        with self._lock:
            status = self._recording_status_locked()
            if self._writer is not None:
                self._writer.release()
                self._writer = None
            return replace(status, recording=False)

    def recording_status(self) -> WebcamRecordingStatus:
        with self._lock:
            return self._recording_status_locked()

    def _recording_status_locked(self) -> WebcamRecordingStatus:
        recording = self._writer is not None
        elapsed = time.monotonic() - self._recording_started if self._recording_path else 0.0
        return WebcamRecordingStatus(
            recording,
            self._recording_path,
            self._recording_frames,
            elapsed,
            self._recording_error,
        )

    def start(self) -> None:
        if self.is_running:
            return
        import cv2

        self._cv2 = cv2
        if self._detector is None:
            try:
                self._detector = MediaPipeFaceMeshDetector(cv2_module=cv2)
            except ImportError as error:
                raise RuntimeError(
                    "the MediaPipe face model could not load on this Windows install "
                    f"({error}). Install the Media Foundation feature and the latest "
                    "Microsoft Visual C++ redistributable, then retry."
                ) from error
        self._capture = CameraDeviceProbe.open_device(
            cv2,
            self._configuration.camera_index,
        )
        if self._capture is None:
            raise RuntimeError(
                f"camera {self._configuration.camera_index + 1} could not be opened. "
                "Close other apps using it and allow desktop apps under "
                "Settings > Privacy & security > Camera."
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
        if raw.available and raw.confidence >= self._configuration.min_confidence:
            with self._lock:
                self._recent.append((timestamp_ms, raw.features))
        if self._calibration is None:
            return self._transform.apply(raw)

        # Calibrated path: the fitted mapping replaces mirror/offset/sensitivity entirely, since
        # it learned screen coordinates directly from raw iris ratios and head pose.
        if not raw.available or raw.confidence < self._configuration.min_confidence:
            self._smoother.reset()
            return replace(raw, x=None, y=None)
        try:
            x, y = self._calibration.apply(raw.features, self._display_signature)
        except CalibrationInvalidatedError:
            self._calibration = None
            return self._transform.apply(raw)
        x, y = self._smoother.apply(x, y)
        return replace(raw, x=x, y=y)

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        self.stop_recording()
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
                self._write_recording_frame(frame)
            with self._lock:
                self._latest = sample

    def _write_recording_frame(self, frame: Any) -> None:
        with self._lock:
            writer = self._writer
        if writer is None:
            return
        try:
            writer.write(frame)
            with self._lock:
                if self._writer is writer:
                    self._recording_frames += 1
        except Exception as error:  # noqa: BLE001 - surfaced through status
            with self._lock:
                self._recording_error = str(error)
                if self._writer is writer:
                    self._writer = None
            writer.release()

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
        if sample.eyes:
            preview = self._append_eye_strip(frame, preview, sample)
        success, encoded = self._cv2.imencode(".jpg", preview)
        return encoded.tobytes() if success else b""

    def _append_eye_strip(self, frame: Any, preview: Any, sample: GazeSample) -> Any:
        """Adds a strip under the preview with each eye enlarged and its iris centre marked,
        so the user can see exactly what the gaze estimate is built from."""
        cv2 = self._cv2
        assert cv2 is not None
        frame_height, frame_width = frame.shape[:2]
        preview_width = preview.shape[1]
        strip_height = max(48, preview_width // 6)
        crops = []
        for eye in sample.eyes:
            x0 = max(0, int(eye.left * frame_width))
            y0 = max(0, int(eye.top * frame_height))
            x1 = min(frame_width, int((eye.left + eye.width) * frame_width))
            y1 = min(frame_height, int((eye.top + eye.height) * frame_height))
            if x1 - x0 < 4 or y1 - y0 < 4:
                continue
            crop = frame[y0:y1, x0:x1].copy()
            crop_height, crop_width = crop.shape[:2]
            scale = strip_height / crop_height
            crop = cv2.resize(
                crop,
                (max(1, int(crop_width * scale)), strip_height),
                interpolation=cv2.INTER_CUBIC,
            )
            cv2.circle(
                crop,
                (
                    int((eye.iris_x * frame_width - x0) * scale),
                    int((eye.iris_y * frame_height - y0) * scale),
                ),
                max(3, strip_height // 12),
                (80, 210, 120),
                2,
            )
            if self._configuration.mirror:
                crop = cv2.flip(crop, 1)
            crops.append(crop)
        if not crops:
            return preview
        if self._configuration.mirror:
            crops.reverse()
        strip = np.zeros((strip_height, preview_width, 3), dtype=preview.dtype)
        gap = 8
        total = sum(crop.shape[1] for crop in crops) + gap * (len(crops) - 1)
        offset = max(0, (preview_width - total) // 2)
        for crop in crops:
            width = min(crop.shape[1], preview_width - offset)
            if width <= 0:
                break
            strip[:, offset : offset + width] = crop[:, :width]
            offset += width + gap
        return np.vstack((preview, strip))
