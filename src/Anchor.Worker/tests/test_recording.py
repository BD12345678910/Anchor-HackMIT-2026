from __future__ import annotations

import csv
import json
import time

import cv2
import numpy as np

from anchor_worker.recording import StudyRecorder


def _frame_source():
    frame = np.full((180, 320, 3), 28, dtype=np.uint8)
    frame[:, :80] = (80, 40, 140)
    return frame


def test_records_playable_mp4_and_aligned_metrics(tmp_path):
    recorder = StudyRecorder(fps=30, capture=_frame_source)
    manifest = recorder.start(tmp_path, trial_mode="anchor_enabled", participant_code="P01")
    recorder.append_sample({"at_ms": 40, "gaze_x": 0.4, "gaze_y": 0.6, "confidence": 0.9})
    recorder.append_event({"type": "intervention", "at_ms": 120, "presented": True})
    time.sleep(0.18)
    result = recorder.stop()

    video = cv2.VideoCapture(str(manifest.video_path))
    frame_count = int(video.get(cv2.CAP_PROP_FRAME_COUNT))
    video.release()
    summary = json.loads(manifest.summary_path.read_text(encoding="utf-8"))
    assert result.status == "complete"
    assert result.video_usable is True
    assert frame_count >= 3
    assert summary["trialMode"] == "anchor_enabled"
    assert summary["interventionsEnabled"] is True


def test_baseline_keeps_gaze_samples_but_never_logs_presented_intervention(tmp_path):
    recorder = StudyRecorder(fps=30, capture=_frame_source)
    manifest = recorder.start(tmp_path, trial_mode="baseline", participant_code="P02")
    for index in range(3):
        recorder.append_sample({"at_ms": index * 50, "gaze_x": 0.2 + index * 0.1, "gaze_y": 0.5, "confidence": 0.8})
    recorder.append_event({"type": "intervention", "at_ms": 120, "presented": True})
    time.sleep(0.12)
    recorder.stop()

    rows = list(csv.DictReader(manifest.samples_path.open(encoding="utf-8")))
    events = [json.loads(line) for line in manifest.events_path.read_text(encoding="utf-8").splitlines()]
    summary = json.loads(manifest.summary_path.read_text(encoding="utf-8"))
    assert len(rows) == 3
    assert not any(item.get("type") == "intervention" and item.get("presented") for item in events)
    assert summary["interventionsEnabled"] is False


def test_capture_failure_finalizes_outputs_without_claiming_success(tmp_path):
    calls = 0

    def failing_capture():
        nonlocal calls
        calls += 1
        if calls > 3:
            raise OSError("capture lost")
        return _frame_source()

    recorder = StudyRecorder(fps=60, capture=failing_capture)
    manifest = recorder.start(tmp_path, trial_mode="anchor_enabled", participant_code="P03")
    deadline = time.monotonic() + 2
    while recorder.status.status == "recording" and time.monotonic() < deadline:
        time.sleep(0.02)
    result = recorder.stop()

    persisted = json.loads(manifest.manifest_path.read_text(encoding="utf-8"))
    assert result.status == "failed"
    assert result.error_code == "screen_capture_failed"
    assert persisted["status"] == "failed"
    assert persisted["videoUsable"] in (True, False)


def test_rejects_non_pseudonymous_participant_codes(tmp_path):
    recorder = StudyRecorder(fps=15, capture=_frame_source)
    try:
        recorder.start(tmp_path, trial_mode="baseline", participant_code="person@example.com")
    except ValueError as error:
        assert "participant" in str(error).lower()
    else:
        raise AssertionError("personally identifying participant code was accepted")
