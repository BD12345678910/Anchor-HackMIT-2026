import pytest

from anchor_worker.calibration import (
    CalibrationInvalidatedError,
    CalibrationModel,
    CalibrationSample,
)


def test_calibration_fits_mapping_and_rejects_single_outlier():
    samples = [
        CalibrationSample((0.0, 0.0, 0.0, 0.0), (0.10, 0.05)),
        CalibrationSample((0.5, 0.0, 0.0, 0.0), (0.50, 0.10)),
        CalibrationSample((1.0, 0.0, 0.0, 0.0), (0.90, 0.15)),
        CalibrationSample((0.0, 0.5, 0.0, 0.0), (0.125, 0.475)),
        CalibrationSample((0.5, 0.5, 0.0, 0.0), (0.525, 0.525)),
        CalibrationSample((1.0, 0.5, 0.0, 0.0), (0.925, 0.575)),
        CalibrationSample((0.0, 1.0, 0.0, 0.0), (0.15, 0.90)),
        CalibrationSample((0.5, 1.0, 0.0, 0.0), (0.55, 0.95)),
        CalibrationSample((1.0, 1.0, 0.0, 0.0), (0.95, 1.00)),
        CalibrationSample((0.5, 0.5, 0.0, 0.0), (0.99, 0.01)),
    ]

    model = CalibrationModel.fit(samples, display_signature="1920x1080@100")
    predicted = model.apply((0.25, 0.75, 0.0, 0.0), "1920x1080@100")

    assert model.inlier_count == 9
    assert model.sample_count == 10
    assert model.median_error < 0.01
    assert predicted[0] == pytest.approx(0.3375, abs=0.015)
    assert predicted[1] == pytest.approx(0.7125, abs=0.015)


def test_calibration_requires_nine_samples():
    samples = [CalibrationSample((0.5, 0.5, 0.0, 0.0), (0.5, 0.5))] * 8

    with pytest.raises(ValueError, match="nine"):
        CalibrationModel.fit(samples, display_signature="1920x1080@100")


def test_calibration_is_invalidated_when_display_geometry_changes():
    samples = [
        CalibrationSample((x, y, 0.0, 0.0), (x, y))
        for x, y in (
            (0.0, 0.0),
            (0.5, 0.0),
            (1.0, 0.0),
            (0.0, 0.5),
            (0.5, 0.5),
            (1.0, 0.5),
            (0.0, 1.0),
            (0.5, 1.0),
            (1.0, 1.0),
        )
    ]
    model = CalibrationModel.fit(samples, display_signature="1920x1080@100")

    with pytest.raises(CalibrationInvalidatedError, match="display"):
        model.apply((0.5, 0.5, 0.0, 0.0), "2560x1440@125")

