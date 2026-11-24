"""New API-scope checks; no historical NX experiments or projection matrix."""
import unittest,json,hashlib,os
from pathlib import Path
from ai_studio.geometry.param_map_candidates import uv_candidates
class ScopeP08Tests(unittest.TestCase):
 def check_flags(self,r,value):
  self.assertEqual(r['complete'],value)
  self.assertEqual(r['candidate_enumeration_complete_for_supplied_uv_and_shifts'],value)
  self.assertFalse(r['global_param_map_complete']);self.assertFalse(r['global_3d_minimum_proven'])
  self.assertEqual(r['completeness_schema_version'],2)
 def test_local_complete_never_global(self):
  r=uv_candidates(1,[0,0,1,1],[[0,0,0,1],[1,0,0,1]],[.3,0],[0,0],'synthetic_scope')
  self.check_flags(r,True);self.assertGreaterEqual(len(r['candidates']),3)
 def test_invalid_weights_explicit_scope(self):
  r=uv_candidates(1,[0,0,1,1],[[0,0,0,0],[1,0,0,1]],[.3,0],[0,0],'synthetic_scope')
  self.check_flags(r,False)
 def test_unresolved_is_not_global_or_complete(self):
  r=uv_candidates(2,[0,0,0,1,1,1],[[0,0,0,1],[1,1,0,1],[2,0,0,1]],[.3,.2],[0,0],'synthetic_scope',max_depth=0)
  self.check_flags(r,False);self.assertTrue(r['unresolved'])
 def test_fixture_is_identified_native_not_synthetic(self):
  p=Path(__file__).parent/'fixtures/p04_native_topology_paths.json';v=json.loads(p.read_text())
  self.assertEqual(v['origin'],'EXTRACTED_REAL_NX12_NATIVE_RECORDS_NOT_SYNTHETIC')
  self.assertEqual(len([x for x in v['records'] if x['path'][-1]['type']==8]),8)
  digest=hashlib.sha256(json.dumps(v['records'],sort_keys=True,separators=(',',':')).encode()).hexdigest()
  self.assertEqual(digest,v['records_canonical_SHA256'])
class ArchiveP08Integration(unittest.TestCase):
 @unittest.skipUnless(os.environ.get('NXAI_ARCHIVE_ROOT'),'Set NXAI_ARCHIVE_ROOT for real archive integration; basic clone tests do not require logs')
 def test_tracked_fixture_against_native_archive(self):
  f=json.loads((Path(__file__).parent/'fixtures/p04_native_topology_paths.json').read_text())
  p=Path(os.environ['NXAI_ARCHIVE_ROOT'])/f['source_relative_path'];b=p.read_bytes()
  self.assertEqual(hashlib.sha256(b).hexdigest(),f['source_SHA256'])
  records=[x for line in b.decode().splitlines() for x in [json.loads(line)] if x['kind']=='topology_use']
  self.assertEqual(records,f['records'])
if __name__=='__main__':unittest.main()
