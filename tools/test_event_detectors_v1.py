"""Synthetic contract tests; not a historical validation or profitability test."""
import unittest
from dataclasses import replace
from event_detectors_v1 import Bar, cross, detect


class DetectorTests(unittest.TestCase):
    def setUp(self):
        self.previous = Bar(109, 112, 108, 110, {20: 108, 50: 105, 100: 100, 200: 90})
        self.current = Bar(110, 112, 108, 111, {20: 109, 50: 106, 100: 101, 200: 91})

    def test_order_is_separate_from_price(self):
        row = detect(replace(self.current, open=100, high=101, low=98, close=99))
        self.assertTrue(row["T01.order"])
        self.assertFalse(row["T01.price_above_20"])

    def test_one_bar_slopes(self):
        row = detect(self.current, self.previous)
        self.assertTrue(row["T02.all_rising"])
        self.assertEqual(row["T02.slope_200"], 1)

    def test_cross_uses_each_days_level(self):
        self.assertFalse(cross(99, 100, 97, 98, up=False))
        self.assertTrue(cross(100, 100, 97, 98, up=False))

    def test_equal_is_not_strict_cross(self):
        self.assertFalse(cross(99, 100, 100, 100, up=True))
        self.assertTrue(cross(100, 100, 101, 100, up=True))

    def test_double_break(self):
        row = detect(replace(self.current, open=110, high=111, low=102, close=103), self.previous)
        self.assertTrue(row["E03.double_break"])

    def test_wick_not_close_break(self):
        row = detect(self.current, self.previous)
        self.assertTrue(row["E02.wick_hold_20"])
        self.assertFalse(row["E01.cross_down_20"])

    def test_missing_is_unknown(self):
        row = detect(Bar())
        self.assertIsNone(row["E03.double_break"])
        self.assertIsNone(row["R08.both_leave_upper"])
        self.assertIsNone(row["P03.atrp1"])
        self.assertIsNone(row["T02.all_rising"])

    def test_missing_previous_does_not_create_rsi_event(self):
        row = detect(replace(self.current, rsi={"slow": -0.9}))
        self.assertIsNone(row["R03.slow"])
        self.assertFalse(row["R01.slow"])

    def test_rsi_release_and_no_repeated_event(self):
        low = replace(self.previous, rsi={"slow": -1, "fast": -1})
        released = replace(self.current, rsi={"slow": -0.998, "fast": -0.99})
        self.assertTrue(detect(released, low)["R03.slow"])
        self.assertFalse(detect(released, released)["R03.slow"])

    def test_breadth_candle_differs_from_daily_change(self):
        p = replace(self.previous, breadth_close={"B50": 50})
        c = replace(self.current, breadth_close={"B50": 51}, breadth_open={"B50": 52}, breadth_ohlc_verified=True)
        row = detect(c, p)
        self.assertTrue(row["B04.rising_B50"])
        self.assertFalse(row["B05.green_B50"])
        self.assertEqual(row["B04.delta_pp_B50"], 1)

    def test_synthetic_breadth_open_not_accepted(self):
        c = replace(self.current, breadth_close={"B50": 51}, breadth_open={"B50": 50})
        self.assertIsNone(detect(c)["B05.green_B50"])

    def test_atrp_includes_gap_but_body_does_not(self):
        c = Bar(open=120, high=122, low=119, close=121)
        row = detect(c, self.previous)
        self.assertEqual(row["P03.true_range"], 12)
        self.assertAlmostEqual(row["P03.atrp1"], 1200 / 121)
        self.assertAlmostEqual(row["P01.body_pct"], 100 / 120)

    def test_invalid_values_are_not_silently_replaced(self):
        for c in (Bar(close=0), Bar(rsi={"slow": 1.1}), Bar(breadth_close={"BN": float("nan")}), Bar(5, 4, 3, 5)):
            with self.subTest(c=c), self.assertRaises(ValueError):
                detect(c)

    def test_future_append_cannot_change_prior_outputs(self):
        def replay(bars):
            return [detect(b, bars[i-1] if i else None) for i, b in enumerate(bars)]
        prefix = [self.previous, self.current]
        result = replay(prefix)
        future = Bar(50, 55, 45, 51, {20: 60, 50: 70, 100: 80, 200: 90})
        self.assertEqual(result, replay(prefix + [future])[:2])


if __name__ == "__main__":
    unittest.main(verbosity=2)
