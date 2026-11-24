import unittest
from .test_synthesis import specification
from .realization import realize
from .synthesized_feeder import plan_from_selection

class RealizationTests(unittest.TestCase):
    def test_small_crank_pin_head_clears_bearing_over_full_rotation(self):
        # Regression from the actual NX PIN_A/GROUND interference, not an acceptance substitute.
        import numpy as np
        for stroke in (20,24,36,80):
            s=specification();s.update(stroke_mm=stroke,low_pair_only=True)
            p=plan_from_selection(realize(s))['parameters'];theta=np.linspace(0,2*np.pi,361)
            centers=p['crank_radius']*np.array([np.cos(theta),np.sin(theta)]).T
            gap=np.linalg.norm(centers,axis=1)-5-p['bearing_diameter']/2
            self.assertGreaterEqual(min(gap),.4-1e-10)
            self.assertGreaterEqual((p['bearing_diameter']-6.4)/2,1.4-1e-10)
    def test_real_guide_envelope_recomputes_rod_and_pressure(self):
        s=specification();s.update(stroke_mm=24,low_pair_only=True)
        r=realize(s);self.assertEqual(r['selected'],'inline_slider_crank');p=plan_from_selection(r)
        self.assertGreater(p['parameters']['rod_length'],2*12+25.4)
        row=next(c for c in r['candidates'] if c['feasible']);self.assertLess(row['metrics']['max_pressure_angle_deg'],row['theoretical_metrics']['max_pressure_angle_deg']);self.assertFalse(r['nx_acceptance'])
    def test_same_objective_can_choose_yoke_with_supported_hardware(self):
        s=specification();s.update(stroke_mm=24,motion_law='simple_harmonic');r=realize(s);self.assertEqual(r['selected'],'scotch_yoke');self.assertEqual(next(c for c in r['candidates'] if c['mechanism']=='scotch_yoke')['realization']['status'],'dimensioned_requires_NX')
    def test_dwell_requires_a_dimensioned_cam(self):
        s=specification();s.update(dwell_fraction=.25,motion_law='quintic');r=realize(s);self.assertEqual(r['selected'],'disk_cam');c=next(c for c in r['candidates'] if c['mechanism']=='disk_cam');self.assertGreaterEqual(c['realization']['adjustments']['final_roller_radius_mm'],3.5)
    def test_packaging_rejects_small_hardware_instead_of_scaling_a_passed_file(self):
        s=specification();s.update(stroke_mm=2,low_pair_only=True);r=realize(s);self.assertIsNone(r['selected']);self.assertFalse(r['nx_acceptance'])

if __name__=='__main__':unittest.main()
