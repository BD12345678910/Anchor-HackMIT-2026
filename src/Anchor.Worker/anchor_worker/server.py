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
from .recording import MssScreenCapture, StudyRecorder
from .generated import anchor_pb2

sys.modules.setdefault("anchor_pb2", anchor_pb2)
from .generated import anchor_pb2_grpc  # noqa: E402


PROTOCOL_VERSION = 3


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
        recording_factory: Callable[..., StudyRecorder] | None = None,
    ) -> None:
        self._token = token
        self._stop_event = stop_event
        self._inference = AttentionInference()
        self._capabilities = worker_capabilities()
        self._gaze = gaze_controller or GazeController()
        self._recording_factory = recording_factory or StudyRecorder
        self._recorder: StudyRecorder | None = None

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
            return anchor_pb2.GazeStatusReply(running=True, calibrated=self._gaze.is_calibrated)
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
            "calibrated": self._gaze.is_calibrated,
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
            return anchor_pb2.CalibrationProgressReply(
                accepted=True,
                sample_count=count,
                target_count=self._gaze.calibration_target_count,
            )
        except (ValueError, RuntimeError) as error:
            return anchor_pb2.CalibrationProgressReply(
                accepted=False,
                error=str(error),
                target_count=self._gaze.calibration_target_count,
            )

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
                mean_error=model.mean_error,
                max_error=model.max_error,
                target_count=model.target_count,
                target_errors=[
                    anchor_pb2.CalibrationTargetError(
                        target_x=entry.target[0],
                        target_y=entry.target[1],
                        predicted_x=entry.predicted[0],
                        predicted_y=entry.predicted[1],
                        error=entry.error,
                    )
                    for entry in model.target_errors
                ],
            )
        except (ValueError, RuntimeError, IndexError) as error:
            return anchor_pb2.CalibrationResultReply(accepted=False, error=str(error))

    async def ResetCalibration(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.CalibrationProgressReply()
        self._gaze.reset_calibration()
        return anchor_pb2.CalibrationProgressReply(accepted=True)

    async def StartWebcamRecording(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.WebcamRecordingReply()
        try:
            status = self._gaze.start_webcam_recording(request.output_directory)
            return self._webcam_message(status, accepted=True)
        except (ValueError, RuntimeError, OSError) as error:
            return anchor_pb2.WebcamRecordingReply(accepted=False, error=str(error))

    async def GetWebcamRecording(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.WebcamRecordingReply()
        return self._webcam_message(self._gaze.webcam_recording_status(), accepted=True)

    async def StopWebcamRecording(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.WebcamRecordingReply()
        return self._webcam_message(self._gaze.stop_webcam_recording(), accepted=True)

    @staticmethod
    def _webcam_message(status, *, accepted: bool):
        return anchor_pb2.WebcamRecordingReply(
            accepted=accepted,
            recording=status.recording,
            video_path=status.video_path,
            frame_count=status.frame_count,
            elapsed_seconds=status.elapsed_seconds,
            error=status.error,
        )

    async def StopGaze(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.GazeStatusReply()
        self._gaze.stop()
        return anchor_pb2.GazeStatusReply(running=False)

    async def StartRecording(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.RecordingManifestReply()
        if self._recorder is not None and self._recorder.status.status == "recording":
            return anchor_pb2.RecordingManifestReply(accepted=False, error="recording_already_active")
        try:
            capture = MssScreenCapture(max(1, request.display_index or 1))
            self._recorder = self._recording_factory(
                fps=max(1, min(60, request.fps or 15)),
                capture=capture,
            )
            manifest = self._recorder.start(
                request.output_directory,
                trial_mode=request.trial_mode,
                participant_code=request.participant_code,
            )
            return anchor_pb2.RecordingManifestReply(
                accepted=True,
                trial_id=manifest.trial_id,
                trial_mode=manifest.trial_mode,
                video_path=str(manifest.video_path),
                events_path=str(manifest.events_path),
                samples_path=str(manifest.samples_path),
                summary_path=str(manifest.summary_path),
                manifest_path=str(manifest.manifest_path),
            )
        except Exception as error:
            self._recorder = None
            return anchor_pb2.RecordingManifestReply(accepted=False, error=str(error))

    async def AppendRecordingEvent(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.RecordingStatusReply()
        if self._recorder is None:
            return anchor_pb2.RecordingStatusReply(status="idle", error_code="recording_not_active")
        try:
            self._recorder.append_event(self._parse_recording_json(request.json))
        except (ValueError, RuntimeError, json.JSONDecodeError) as error:
            return self._status_message(error_code=str(error))
        return self._status_message()

    async def AppendRecordingSample(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.RecordingStatusReply()
        if self._recorder is None:
            return anchor_pb2.RecordingStatusReply(status="idle", error_code="recording_not_active")
        try:
            self._recorder.append_sample(self._parse_recording_json(request.json))
        except (ValueError, RuntimeError, json.JSONDecodeError) as error:
            return self._status_message(error_code=str(error))
        return self._status_message()

    async def GetRecordingStatus(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.RecordingStatusReply()
        return self._status_message()

    async def StopRecording(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.RecordingStatusReply()
        if self._recorder is None:
            return anchor_pb2.RecordingStatusReply(status="idle")
        return self._status_message(self._recorder.stop())

    async def Shutdown(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.ShutdownReply(accepted=False)
        self._gaze.stop()
        if self._recorder is not None:
            self._recorder.stop()
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

    @staticmethod
    def _parse_recording_json(value: str) -> dict[str, object]:
        if len(value.encode("utf-8")) > 65_536:
            raise ValueError("recording payload too large")
        item = json.loads(value)
        if not isinstance(item, dict):
            raise ValueError("recording payload must be an object")
        return item

    def _status_message(self, status=None, error_code: str = ""):
        if self._recorder is None:
            return anchor_pb2.RecordingStatusReply(status="idle", error_code=error_code)
        current = status or self._recorder.status
        return anchor_pb2.RecordingStatusReply(
            trial_id=current.trial_id,
            status=current.status,
            frame_count=current.frame_count,
            dropped_frames=current.dropped_frames,
            elapsed_seconds=current.elapsed_seconds,
            video_usable=current.video_usable,
            error_code=error_code or current.error_code or "",
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
