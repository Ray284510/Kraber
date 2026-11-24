import unittest
import numpy as np
from .synthesis import synthesize,normalized_cam_law

def specification():
    return dict(source_text='Offline unrelated lift experiment',interpretation=[{'field':'output_motion','reason':'A lifting carriage moves out and back'}],input_motion='continuous_rotation',output_motion='reciprocating_translation',stroke_mm=8.,period_s=2.,max_pressure_angle_deg=30.,objectives=['moving_links','footprint_area_mm2'],assumptions=['8 mm illustrative lift, not a user acceptance result'])

class SynthesisTests(unittest.TestCase):
    def test_unmapped_requirement_is_not_silently_ignored(self):
        s=specification();s['invented_requirement']=True
        with self.assertRaises(ValueError):synthesize(s)
    def test_fixed_stroke_cannot_be_claimed_adjustable(self):
        s=specification();s['adjustable_stroke']=True
        with self.assertRaises(ValueError):synthesize(s)
    def test_nonfinite_range_is_rejected_before_sizing(self):
        s=specification();s['stroke_range_mm']=[1,float('inf')]
        with self.assertRaises(ValueError):synthesize(s)
    def test_dwell_changes_mechanism_by_feasibility(self):
        s=specification();s.update(dwell_fraction=.25,motion_law='quintic');r=synthesize(s);self.assertEqual(r['selected'],'disk_cam');self.assertFalse(r['nx_acceptance']);self.assertTrue(all(not x['feasible'] for x in r['candidates'] if x['mechanism']!='disk_cam'))
    def test_exact_harmonic_can_select_yoke(self):
        s=specification();s['motion_law']='simple_harmonic';r=synthesize(s);self.assertEqual(r['selected'],'scotch_yoke')
    def test_low_pairs_reject_contact_candidates(self):
        s=specification();s['low_pair_only']=True;r=synthesize(s);self.assertEqual(r['selected'],'inline_slider_crank');self.assertLessEqual(next(x for x in r['candidates'] if x['feasible'])['metrics']['max_pressure_angle_deg'],30)
    def test_constant_speed_law_integrals(self):
        u=np.linspace(0,1,10001);s,v,a,_=normalized_cam_law(u,'constant_speed_segment');self.assertAlmostEqual(s[0],0);self.assertAlmostEqual(s[-1],1);self.assertLess(abs(np.trapezoid(v,u)-1),1e-8);self.assertAlmostEqual(v[4000],v[6000]);self.assertAlmostEqual(a[4000],0);self.assertAlmostEqual(a[0],0);self.assertAlmostEqual(a[-1],0)
    def test_infeasible_envelope_does_not_get_a_fake_winner(self):
        s=specification();s['maxima']={'footprint_area_mm2':1};self.assertIsNone(synthesize(s)['selected'])

    def test_coaxial_rotary_proposal_uses_willis(self):
        s=specification();s.update(output_motion='continuous_rotation',signed_speed_ratio=.25,module_mm=1.,coaxial=True,objectives=['ratio_relative_error','footprint_area_mm2']);r=synthesize(s);self.assertEqual(r['selected'],'planetary_gears');p=r['selected_parameters'];self.assertEqual(p['sun_teeth']+2*p['planet_teeth'],p['ring_teeth']);self.assertEqual((p['sun_teeth']+p['ring_teeth'])%3,0)

if __name__=='__main__':unittest.main()
