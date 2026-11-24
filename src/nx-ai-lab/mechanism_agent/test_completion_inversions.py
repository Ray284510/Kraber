import unittest
import numpy as np
from .completion_inversions import design,pose
from .contract import validate_plan
class SliderChainInversionTests(unittest.TestCase):
    def test_distinct_ground_members_and_closed_loops(self):
        members=set()
        for kind in ['guide_bar','oscillating_block','fixed_block']:
            p=validate_plan(design(kind));members.add(p['grounded_member_and_roles']['fixed_original'])
            self.assertEqual(p['independent_calculation']['rank']['functional_dof'],1)
            self.assertEqual(sum(j['type']=='Slider' for j in p['joints']),1)
            for t in np.linspace(0,4,49):
                q=pose(kind,t);A,C,u=[np.array(q[k]) for k in ['A','C','u']]
                if kind=='fixed_block':
                    self.assertAlmostEqual(np.linalg.norm(A),80,places=10);self.assertAlmostEqual(np.linalg.norm(C-A),45,places=10);self.assertAlmostEqual(C[1],0)
                else:
                    self.assertAlmostEqual(np.linalg.norm(C-A),25,places=10);self.assertAlmostEqual(float(C[0]*u[1]-C[1]*u[0]),0,places=10)
            slider=next(j for j in p['joints'] if j['type']=='Slider')
            self.assertEqual(slider['second']=='Link0',kind=='fixed_block')
        self.assertEqual(members,{'L1','L2','L3'})
        self.assertLess(design('fixed_block')['independent_calculation']['max_pressure_angle_deg'],45)
