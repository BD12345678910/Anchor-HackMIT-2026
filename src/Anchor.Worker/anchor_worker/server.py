from __future__ import annotations

import asyncio
from importlib import import_module
import json
import sys
from typing import Callable, Iterable

import grpc

from .features import normalize_features
from .gaze import GazeConfiguration
from .gaze_controller import GazeController
from .health import detect_capabilities
from .inference import AttentionInference
from .generated import anchor_pb2

sys.modules.setdefault("anchor_pb2", anchor_pb2)
from .generated import anchor_pb2_grpc  # noqa: E402


PROTOCOL_VERSION = 2


class AuthenticationError(PermissionError):
    pass


def require_token(metadata: Iterable[tuple[str, str]], expected: str) -> None:
    supplied = next((value for key, value in metadata if key.lower() == "x-anchor-token"), None)
    if not expected or supplied != expected:
        raise AuthenticationError("invalid worker authentication token")


def readiness_payload(port: int, token: str) -> str:
    del token
    return json.dumps(
        {
            "status": "ready",
            "host": "127.0.0.1",
            "port": port,
            "protocolVersion": PROTOCOL_VERSION,
        },
        separators=(",", ":"),
    )


def worker_capabilities(
    import_module: Callable[[str], object] = import_module,
) -> dict[str, dict[str, object]]:
    return detect_capabilities(import_module)


