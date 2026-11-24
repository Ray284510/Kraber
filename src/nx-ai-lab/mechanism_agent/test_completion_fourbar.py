import unittest
import numpy as np
from .completion_fourbar import design,pose
from .calculations import fourbar_metrics
class FourRInversionTests(unittest.TestCase):
    def test_closure_and_ground_choice(self):
        for kind in ['double_crank','double_rocker']:
            p=design(kind);d,r,l,s=p['parameters']['lengths_ground_input_coupler_output']
            self.assertEqual(p['independent_calculation']['classification']['type'],kind)
            for angle in np.linspace(0,360,97):
                q=pose(kind,angle);O,A,C,D=[np.array(q[k]) for k in ['O','A','C','D']]
                np.testing.assert_allclose([np.linalg.norm(D-O),np.linalg.norm(A-O),np.linalg.norm(C-A),np.linalg.norm(C-D)],[d,r,l,s],atol=1e-10)
            self.assertEqual(p['independent_calculation']['rank']['functional_dof'],1)
        self.assertIsNone(design('double_crank')['independent_calculation']['quick_return_ratio'])
        self.assertEqual(design('double_rocker')['drive']['joint'],'J1')
    def test_general_calculator_does_not_invent_quick_return_for_rotation(self):
        self.assertIsNone(fourbar_metrics(40,90,70,100)['quick_return_ratio'])
        rocker=fourbar_metrics(100,90,25,70)
        self.assertFalse(rocker['grounded_input_full_rotation']);self.assertIsNone(rocker['quick_return_ratio'])
        lower,upper=rocker['feasible_grounded_input_intervals_deg'][0]
        self.assertAlmostEqual(upper-lower,design('double_rocker')['independent_calculation']['grounded_input_range_deg'],places=4)
