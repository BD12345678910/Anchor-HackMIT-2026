from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Callable, Optional

from .calibration import CalibrationModel, CalibrationSample
from .camera import (
    CameraDevice,
    CameraDeviceProbe,
    CameraGazeTracker,
    GazeSettingsStore,
    WebcamRecordingStatus,
)
from .gaze import GazeConfiguration, GazeSample


class GazeController:
    def __init__(
        self,
        *,
        settings_store: Optional[GazeSettingsStore] = None,
        tracker_factory: Callable[[GazeConfiguration], CameraGazeTracker] = CameraGazeTracker,
    ) -> None:
        if settings_store is None:
            configured_directory = os.environ.get("ANCHOR_SETTINGS_DIR")
            app_data = (
                Path(configured_directory)
                if configured_directory
                else Path(os.environ.get("LOCALAPPDATA", Path.home())) / "Anchor"
            )
            settings_store = GazeSettingsStore(app_data / "gaze.json")
        self._settings_store = settings_store
        self._calibration_path = settings_store.path.with_name("gaze-calibration.json")
        self._tracker_factory = tracker_factory
        self._configuration = settings_store.load()
        self._display_signature = ""
        self._tracker: Optional[CameraGazeTracker] = None
        self._calibration_samples: list[CalibrationSample] = []
        self._calibration = self._load_calibration()

    @property
    def configuration(self) -> GazeConfiguration:
        return self._configuration

    def list_cameras(self) -> list[CameraDevice]:
        import cv2

        return CameraDeviceProbe.list_devices(cv2)

    def configure(
        self,
        configuration: GazeConfiguration,
        display_signature: str,
    ) -> GazeConfiguration:
        camera_changed = configuration.camera_index != self._configuration.camera_index
        was_running = self._tracker is not None and self._tracker.is_running
        if camera_changed and was_running:
            self.stop()
        self._configuration = configuration
        self._display_signature = display_signature
        self._calibration_samples.clear()
        self._settings_store.save(configuration)
        if camera_changed:
            self._store_calibration(None)
        if self._tracker is not None:
            self._tracker.configure(configuration)
            self._tracker.set_calibration(self._usable_calibration(), display_signature)
        if camera_changed and was_running:
            self.start()
        return configuration

    @property
    def is_calibrated(self) -> bool:
        return self._usable_calibration() is not None

    def start(self) -> None:
        if self._tracker is None:
            self._tracker = self._tracker_factory(self._configuration)
            self._tracker.set_calibration(self._usable_calibration(), self._display_signature)
        self._tracker.start()

    def read(self) -> GazeSample:
        if self._tracker is None or not self._tracker.is_running:
            return GazeSample(None, None, 0.0, False, 0)
        return self._tracker.read()

    def eye_panel(self, width: int = 420):
        """Camera view plus enlarged eyes, or None when the camera is not open."""
        if self._tracker is None or not self._tracker.is_running:
            return None
        return self._tracker.eye_panel(width)

    @property
    def calibration_target_count(self) -> int:
        return len({tuple(round(v, 3) for v in item.target) for item in self._calibration_samples})

    def add_calibration_sample(self, target_x: float, target_y: float) -> int:
        if not 0.0 <= target_x <= 1.0 or not 0.0 <= target_y <= 1.0:
            raise ValueError("calibration target must be inside the display")
        if self._tracker is None or not self._tracker.is_running:
            raise RuntimeError("the camera is not running")
        sample = self.read()
        if not sample.face_present:
            raise RuntimeError("no face is visible to the camera")
        features = self._tracker.sample_fixation()
        self._calibration_samples.append(CalibrationSample(features, (target_x, target_y)))
        return len(self._calibration_samples)

    def reset_calibration(self) -> None:
        self._calibration_samples.clear()
        self._store_calibration(None)
        if self._tracker is not None:
            self._tracker.set_calibration(None, self._display_signature)

    def finish_calibration(self, display_signature: str) -> CalibrationModel:
        model = CalibrationModel.fit(self._calibration_samples, display_signature)
        self._display_signature = display_signature
        self._store_calibration(model)
        if self._tracker is not None:
            self._tracker.set_calibration(model, display_signature)
        self._calibration_samples.clear()
        return model

    def _usable_calibration(self) -> Optional[CalibrationModel]:
        """A stored calibration is only reused for the display geometry it was fitted on."""
        model = self._calibration
        if model is None or not self._display_signature:
            return None
        return model if model.display_signature == self._display_signature else None

    def _store_calibration(self, model: Optional[CalibrationModel]) -> None:
        self._calibration = model
        try:
            if model is None:
                self._calibration_path.unlink(missing_ok=True)
                return
            self._calibration_path.parent.mkdir(parents=True, exist_ok=True)
            payload = dict(model.to_dict(), camera_index=self._configuration.camera_index)
            temporary = self._calibration_path.with_suffix(".json.tmp")
            temporary.write_text(json.dumps(payload, separators=(",", ":")), encoding="utf-8")
            temporary.replace(self._calibration_path)
        except OSError:
            pass

    def _load_calibration(self) -> Optional[CalibrationModel]:
        try:
            payload = json.loads(self._calibration_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        try:
            if int(payload.get("camera_index", -1)) != self._configuration.camera_index:
                return None
            return CalibrationModel.from_dict(payload)
        except (KeyError, TypeError, ValueError):
            return None

    def start_webcam_recording(self, output_directory: str) -> WebcamRecordingStatus:
        if self._tracker is None or not self._tracker.is_running:
            raise RuntimeError("open the webcam before recording")
        return self._tracker.start_recording(output_directory)

    def stop_webcam_recording(self) -> WebcamRecordingStatus:
        if self._tracker is None:
            return WebcamRecordingStatus(False, "", 0, 0.0)
        return self._tracker.stop_recording()

    def webcam_recording_status(self) -> WebcamRecordingStatus:
        if self._tracker is None:
            return WebcamRecordingStatus(False, "", 0, 0.0)
        return self._tracker.recording_status()

    def stop(self) -> None:
        if self._tracker is not None:
            self._tracker.stop()
            self._tracker = None
