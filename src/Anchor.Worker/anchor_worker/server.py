from __future__ import annotations

import asyncio
from importlib import import_module
import json
import sys
from typing import Callable, Iterable

import grpc

from .features import normalize_features
from .health import detect_capabilities
from .inference import AttentionInference
from .generated import anchor_pb2

sys.modules.setdefault("anchor_pb2", anchor_pb2)
from .generated import anchor_pb2_grpc  # noqa: E402


PROTOCOL_VERSION = 1


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
    def __init__(self, token: str, stop_event: asyncio.Event) -> None:
        self._token = token
        self._stop_event = stop_event
        self._inference = AttentionInference()
        self._capabilities = worker_capabilities()

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

    async def Shutdown(self, request, context):
        if not await self._authenticate(context):
            return anchor_pb2.ShutdownReply(accepted=False)
        self._stop_event.set()
        return anchor_pb2.ShutdownReply(accepted=True)


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
