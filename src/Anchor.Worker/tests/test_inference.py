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


def test_repeated_scroll_loop_on_relevant_content_reports_stuck_phrase():
    model = AttentionInference(alpha=1)
    features = normalize_features(
        {
            "app_relevance": 0.9,
            "scroll_reversal_count": 9,
            "idle_seconds": 2,
        }
    )
    first = model.predict(features)
    result = model.predict(features)

    assert "stuck_phrase" not in first.reason_codes
    assert "stuck_phrase" in result.reason_codes


def test_missing_gaze_is_unavailable_not_neutral_center_evidence():
    features = normalize_features({})
    result = AttentionInference(alpha=1).predict(features)

    assert features.gaze_available is False
    assert features.gaze_presence == 0.0
    assert "gaze_absent" not in result.reason_codes


def test_gaze_away_requires_sustained_available_windows():
    model = AttentionInference(alpha=1)
    features = normalize_features(
        {
            "app_relevance": 0.8,
            "gaze_available": True,
            "gaze_presence": 0.0,
        }
    )

    first = model.predict(features)
    second = model.predict(features)
    sustained = model.predict(features)

    assert "gaze_away_sustained" not in first.reason_codes
    assert "gaze_away_sustained" not in second.reason_codes
    assert "gaze_away_sustained" in sustained.reason_codes
