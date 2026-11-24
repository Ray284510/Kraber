import unittest
from .feeder_plan import design
from .contract import validate_plan


class FeederChecks(unittest.TestCase):
    def test_adjustment_range_preserves_inline_equal_strokes(self):
        for r in (15,20,25):
            p=design(r);validate_plan(p);self.assertEqual(p['selection']['selected'],'inline_slider_crank');self.assertAlmostEqual(p['independent_calculation']['stroke_mm'],2*r);self.assertEqual(p['independent_calculation']['quick_return_ratio'],1)
            self.assertLess(p['independent_calculation']['maximum_pressure_angle_deg'],14.48)
        with self.assertRaises(ValueError): design(30)


if __name__=='__main__':unittest.main()
