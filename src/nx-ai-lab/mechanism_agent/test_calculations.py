import unittest,math
import numpy as np
from .calculations import *
from .selection import interpret,select
from .fourbar_plan import design
class Checks(unittest.TestCase):
 def test_offset_slider_extrema_against_independent_sweep(self):
  for e in (-12.,12.):
   t=np.linspace(0,2*np.pi,100001);r=slider_crank(20,100,t,e);x=np.asarray(r['x'])
   np.testing.assert_allclose([min(x),max(x)],r['position_extremes_mm'],atol=1e-7)
   self.assertGreater(r['quick_return_ratio'],1)
   for deg in r['dead_centers_deg']:
    theta=math.radians(deg);h=1e-6;near=slider_crank(20,100,np.array([theta-h,theta+h]),e)['x'];self.assertLess(abs((near[1]-near[0])/(2*h)),1e-6)
 def test_planetary_redundant_meshes(self):
  r=planetary_mesh_rank(40,20,80,3);self.assertEqual((r['rank'],r['redundant_equations'],r['functional_dof']),(4,2,1))
  # Carrier 30, sun relative 60, each planet relative -120 degrees/s.
  np.testing.assert_allclose(np.array(r['matrix'])@np.array([30,60,-120,-120,-120]),0)
  self.assertGreater(np.linalg.norm(np.array(r['matrix'])@np.array([30,60,120,-120,-120])),0)
 def test_cam_envelope_and_boundary_continuity(self):
  from .cam_plan import law,profile
  for t in [0,2*math.pi/3,math.pi,5*math.pi/3,2*math.pi]:
   self.assertLess(np.linalg.norm(np.array(law(t-1e-7))-law(t+1e-7)),1e-4)
  for t in np.linspace(0,2*np.pi,101):
   s,v,_=law(t);pitch=np.array([(40+s)*math.sin(t),(40+s)*math.cos(t)]);normal=pitch-np.array(profile(t));self.assertAlmostEqual(np.linalg.norm(normal),5,places=10)
   tangent=np.array([v*math.sin(t)+(40+s)*math.cos(t),v*math.cos(t)-(40+s)*math.sin(t)]);self.assertLess(abs(normal@tangent),1e-10)
 def test_known_mobility_and_compound(self):
  self.assertEqual(planar_mobility(3,4)['functional'],1)
  self.assertEqual(len(compound_revolute_pairs(['a','b','c'])),2)
  self.assertEqual(planar_mobility(3,3,1,local_dof=1)['functional'],1)
 def test_spatial_redundancy(self):
  r=design()['rank_analysis'];self.assertEqual((r['coordinates'],r['constraint_rows'],r['rank'],r['redundant_rows'],r['functional_dof']),(18,20,17,3,1))
 def test_classification(self):
  self.assertEqual(classify_fourbar([100,25,90,70])['type'],'crank_rocker')
  self.assertEqual(classify_fourbar([25,100,90,70])['type'],'double_crank')
  self.assertEqual(classify_fourbar([100,90,25,70])['type'],'double_rocker')
  with self.assertRaises(ValueError):classify_fourbar([100,1,2,3])
 def test_closure_and_limits(self):
  for theta in np.linspace(0,2*np.pi,200):
   p=fourbar_pose(100,25,90,70,theta);a=np.array(p['A']);c=np.array(p['C']);self.assertAlmostEqual(np.linalg.norm(a-c),90,places=10);self.assertAlmostEqual(np.linalg.norm(c-[100,0]),70,places=10)
  for deg in fourbar_metrics(100,25,90,70)['dead_center_input_deg']:
   p=fourbar_pose(100,25,90,70,math.radians(deg));self.assertLess(abs(math.sin(p['coupler_angle']-math.radians(deg))),1e-10)
 def test_inline_slider_and_cycloid(self):
  q=slider_crank(20,100,np.array([0,np.pi/2,np.pi]));self.assertAlmostEqual(q['x'][0],120);self.assertAlmostEqual(q['x'][2],80);self.assertEqual(q['quick_return_ratio'],1)
  s,v,a=cycloidal([0,1]);np.testing.assert_allclose(s,[0,1]);np.testing.assert_allclose(v,[0,0],atol=1e-14);np.testing.assert_allclose(a,[0,0],atol=1e-14)
 def test_selection_does_not_force_arms_or_gears(self):
  self.assertEqual(select(interpret('连续旋转变为间歇旋转'))['selected'],'geneva')
  self.assertEqual(select(interpret('往复直线，可调行程，尽量减小急回'))['selected'],'inline_slider_crank')
  self.assertEqual(select(interpret('凸轮升降，需要停歇'))['selected'],'disk_cam')
  self.assertEqual(planetary_ratio(20,20,60)['fixed_ring_sun_to_carrier_ratio'],4)
if __name__=='__main__':unittest.main()
