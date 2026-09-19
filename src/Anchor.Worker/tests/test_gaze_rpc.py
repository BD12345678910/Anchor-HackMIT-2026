import asyncio

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

    def list_cameras(self):
        return []

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

