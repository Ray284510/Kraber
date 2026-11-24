import copy
import unittest
from .contract import validate_plan
from .fourbar_plan import design


class PlanChecks(unittest.TestCase):
    def test_physical_plan_and_rejected_nonfinite_or_disconnected_joint(self):
        plan = design(); self.assertIs(validate_plan(plan), plan)
        for change in ('nan', 'zero_axis', 'same_link', 'duplicate'):
            p = copy.deepcopy(plan)
            if change == 'nan': p['parameters']['crank'] = float('nan')
            if change == 'zero_axis': p['joints'][0]['axis'] = [0, 0, 0]
            if change == 'same_link': p['joints'][0]['second'] = p['joints'][0]['first']
            if change == 'duplicate': p['joints'][1]['name'] = p['joints'][0]['name']
            with self.assertRaises(ValueError): validate_plan(p)


if __name__ == '__main__': unittest.main()
