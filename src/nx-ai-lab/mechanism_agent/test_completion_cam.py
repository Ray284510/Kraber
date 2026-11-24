import unittest,math
import numpy as np
from .completion_cam import KINDS,pose,design,P,R,E,ARM

class ExactCamTests(unittest.TestCase):
    def test_contact_geometry_and_pressure(self):
        for kind in KINDS:
            self.assertLess(design(kind)['independent_calculation']['max_pressure_angle_deg'],45)
            for t in np.linspace(0,4,121):
                p=pose(kind,t);q=np.array(p['point']);center=np.array(p['center'])
                self.assertAlmostEqual(np.linalg.norm(q-center),R+(6 if 'roller' in kind else 0),places=10)
                if 'oscillating' in kind:self.assertAlmostEqual(np.linalg.norm(q-P),ARM,places=10)
                else:self.assertEqual(q[0],0)
    def test_translating_derivatives_against_central_differences(self):
        for kind in KINDS[:2]:
            for t in [.13,.7,1.9,3.2]:
                h=1e-4;p=pose(kind,t);a=pose(kind,t-h)['point'][1];b=pose(kind,t+h)['point'][1];y=p['point'][1]
                self.assertAlmostEqual((b-a)/(2*h),p['velocity_mm_s'],places=6)
                self.assertAlmostEqual((a+b-2*y)/h**2,p['acceleration_mm_s2'],places=4)
