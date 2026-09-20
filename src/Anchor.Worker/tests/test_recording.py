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


def _wide_frame():
    return np.full((720, 1280, 3), 28, dtype=np.uint8)


def _sample():
    return {
        "at_ms": 100,
        "gaze_x": 0.5,
        "gaze_y": 0.5,
        "confidence": 0.8,
        "face_present": True,
        "attention_state": "drifting",
        "distraction_probability": 0.72,
        "attention_confidence": 0.61,
        "task": "Do 3 USACO problems",
        "subtask": "Solve problem 1",
    }


def test_composite_shows_the_camera_panel_next_to_the_screen(tmp_path):
    panel = np.full((240, 260, 3), (0, 0, 255), dtype=np.uint8)
    recorder = StudyRecorder(fps=15, capture=_wide_frame, eye_panel=lambda width: panel[:, :width])
    recorder._latest_sample = _sample()

    composed = recorder._composite(_wide_frame(), 1000)

    left = 1280 - 240 - 16
    assert tuple(composed[100, left + 20]) == (0, 0, 255)
    assert not np.array_equal(composed, _wide_frame())


def test_composite_says_the_camera_is_off_instead_of_faking_an_eye_view():
    recorder = StudyRecorder(fps=15, capture=_wide_frame, eye_panel=lambda width: None)
    recorder._latest_sample = {**_sample(), "face_present": False, "confidence": 0.0}

    composed = recorder._composite(_wide_frame(), 1000)

    left = 1280 - 240 - 16
    column = composed[64:260, left:]
    assert column.max() > 28
    assert not np.any(np.all(column == (0, 0, 255), axis=-1))


def test_composite_survives_a_camera_that_raises():
    def broken(width):
        raise RuntimeError("camera disconnected")

    recorder = StudyRecorder(fps=15, capture=_wide_frame, eye_panel=broken)
    recorder._latest_sample = _sample()

    assert recorder._composite(_wide_frame(), 500).shape == (720, 1280, 3)
