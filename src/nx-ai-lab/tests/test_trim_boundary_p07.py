"""P07 new cases only; does not rerun the ten P06 cases."""
import unittest,json
from pathlib import Path
from ai_studio.geometry.trim_boundary_reference import edge_use_orientation,periodic_uv_residual,ordered_loop_inventory
class P07Tests(unittest.TestCase):
 def test_four_sign_combinations(self):
  for f,e in [(1,1),(1,-1),(-1,1),(-1,-1)]:
   with self.subTest(fin=f,edge=e):
    r=edge_use_orientation(f,e);self.assertEqual(r['raw'],dict(loop_to_fin=f,fin_to_edge=e))
    self.assertEqual(r['traversal'],f if f==e else None)
    self.assertEqual(r['status'],'AGREEMENT_NOT_MULTIPLICATION' if f==e else 'INCONSISTENT_PUBLIC_CONTRACT')
 def test_none_slots(self):
  self.assertEqual(edge_use_orientation(0,-1)['traversal'],-1)
  self.assertEqual(edge_use_orientation(1,0)['traversal'],1)
  self.assertEqual(edge_use_orientation(0,0)['status'],'UNKNOWN')
 def test_half_period(self):
  r=periodic_uv_residual([.5,0],[0,0],[1,0]);self.assertIsNone(r['shifts'])
  self.assertEqual({tuple(x['shifts']) for x in r['candidates']},{(0,0),(1,0)})
 def test_negative_half_period(self):
  r=periodic_uv_residual([-.5,0],[0,0],[1,0])
  self.assertEqual({tuple(x['shifts']) for x in r['candidates']},{(-1,0),(0,0)})
 def test_near_half(self):
  self.assertEqual(periodic_uv_residual([.500000000000001],[0],[1])['shifts'],[1])
  self.assertEqual(periodic_uv_residual([.499999999999999],[0],[1])['shifts'],[0])
 def test_double_period_ties(self):
  r=periodic_uv_residual([.5,1],[0,0],[1,2])
  self.assertEqual({tuple(x['shifts']) for x in r['candidates']},{(0,0),(1,0),(0,1),(1,1)})
 def test_eight_archived_native_paths(self):
  p=Path(__file__).resolve().parent/'fixtures/p04_native_topology_paths.json'
  loops=ordered_loop_inventory(json.loads(p.read_text())['records']);self.assertEqual(len(loops),8)
  directions=[]
  for l in loops:
   for u in l['ordered_uses']:
    self.assertEqual(u['raw_orientations']['loop_to_fin'],u['raw_orientations']['fin_to_edge'])
    self.assertEqual(u['orientation_composition'],'AGREEMENT_NOT_MULTIPLICATION');directions.append(u['traversal_orientation'])
  self.assertEqual(sorted(directions),[-1]*4+[1]*4)
if __name__=='__main__':unittest.main()
