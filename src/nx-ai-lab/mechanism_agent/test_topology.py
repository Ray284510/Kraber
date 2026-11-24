import unittest
from .topology import analyze
class TopologyTests(unittest.TestCase):
    def test_compound_hinge_counts_n_minus_one_pairs(self):
        joints=[{'name':'a','type':'Revolute','first':'A','second':'Link0','origin':[0,0,0],'axis':[0,0,1]},{'name':'b','type':'Revolute','first':'B','second':'A','origin':[0,0,20],'axis':[0,0,-1]}]
        r=analyze(joints);self.assertEqual(r['compound_hinge_clusters'][0]['equivalent_independent_revolute_pairs'],2);self.assertEqual(r['independent_spatial_rank']['instantaneous_dof'],2)
        joints[1]['origin']=[5,0,20];self.assertFalse(analyze(joints)['compound_hinge_clusters'])
