from anchor_worker.features import normalize_features
from anchor_worker.inference import AttentionInference


def test_predict_is_bounded_and_explainable():
    model = AttentionInference(alpha=0.5)
    result = model.predict(
        normalize_features(
            {
                "idle_seconds": 14,
                "app_relevance": 0.05,
                "app_switch_count": 4,
                "scroll_reversal_count": 5,
            }
        )
    )

    assert 0.0 <= result.distraction_probability <= 1.0
    assert 0.0 <= result.confidence <= 1.0
    assert result.reason_codes


def test_temporal_smoothing_limits_a_single_spike():
    model = AttentionInference(alpha=0.25)
    focused = normalize_features({"app_relevance": 1, "key_count": 10})
    distracted = normalize_features(
        {"app_relevance": 0, "idle_seconds": 20, "app_switch_count": 5}
    )
    model.predict(focused)

    spike = model.predict(distracted)
    sustained = [model.predict(distracted) for _ in range(5)][-1]

    assert spike.distraction_probability < sustained.distraction_probability


def test_manual_report_bypasses_inference_thresholds():
    model = AttentionInference()
    result = model.predict(
        normalize_features(
            {"app_relevance": 1, "key_count": 10, "manual_report": True}
        )
    )

    assert result.distraction_probability == 1
    assert result.confidence == 1
    assert "manual_report" in result.reason_codes
