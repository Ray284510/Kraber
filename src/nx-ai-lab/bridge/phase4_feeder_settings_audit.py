"""Cross-check actual native measurements for three real slot settings."""
from lab_paths import lab_path
import json,math
from pathlib import Path
ROOT=lab_path('');LOG=ROOT/'logs/phase4'
def main():
 rows=[];expected_disk=math.pi*33**2*6-2*math.pi*3.2**2*6-10*6.4*6
 for family in ['feeder15','feeder','feeder25']:
  run=json.loads((LOG/f'current-{family}.json').read_text());assert run['accepted'];e=Path(run['logs'])
  motion=json.loads((e/'motion-results.json').read_text());v=json.loads((e/'volume-convergence-source.json').read_text())['crank']['higher_accuracy']['volume_mm3'];c=motion['cross_validation']['checks']
  assert math.isclose(v,expected_disk,rel_tol=1e-7,abs_tol=.001)
  assert abs(c['native_stroke_mm']-2*run['plan']['parameters']['crank_radius'])<.02 and abs(c['native_quick_return_ratio']-1)<.005
  assert len(motion['frames'])==121 and all(x['interference_count']==0 for x in motion['frames'])
  rows.append({'family':family,'radius_mm':run['plan']['parameters']['crank_radius'],'native_crank_volume_mm3':v,'analytic_same_slotted_disk_volume_mm3':expected_disk,**c,'sampled_pairs':sum(len(f['pairs']) for f in motion['frames']),'models':run['models'],'evidence':str(e)})
 report={'passed':True,'same_physical_slot_geometry_verified_by_native_volume':True,'settings':rows,'scope':'Three discrete settings of a real continuous radial slot; intermediate settings, clamp forces and payload not separately validated.'}
 (ROOT/'reports/phase4/feeder-settings.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
if __name__=='__main__':main()
