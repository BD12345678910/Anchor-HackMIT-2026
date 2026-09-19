from __future__ import annotations

import os
from pathlib import Path
from typing import Callable, Optional

from .calibration import CalibrationModel, CalibrationSample
from .camera import CameraDevice, CameraDeviceProbe, CameraGazeTracker, GazeSettingsStore
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
        self._tracker_factory = tracker_factory
        self._configuration = settings_store.load()
        self._display_signature = ""
        self._tracker: Optional[CameraGazeTracker] = None
        self._calibration_samples: list[CalibrationSample] = []

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
        if self._tracker is not None:
            self._tracker.configure(configuration)
            self._tracker.set_calibration(None, display_signature)
        if camera_changed and was_running:
            self.start()
        return configuration

    def start(self) -> None:
        if self._tracker is None:
            self._tracker = self._tracker_factory(self._configuration)
            self._tracker.set_calibration(None, self._display_signature)
        self._tracker.start()

    def read(self) -> GazeSample:
        if self._tracker is None or not self._tracker.is_running:
            return GazeSample(None, None, 0.0, False, 0)
        return self._tracker.read()

    def add_calibration_sample(self, target_x: float, target_y: float) -> int:
        if not 0.0 <= target_x <= 1.0 or not 0.0 <= target_y <= 1.0:
            raise ValueError("calibration target must be inside the display")
        sample = self.read()
        if not sample.available:
            raise RuntimeError("no confident gaze sample is available")
        self._calibration_samples.append(
            CalibrationSample(
                (float(sample.x), float(sample.y), sample.yaw, sample.pitch),
                (target_x, target_y),
            )
        )
        return len(self._calibration_samples)

    def finish_calibration(self, display_signature: str) -> CalibrationModel:
        model = CalibrationModel.fit(self._calibration_samples, display_signature)
        if self._tracker is not None:
            self._tracker.set_calibration(model, display_signature)
        self._display_signature = display_signature
        self._calibration_samples.clear()
        return model

    def stop(self) -> None:
        if self._tracker is not None:
            self._tracker.stop()
            self._tracker = None
