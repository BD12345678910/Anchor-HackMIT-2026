from __future__ import annotations

from dataclasses import asdict, dataclass
import csv
import json
from pathlib import Path
from queue import Empty, Full, Queue
import re
import shutil
import threading
import time
from typing import Callable, Mapping, Optional
from uuid import uuid4

import cv2
import numpy as np


PARTICIPANT_CODE = re.compile(r"^[A-Za-z0-9_-]{1,32}$")
SAMPLE_FIELDS = [
    "at_ms", "gaze_x", "gaze_y", "confidence", "face_present",
    "attention_state", "distraction_probability", "task", "subtask",
]


@dataclass(frozen=True)
class RecordingManifest:
    trial_id: str
    trial_mode: str
    participant_code: str
    video_path: Path
    events_path: Path
    samples_path: Path
    summary_path: Path
    manifest_path: Path


@dataclass(frozen=True)
class RecordingStatus:
    trial_id: str
    status: str
    frame_count: int
    dropped_frames: int
    elapsed_seconds: float
    video_usable: bool
    error_code: Optional[str] = None


class MssScreenCapture:
    def __init__(self, display_index: int = 1) -> None:
        self._display_index = max(1, display_index)
        self._mss = None
        self._monitor = None

    def __call__(self) -> np.ndarray:
        if self._mss is None:
            import mss

            self._mss = mss.mss()
            monitors = self._mss.monitors
            index = self._display_index if self._display_index < len(monitors) else 1
            self._monitor = monitors[index]
        frame = np.asarray(self._mss.grab(self._monitor), dtype=np.uint8)
        return cv2.cvtColor(frame, cv2.COLOR_BGRA2BGR)

    def close(self) -> None:
        if self._mss is not None:
            self._mss.close()
            self._mss = None


