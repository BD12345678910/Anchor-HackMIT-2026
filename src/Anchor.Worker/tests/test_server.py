import json

import pytest

from anchor_worker.server import (
    AuthenticationError,
    readiness_payload,
    require_token,
    worker_capabilities,
)


def test_token_authentication_accepts_only_exact_metadata_value():
    require_token((("x-anchor-token", "correct"),), "correct")

    with pytest.raises(AuthenticationError):
        require_token((("x-anchor-token", "wrong"),), "correct")


def test_readiness_payload_is_machine_readable_and_loopback_only():
    payload = json.loads(readiness_payload(port=43123, token="secret"))

    assert payload["status"] == "ready"
    assert payload["host"] == "127.0.0.1"
    assert payload["port"] == 43123
    assert payload["protocolVersion"] == 1
    assert "secret" not in payload


def test_optional_vision_dependencies_report_degraded_capability():
    capabilities = worker_capabilities(import_module=lambda _: (_ for _ in ()).throw(ImportError()))

    assert capabilities["camera"]["available"] is False
    assert capabilities["visual_analysis"]["available"] is False
