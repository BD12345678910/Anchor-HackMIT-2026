import math

from anchor_worker.features import normalize_features


def test_malformed_values_are_sanitized():
    result = normalize_features(
        {
            "idle_seconds": float("nan"),
            "mouse_distance": -4,
            "app_relevance": 3,
            "gaze_presence": -2,
            "key_count": -7,
        }
    )

    assert result.idle_seconds == 0
    assert result.mouse_distance == 0
    assert result.app_relevance == 1
    assert result.gaze_presence == 0
    assert result.key_count == 0
    assert all(math.isfinite(value) for value in result.numeric_values())


def test_unknown_feature_names_are_ignored():
    result = normalize_features({"app_relevance": 0.7, "raw_key": "A"})

    assert result.app_relevance == 0.7
    assert not hasattr(result, "raw_key")
