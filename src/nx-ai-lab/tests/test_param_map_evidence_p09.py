"""P09 semantic tests only. No NX fixtures, roots or historical experiments."""
import unittest
from ai_studio.geometry.param_map_evidence import finite_observation

class EvidenceP09Tests(unittest.TestCase):
    def record(self, **kw):
        args=dict(source_id='Edge/reference',target_id='FINSP/UV',uv_residual='1e-10',
                  xyz_edge_residual='1e-8',xyz_face_residual='1e-8',evidence_refs=['synthetic-test-only'])
        args.update(kw)
        return finite_observation(**args)

    def test_strict_failure_does_not_erase_finite_correspondence(self):
        r=self.record()
        self.assertEqual(r['P05_numeric_coincidence_check']['status'],'OUTSIDE_THRESHOLDS')
        self.assertIn('OBSERVED_STRICT_RESIDUAL_UNMET',r['finite_param_map_status'])
        self.assertEqual(r['geometry_gate_observation']['status'],'BELOW_THRESHOLD_FINITE_OBSERVATION')
        self.assertFalse(r['geometry_release'])

    def test_zero_residual_not_identity_membership_or_global_proof(self):
        r=self.record(uv_residual=0,xyz_edge_residual=0,xyz_face_residual=0,candidate_enumeration_complete=True)
        self.assertEqual(r['P05_numeric_coincidence_check']['status'],'WITHIN_THRESHOLDS')
        self.assertEqual(r['representation_identity_match']['status'],'NOT_ESTABLISHED')
        self.assertEqual(r['trim_membership_status']['FINSP_candidate'],'UNKNOWN')
        self.assertFalse(r['global_param_map_complete']);self.assertFalse(r['global_3d_minimum_proven'])

    def test_native_source_classification_not_inherited(self):
        r=self.record(source_membership_receipt={'Face':3,'Edge':1},topology_receipt={'path':['Face','Fin','Edge']})
        self.assertEqual(r['topology_incidence_status']['status'],'RECORDED_NATIVE_INCIDENCE')
        self.assertEqual(r['trim_membership_status']['FINSP_candidate'],'UNKNOWN')
        self.assertFalse(r['trim_membership_status']['inherit_source_to_candidate'])

    def test_geometry_exceedance_is_an_observation_only(self):
        r=self.record(xyz_edge_residual='.002')
        self.assertTrue(r['geometry_gate_observation']['status'].startswith('EXCEEDS'))
        self.assertEqual(r['geometry_gate_observation']['boundary_acceptance'],'NOT_ESTABLISHED')

    def test_threshold_equality_and_face_projection_are_independent(self):
        self.assertEqual(self.record(uv_residual='1e-12',xyz_edge_residual='1e-9',xyz_face_residual='1e-9')['P05_numeric_coincidence_check']['status'],'WITHIN_THRESHOLDS')
        self.assertEqual(self.record(uv_residual='1e-12',xyz_edge_residual='1e-9',xyz_face_residual='1.0001e-9')['P05_numeric_coincidence_check']['status'],'OUTSIDE_THRESHOLDS')

    def test_periodic_duplicates_and_unresolved_are_preserved(self):
        original={'candidates':[{'t':0,'winding':0},{'t':1,'winding':1}], 'continuous':[[.2,.4]],'unresolved':[[.5,.6]]}
        r=self.record(candidate_metadata=original,legacy_status='UNRESOLVED_STRICT_UV_XYZ_MATCH')
        self.assertEqual(r['candidate_metadata'],original)
        r['candidate_metadata']['candidates'].clear()
        self.assertEqual(len(original['candidates']),2)
        self.assertEqual(r['legacy_status'],'UNRESOLVED_STRICT_UV_XYZ_MATCH')

    def test_invalid_residuals_are_rejected(self):
        for v in ['NaN','Infinity','-1']:
            with self.subTest(value=v),self.assertRaises(ValueError):self.record(uv_residual=v)

    def test_missing_evidence_does_not_create_claim(self):
        with self.assertRaises(ValueError):self.record(evidence_refs=[])
        r=self.record();self.assertEqual(r['topology_incidence_status']['status'],'UNKNOWN')

if __name__=='__main__':unittest.main()
