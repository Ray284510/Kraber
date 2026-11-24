"""Synthetic adapter regression only; not NX geometry acceptance."""
import copy
import unittest
from ai_studio.geometry.trim_boundary_reference import ordered_loop_inventory,periodic_uv_residual,nearest_recorded_samples

def fixture():
    root=dict(id=1,type=1,tag=100,child_index=-1,orientation=0)
    loop=dict(id=2,type=6,tag=0,child_index=0,orientation=0)
    rows=[dict(kind='topology_use',path=[root],face=20,children=1),dict(kind='topology_use',path=[root,loop],face=20,children=2)]
    for i,edge_id in enumerate([99,3]):
        f=dict(id=10+i,type=7,tag=0,child_index=i,orientation=1)
        e=dict(id=edge_id,type=8,tag=edge_id,child_index=0,orientation=1)
        rows.extend([dict(kind='topology_use',path=[root,loop,f],face=20,children=1),dict(kind='topology_use',path=[root,loop,f,e],face=20,children=0)])
    return rows

class ReferenceTests(unittest.TestCase):
    def test_native_child_order_not_tag_order(self):
        rows=fixture();result=ordered_loop_inventory(list(reversed(rows)))
        self.assertEqual([u['edge_id'] for u in result[0]['ordered_uses']],[99,3])
        self.assertEqual(result[0]['geometric_closure'],'NOT_PROVEN')
    def test_missing_child_stops(self):
        with self.assertRaises(ValueError):ordered_loop_inventory(fixture()[:-1])
    def test_orientation_conflict_stops(self):
        rows=fixture();rows[3]=copy.deepcopy(rows[3]);rows[3]['path'][-1]['orientation']=-1
        with self.assertRaises(ValueError):ordered_loop_inventory(rows)
    def test_duplicate_path_stops(self):
        rows=fixture()
        with self.assertRaises(ValueError):ordered_loop_inventory(rows+[rows[3]])
    def test_repeated_edge_distinct_uses_retained(self):
        rows=fixture();rows[5]['path'][-1]['id']=99;rows[5]['path'][-1]['tag']=99
        self.assertEqual(len(ordered_loop_inventory(rows)[0]['ordered_uses']),2)
    def test_singular_loop_not_invented(self):
        rows=fixture();rows[2]['path'][-1]['type']=9
        with self.assertRaises(ValueError):ordered_loop_inventory(rows)
    def test_periodic_chart_shift_retained(self):
        r=periodic_uv_residual([1.25,.2],[.25,.2],[1,0])
        self.assertEqual(r,dict(residual=0.0,shifts=[1,0]))
    def test_nearby_sample_does_not_form_curve_map(self):
        r=nearest_recorded_samples([0,0,0],[dict(xyz=[-1,0,0],id=1),dict(xyz=[1,0,0],id=2)])
        self.assertEqual(len(r['candidates']),2);self.assertIsNone(r['curve_parameter_map'])
        self.assertFalse(r['global_minimum_proven'])
    def test_empty_cloud_unresolved(self):
        r=nearest_recorded_samples([0,0,0],[])
        self.assertEqual(r['status'],'NO_RECORDED_TARGET');self.assertIsNone(r['curve_parameter_map'])
    def test_geometry_and_use_sense_stay_separate(self):
        use=ordered_loop_inventory(fixture())[0]['ordered_uses'][0]
        self.assertEqual(use['traversal_orientation'],1)
        self.assertIsNone(use['geometric_sense']);self.assertIsNone(use['native_use_interval'])

if __name__=='__main__':unittest.main()
