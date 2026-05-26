from evaluate import _elo_update, _expected


def test_expected_score_symmetric():
    assert abs(_expected(1000, 1000) - 0.5) < 1e-9
    assert _expected(1200, 1000) > 0.5
    assert _expected(800, 1000) < 0.5


def test_elo_winner_gains():
    a, b = _elo_update(1000, 1000, 1.0)
    assert a > 1000 and b < 1000
    assert abs((a - 1000) + (b - 1000)) < 1e-6   # zero-sum

    a, b = _elo_update(1000, 1000, 0.5)
    assert abs(a - 1000) < 1e-6