class WorkerService(anchor_pb2_grpc.InferenceWorkerServicer):
    def __init__(
        self,
        token: str,
        stop_event: asyncio.Event,
        gaze_controller: GazeController | None = None,
    ) -> None:
        self._token = token
        self._stop_event = stop_event
        self._inference = AttentionInference()
        self._capabilities = worker_capabilities()
        self._gaze = gaze_controller or GazeController()

    async def _authenticate(self, context: grpc.aio.ServicerContext) -> bool:
        try:
            require_token(context.invocation_metadata(), self._token)
            return True
        except AuthenticationError as error:
            await context.abort(grpc.StatusCode.UNAUTHENTICATED, str(error))
            return False

    async def Health(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.HealthResponse()
        return anchor_pb2.HealthResponse(
            protocol_version=PROTOCOL_VERSION,
            worker_version="0.1.0",
            camera=anchor_pb2.Capability(**self._capabilities["camera"]),
            visual_analysis=anchor_pb2.Capability(**self._capabilities["visual_analysis"]),
        )

    async def PredictAttention(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.AttentionPredictionReply()
        item = request.sensor_window
        result = self._inference.predict(
            normalize_features(
                {
                    "key_count": item.key_count,
                    "mouse_distance": item.mouse_distance,
                    "idle_seconds": item.idle_seconds,
                    "app_relevance": item.app_relevance,
                    "gaze_presence": item.gaze_presence,
                    "app_switch_count": item.app_switch_count,
                    "scroll_reversal_count": item.scroll_reversal_count,
                    "gaze_available": item.gaze_available,
                    "manual_report": item.manual_report,
                }
            )
        )
        return anchor_pb2.AttentionPredictionReply(
            distraction_probability=result.distraction_probability,
            confidence=result.confidence,
            reason_codes=result.reason_codes,
            latency_ms=result.latency_ms,
        )

    async def AnalyzeFrame(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.AnalyzeFrameReply()
        if not self._capabilities["visual_analysis"]["available"]:
            return anchor_pb2.AnalyzeFrameReply(
                unavailable=anchor_pb2.Unavailable(
                    reason=str(self._capabilities["visual_analysis"]["reason"])
                )
            )
        return anchor_pb2.AnalyzeFrameReply(
            unavailable=anchor_pb2.Unavailable(
                reason="visual model is enabled but no frame analyzer is configured"
            )
        )

    async def ListCameras(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.ListCamerasReply()
        try:
            devices = self._gaze.list_cameras()
            return anchor_pb2.ListCamerasReply(
                devices=[
                    anchor_pb2.CameraDeviceMessage(index=item.index, name=item.name)
                    for item in devices
                ]
            )
        except Exception as error:
            return anchor_pb2.ListCamerasReply(
                unavailable=anchor_pb2.Unavailable(reason=str(error))
            )

    async def ConfigureGaze(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.GazeConfigurationReply()
        item = request.configuration
        try:
            configuration = self._gaze.configure(
                GazeConfiguration(
                    camera_index=item.camera_index,
                    mirror=item.mirror,
                    rotation_degrees=item.rotation_degrees,
                    offset_x=item.offset_x,
                    offset_y=item.offset_y,
                    smoothing=item.smoothing,
                    sensitivity=item.sensitivity,
                    min_confidence=item.min_confidence,
                ),
                request.display_signature,
            )
            return anchor_pb2.GazeConfigurationReply(
                accepted=True,
                configuration=self._configuration_message(configuration),
            )
        except (ValueError, OSError, RuntimeError) as error:
            return anchor_pb2.GazeConfigurationReply(accepted=False, error=str(error))

    async def StartGaze(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.GazeStatusReply()
        try:
            self._gaze.start()
            return anchor_pb2.GazeStatusReply(running=True)
        except Exception as error:
            return anchor_pb2.GazeStatusReply(running=False, error=str(error))

    async def ReadGaze(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.GazeSampleReply()
        sample = self._gaze.read()
        common = {
            "confidence": sample.confidence,
            "face_present": sample.face_present,
            "timestamp_unix_ms": sample.timestamp_ms,
            "yaw": sample.yaw,
            "pitch": sample.pitch,
            "roll": sample.roll,
            "preview_jpeg": sample.preview_jpeg,
        }
        if not sample.available:
            reason = "face not detected" if not sample.face_present else "gaze confidence too low"
            return anchor_pb2.GazeSampleReply(
                unavailable=anchor_pb2.Unavailable(reason=reason),
                **common,
            )
        return anchor_pb2.GazeSampleReply(
            point=anchor_pb2.GazePoint(x=sample.x, y=sample.y),
            **common,
        )

    async def AddCalibrationSample(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.CalibrationProgressReply()
        try:
            count = self._gaze.add_calibration_sample(request.target_x, request.target_y)
            return anchor_pb2.CalibrationProgressReply(accepted=True, sample_count=count)
        except (ValueError, RuntimeError) as error:
            return anchor_pb2.CalibrationProgressReply(accepted=False, error=str(error))

    async def FinishCalibration(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.CalibrationResultReply()
        try:
            model = self._gaze.finish_calibration(request.display_signature)
            return anchor_pb2.CalibrationResultReply(
                accepted=True,
                sample_count=model.sample_count,
                inlier_count=model.inlier_count,
                median_error=model.median_error,
            )
        except (ValueError, RuntimeError) as error:
            return anchor_pb2.CalibrationResultReply(accepted=False, error=str(error))

    async def StopGaze(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.GazeStatusReply()
        self._gaze.stop()
        return anchor_pb2.GazeStatusReply(running=False)

    async def Shutdown(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.ShutdownReply(accepted=False)
        self._gaze.stop()
        self._stop_event.set()
        return anchor_pb2.ShutdownReply(accepted=True)

    @staticmethod
    def _configuration_message(configuration: GazeConfiguration):
        return anchor_pb2.GazeConfigurationMessage(
            camera_index=configuration.camera_index,
            mirror=configuration.mirror,
            rotation_degrees=configuration.rotation_degrees,
            offset_x=configuration.offset_x,
            offset_y=configuration.offset_y,
            smoothing=configuration.smoothing,
            sensitivity=configuration.sensitivity,
            min_confidence=configuration.min_confidence,
        )


async def serve(port: int, token: str) -> None:
    if not token:
        raise ValueError("token is required")
    stop_event = asyncio.Event()
    server = grpc.aio.server(options=(("grpc.so_reuseport", 0),))
    anchor_pb2_grpc.add_InferenceWorkerServicer_to_server(
        WorkerService(token, stop_event), server
    )
    bound_port = server.add_insecure_port(f"127.0.0.1:{port}")
    if bound_port == 0:
        raise RuntimeError("failed to bind worker to loopback")
    await server.start()
    print(readiness_payload(bound_port, token), flush=True)
    await stop_event.wait()
    await server.stop(grace=1)
