import math,unittest
from .completion_groove import law,design
from .cam_plan import law as cycloid
class QuinticTests(unittest.TestCase):
    def test_continuous_end_velocities_and_accelerations(self):
        for t in [0,2*math.pi/3,math.pi,5*math.pi/3,2*math.pi]:
            a,b=law(t-1e-8),law(t+1e-8)
            for x,y in zip(a,b):self.assertLess(abs(x-y),1e-5)
        self.assertNotAlmostEqual(law(math.pi/6)[0],cycloid(math.pi/6)[0],places=3)
    def test_design_curvature_and_actual_dwell(self):
        d=design();self.assertGreater(d['independent_calculation']['min_pitch_curvature_radius_mm'],5.2)
        self.assertEqual(law(5*math.pi/6),(12,0,0));self.assertEqual(law(11*math.pi/6),(0,0,0))
