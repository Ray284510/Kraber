import unittest
import numpy as np
from .delta_plan import design, forward, joints_at
from .calculations import constraint_rank


class DeltaChecks(unittest.TestCase):
    def test_symmetry_closure_mobility_and_perturbation(self):
        p=design();self.assertEqual(p['independent_calculation']['rank']['functional_dof'],3)
        np.testing.assert_allclose(p['initial']['platform'][:2],[0,0],atol=1e-12)
        for q in ([20,30,40],[38,26,36],[45,45,45]):
            f=forward(q);self.assertLess(f['sphere_closure_max_mm'],1e-10)
            self.assertEqual(constraint_rank(p['rigid_links'],joints_at(f),'Link0')['functional_dof'],3)
        f=forward([30,30,30]);g=forward([30.001,30,30]);self.assertGreater(np.linalg.norm(np.array(g['platform'])-f['platform']),1e-5)


if __name__=='__main__':unittest.main()
