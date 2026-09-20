import asyncio

from anchor_worker.calibration import CalibrationModel, CalibrationSample
from anchor_worker.camera import WebcamRecordingStatus
from anchor_worker.gaze import GazeConfiguration, GazeSample
from anchor_worker.generated import anchor_pb2
from anchor_worker.server import WorkerService


class _Context:
    def invocation_metadata(self):
        return (("x-anchor-token", "secret"),)

    async def abort(self, code, message):
        raise AssertionError(f"unexpected abort {code}: {message}")


class _GazeController:
    def __init__(self, sample):
        self.sample = sample
        self.configuration = GazeConfiguration()
        self.is_calibrated = False
        self.calibration_target_count = 0
        self.recording = WebcamRecordingStatus(False, "", 0, 0.0)

    def list_cameras(self):
        return []

    def add_calibration_sample(self, target_x, target_y):
        if target_x > 0.9:
            raise RuntimeError("eyes were still moving when you clicked")
        self.calibration_target_count += 1
        return self.calibration_target_count

    def finish_calibration(self, display_signature):
        samples = [
            CalibrationSample((x, y, 0.0, 0.0), (x, y))
            for x, y in ((0.0, 0.0), (1.0, 0.0), (0.5, 0.5), (0.0, 1.0), (1.0, 1.0))
        ]
        self.is_calibrated = True
        return CalibrationModel.fit(samples, display_signature)

    def reset_calibration(self):
        self.is_calibrated = False

    def start_webcam_recording(self, output_directory):
        self.recording = WebcamRecordingStatus(True, output_directory + "/webcam.mp4", 0, 0.0)
        return self.recording

    def webcam_recording_status(self):
        return self.recording

    def stop_webcam_recording(self):
        self.recording = WebcamRecordingStatus(False, self.recording.video_path, 42, 2.1)
        return self.recording

    def configure(self, configuration, display_signature):
        self.configuration = configuration
        self.display_signature = display_signature
        return configuration

    def start(self):
        return None

    def read(self):
        return self.sample

    def stop(self):
        return None


def test_read_gaze_rpc_maps_missing_face_to_unavailable_outcome():
    controller = _GazeController(GazeSample(None, None, 0.0, False, 123))
    service = WorkerService("secret", asyncio.Event(), gaze_controller=controller)

    reply = asyncio.run(service.ReadGaze(anchor_pb2.ReadGazeRequest(), _Context()))

    assert reply.WhichOneof("outcome") == "unavailable"
    assert "face" in reply.unavailable.reason
    assert reply.face_present is False


def test_read_gaze_rpc_maps_coordinates_confidence_pose_and_preview():
    controller = _GazeController(
        GazeSample(
            0.23,
            0.81,
            0.91,
            True,
            456,
            yaw=3.0,
            pitch=-2.0,
            roll=1.0,
            preview_jpeg=b"jpeg",
        )
    )
    service = WorkerService("secret", asyncio.Event(), gaze_controller=controller)

    reply = asyncio.run(service.ReadGaze(anchor_pb2.ReadGazeRequest(), _Context()))

    assert reply.WhichOneof("outcome") == "point"
    assert reply.point.x == 0.23
    assert reply.point.y == 0.81
    assert reply.confidence == 0.91
    assert reply.yaw == 3.0
    assert reply.pitch == -2.0
    assert reply.roll == 1.0
    assert reply.preview_jpeg == b"jpeg"


def test_configure_gaze_rpc_maps_every_adjustable_parameter():
    controller = _GazeController(GazeSample(None, None, 0.0, False, 0))
    service = WorkerService("secret", asyncio.Event(), gaze_controller=controller)
    request = anchor_pb2.ConfigureGazeRequest(
        configuration=anchor_pb2.GazeConfigurationMessage(
            camera_index=2,
            mirror=False,
            rotation_degrees=270,
            offset_x=0.12,
            offset_y=-0.08,
            smoothing=0.4,
            sensitivity=1.35,
            min_confidence=0.72,
        ),
        display_signature="1920x1080@100",
    )

    reply = asyncio.run(service.ConfigureGaze(request, _Context()))

    assert reply.accepted is True
    assert controller.configuration == GazeConfiguration(
        camera_index=2,
        mirror=False,
        rotation_degrees=270,
        offset_x=0.12,
        offset_y=-0.08,
        smoothing=0.4,
        sensitivity=1.35,
        min_confidence=0.72,
    )
    assert controller.display_signature == "1920x1080@100"


def test_calibration_rpcs_report_targets_errors_and_unsteady_clicks():
    controller = _GazeController(GazeSample(0.5, 0.5, 0.9, True, 1))
    service = WorkerService("secret", asyncio.Event(), gaze_controller=controller)

    accepted = asyncio.run(
        service.AddCalibrationSample(
            anchor_pb2.AddCalibrationSampleRequest(target_x=0.1, target_y=0.1), _Context()
        )
    )
    rejected = asyncio.run(
        service.AddCalibrationSample(
            anchor_pb2.AddCalibrationSampleRequest(target_x=0.95, target_y=0.1), _Context()
        )
    )
    finished = asyncio.run(
        service.FinishCalibration(
            anchor_pb2.FinishCalibrationRequest(display_signature="1920x1080@100"), _Context()
        )
    )
    sample = asyncio.run(service.ReadGaze(anchor_pb2.ReadGazeRequest(), _Context()))

    assert accepted.accepted is True and accepted.target_count == 1
    assert rejected.accepted is False and "moving" in rejected.error
    assert finished.accepted is True
    assert finished.target_count == 5
    assert len(finished.target_errors) == 5
    assert finished.max_error < 0.05
    assert sample.calibrated is True


def test_webcam_recording_rpcs_round_trip_status():
    controller = _GazeController(GazeSample(0.5, 0.5, 0.9, True, 1))
    service = WorkerService("secret", asyncio.Event(), gaze_controller=controller)

    started = asyncio.run(
        service.StartWebcamRecording(
            anchor_pb2.StartWebcamRecordingRequest(output_directory="C:/demo"), _Context()
        )
    )
    stopped = asyncio.run(
        service.StopWebcamRecording(anchor_pb2.StopWebcamRecordingRequest(), _Context())
    )

    assert started.accepted is True and started.recording is True
    assert started.video_path.endswith("webcam.mp4")
    assert stopped.recording is False
    assert stopped.frame_count == 42
    assert stopped.elapsed_seconds == 2.1