class StudyRecorder:
    def __init__(
        self,
        *,
        fps: int = 15,
        capture: Optional[Callable[[], np.ndarray]] = None,
        minimum_free_bytes: int = 256 * 1024 * 1024,
    ) -> None:
        if fps < 1 or fps > 60:
            raise ValueError("fps must be between 1 and 60")
        self._fps = fps
        self._capture = capture
        self._minimum_free_bytes = minimum_free_bytes
        self._frames: Queue[tuple[int, np.ndarray]] = Queue(maxsize=2)
        self._stop = threading.Event()
        self._capture_thread: Optional[threading.Thread] = None
        self._writer_thread: Optional[threading.Thread] = None
        self._manifest: Optional[RecordingManifest] = None
        self._started = 0.0
        self._frame_count = 0
        self._dropped_frames = 0
        self._video_usable = False
        self._status_name = "idle"
        self._error_code: Optional[str] = None
        self._lock = threading.RLock()
        self._events_file = None
        self._samples_file = None
        self._sample_writer: Optional[csv.DictWriter] = None
        self._sample_count = 0
        self._usable_gaze_samples = 0
        self._gaze_away_samples = 0
        self._intervention_count = 0
        self._subtasks_completed = 0
        self._distraction_ms = 0
        self._recovery_ms = 0
        self._interruption_count = 0
        self._last_sample_at_ms: Optional[int] = None
        self._last_attention_state = ""
        self._latest_sample: dict[str, object] = {}

    @property
    def status(self) -> RecordingStatus:
        with self._lock:
            return RecordingStatus(
                self._manifest.trial_id if self._manifest else "",
                self._status_name,
                self._frame_count,
                self._dropped_frames,
                self._elapsed(),
                self._video_usable,
                self._error_code,
            )

    def start(
        self,
        output_directory: str | Path,
        *,
        trial_mode: str,
        participant_code: str,
    ) -> RecordingManifest:
        if self._status_name == "recording":
            raise RuntimeError("a recording is already active")
        if trial_mode not in {"baseline", "anchor_enabled"}:
            raise ValueError("trial mode must be baseline or anchor_enabled")
        if not PARTICIPANT_CODE.fullmatch(participant_code or ""):
            raise ValueError("participant code must be a pseudonymous 1-32 character identifier")
        output = Path(output_directory).expanduser().resolve()
        output.mkdir(parents=True, exist_ok=True)
        if shutil.disk_usage(output).free < self._minimum_free_bytes:
            raise OSError("insufficient disk space for recording")

        trial_id = f"{participant_code}-{int(time.time())}-{uuid4().hex[:8]}"
        stem = output / trial_id
        self._manifest = RecordingManifest(
            trial_id,
            trial_mode,
            participant_code,
            stem.with_suffix(".mp4"),
            Path(f"{stem}.events.jsonl"),
            Path(f"{stem}.samples.csv"),
            Path(f"{stem}.summary.json"),
            Path(f"{stem}.manifest.json"),
        )
        self._capture = self._capture or MssScreenCapture()
        self._events_file = self._manifest.events_path.open("w", encoding="utf-8", newline="\n")
        self._samples_file = self._manifest.samples_path.open("w", encoding="utf-8", newline="")
        self._sample_writer = csv.DictWriter(self._samples_file, fieldnames=SAMPLE_FIELDS, extrasaction="ignore")
        self._sample_writer.writeheader()
        self._stop.clear()
        self._started = time.monotonic()
        self._status_name = "recording"
        self._error_code = None
        self._capture_thread = threading.Thread(target=self._capture_loop, name="anchor-screen-capture", daemon=True)
        self._writer_thread = threading.Thread(target=self._writer_loop, name="anchor-video-writer", daemon=True)
        self._write_manifest()
        self._writer_thread.start()
        self._capture_thread.start()
        return self._manifest

    def append_sample(self, sample: Mapping[str, object]) -> None:
        with self._lock:
            if self._status_name not in {"recording", "failed"} or self._sample_writer is None:
                raise RuntimeError("no recording is active")
            row = {key: sample.get(key, "") for key in SAMPLE_FIELDS}
            self._sample_writer.writerow(row)
            self._samples_file.flush()
            self._sample_count += 1
            confidence = _number(sample.get("confidence"))
            x = _optional_number(sample.get("gaze_x"))
            y = _optional_number(sample.get("gaze_y"))
            if confidence >= 0.45 and x is not None and y is not None:
                self._usable_gaze_samples += 1
                if x < 0.02 or x > 0.98 or y < 0.02 or y > 0.98:
                    self._gaze_away_samples += 1
            else:
                self._gaze_away_samples += 1
            at_ms = int(_number(sample.get("at_ms")))
            state = str(sample.get("attention_state") or "").lower()
            if self._last_sample_at_ms is not None:
                interval = max(0, at_ms - self._last_sample_at_ms)
                if self._last_attention_state in {"distracted", "drifting", "stuck"}:
                    self._distraction_ms += interval
                elif self._last_attention_state == "recovering":
                    self._recovery_ms += interval
            if state in {"distracted", "drifting", "stuck"} and self._last_attention_state not in {"distracted", "drifting", "stuck"}:
                self._interruption_count += 1
            self._last_sample_at_ms = at_ms
            self._last_attention_state = state
            self._latest_sample = dict(row)

    def append_event(self, event: Mapping[str, object]) -> None:
        with self._lock:
            if self._manifest is None or self._events_file is None:
                raise RuntimeError("no recording is active")
            item = dict(event)
            if self._manifest.trial_mode == "baseline" and item.get("type") == "intervention" and item.get("presented"):
                return
            if item.get("type") == "intervention" and item.get("presented"):
                self._intervention_count += 1
            if item.get("type") == "subtask_completed":
                self._subtasks_completed += 1
            item.setdefault("at_ms", int(self._elapsed() * 1000))
            self._events_file.write(json.dumps(item, ensure_ascii=False, separators=(",", ":")) + "\n")
            self._events_file.flush()

    def stop(self) -> RecordingStatus:
        if self._manifest is None:
            return self.status
        self._stop.set()
        if self._capture_thread and self._capture_thread is not threading.current_thread():
            self._capture_thread.join(timeout=5)
        if self._writer_thread and self._writer_thread is not threading.current_thread():
            self._writer_thread.join(timeout=5)
        with self._lock:
            if self._status_name == "recording":
                self._status_name = "complete" if self._video_usable else "failed"
                if not self._video_usable:
                    self._error_code = "video_unusable"
            self._close_outputs()
            self._write_summary()
            self._write_manifest()
            return self.status

    def _capture_loop(self) -> None:
        interval = 1.0 / self._fps
        next_frame = time.monotonic()
        try:
            while not self._stop.is_set():
                frame = self._capture()
                if not isinstance(frame, np.ndarray) or frame.ndim != 3 or frame.size == 0:
                    raise OSError("capture returned an invalid frame")
                item = (int(self._elapsed() * 1000), frame.copy())
                try:
                    self._frames.put_nowait(item)
                except Full:
                    try:
                        self._frames.get_nowait()
                        self._frames.task_done()
                    except Empty:
                        pass
                    self._dropped_frames += 1
                    self._frames.put_nowait(item)
                next_frame += interval
                self._stop.wait(max(0.0, next_frame - time.monotonic()))
        except Exception:
            self._fail("screen_capture_failed")
        finally:
            close = getattr(self._capture, "close", None)
            if callable(close):
                close()

    def _writer_loop(self) -> None:
        writer: Optional[cv2.VideoWriter] = None
        try:
            while not self._stop.is_set() or not self._frames.empty():
                try:
                    at_ms, frame = self._frames.get(timeout=0.1)
                except Empty:
                    continue
                if writer is None:
                    height, width = frame.shape[:2]
                    writer = cv2.VideoWriter(
                        str(self._manifest.video_path),
                        cv2.VideoWriter_fourcc(*"mp4v"),
                        float(self._fps),
                        (width, height),
                    )
                    if not writer.isOpened():
                        raise OSError("MP4 writer could not be opened")
                writer.write(self._composite(frame, at_ms))
                with self._lock:
                    self._frame_count += 1
                self._frames.task_done()
        except Exception:
            self._fail("video_writer_failed")
        finally:
            if writer is not None:
                writer.release()
            with self._lock:
                self._video_usable = bool(
                    self._frame_count > 0
                    and self._manifest.video_path.exists()
                    and self._manifest.video_path.stat().st_size > 0
                )

    def _composite(self, frame: np.ndarray, at_ms: int) -> np.ndarray:
        result = frame.copy()
        sample = dict(self._latest_sample)
        x = _optional_number(sample.get("gaze_x"))
        y = _optional_number(sample.get("gaze_y"))
        confidence = _number(sample.get("confidence"))
        if x is not None and y is not None and confidence >= 0.45:
            cv2.circle(result, (int(x * result.shape[1]), int(y * result.shape[0])), 12, (50, 80, 255), 3)
        state = str(sample.get("attention_state") or "observing")
        task = str(sample.get("task") or "Anchor study")[:70]
        subtask = str(sample.get("subtask") or "")[:70]
        cv2.rectangle(result, (0, 0), (result.shape[1], 72), (18, 24, 38), -1)
        cv2.putText(result, f"REC {at_ms / 1000:06.1f}s  {state}", (12, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.52, (230, 235, 255), 1, cv2.LINE_AA)
        cv2.putText(result, task, (12, 43), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (190, 205, 255), 1, cv2.LINE_AA)
        cv2.putText(result, subtask, (12, 63), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (170, 180, 205), 1, cv2.LINE_AA)
        return result

    def _fail(self, error_code: str) -> None:
        with self._lock:
            self._status_name = "failed"
            self._error_code = error_code
        self._stop.set()

    def _write_summary(self) -> None:
        duration = max(self._elapsed(), 0.001)
        sample_interval = duration / max(1, self._sample_count)
        summary = {
            "schemaVersion": 1,
            "trialId": self._manifest.trial_id,
            "trialMode": self._manifest.trial_mode,
            "participantCode": self._manifest.participant_code,
            "interventionsEnabled": self._manifest.trial_mode == "anchor_enabled",
            "durationSeconds": round(duration, 3),
            "usableGazeCoverage": round(self._usable_gaze_samples / max(1, self._sample_count), 4),
            "gazeAwaySeconds": round(self._gaze_away_samples * sample_interval, 3),
            "distractionSeconds": round(self._distraction_ms / 1000, 3),
            "recoverySeconds": round(self._recovery_ms / 1000, 3),
            "interruptionCount": self._interruption_count,
            "interventionCount": self._intervention_count,
            "subtasksCompleted": self._subtasks_completed,
            "sampleCount": self._sample_count,
            "frameCount": self._frame_count,
        }
        _atomic_json(self._manifest.summary_path, summary)

    def _write_manifest(self) -> None:
        if self._manifest is None:
            return
        payload = {
            "schemaVersion": 1,
            "trialId": self._manifest.trial_id,
            "trialMode": self._manifest.trial_mode,
            "participantCode": self._manifest.participant_code,
            "status": self._status_name,
            "errorCode": self._error_code,
            "videoUsable": self._video_usable,
            "frameCount": self._frame_count,
            "droppedFrames": self._dropped_frames,
            "videoPath": str(self._manifest.video_path),
            "eventsPath": str(self._manifest.events_path),
            "samplesPath": str(self._manifest.samples_path),
            "summaryPath": str(self._manifest.summary_path),
        }
        _atomic_json(self._manifest.manifest_path, payload)

    def _close_outputs(self) -> None:
        for handle in (self._events_file, self._samples_file):
            if handle is not None and not handle.closed:
                handle.flush()
                handle.close()

    def _elapsed(self) -> float:
        return max(0.0, time.monotonic() - self._started) if self._started else 0.0


def _atomic_json(path: Path, payload: Mapping[str, object]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def _number(value: object) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def _optional_number(value: object) -> Optional[float]:
    try:
        number = float(value)
        return number if np.isfinite(number) else None
    except (TypeError, ValueError):
        return None
