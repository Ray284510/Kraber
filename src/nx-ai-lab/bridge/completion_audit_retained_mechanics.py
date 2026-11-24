"""Read-only independent rank cross-check against retained actual NX query records."""
import json,hashlib
from pathlib import Path
from phase4_common import ROOT,LOG
from mechanism_agent.native_mobility import gear_rank
from mechanism_agent.topology import from_native_audit

def main():
    rows={}
    for family in ['completion_multistage_gears','completion_internal_gears_direct','delta']:
        meta=json.loads((LOG/f'current-{family}.json').read_text(encoding='utf-8'));file=Path(meta['logs'])/'motion-results.json';raw=file.read_bytes();r=json.loads(raw)
        result=from_native_audit(r['audit']) if family=='delta' else gear_rank(r['audit'],r['coupler_state'])
        dof=result['independent_spatial_rank']['functional_dof'] if family=='delta' else result['functional_dof'];expected=3 if family=='delta' else 1
        rows[family]={'source':str(file),'sha256':hashlib.sha256(raw).hexdigest(),'independent_result':result,'expected_functional_dof':expected,'matches':dof==expected,'scope':'Independent rereading of retained native audit; not a new NX execution and not a substitute for the existing motion/collision acceptance.'}
        assert dof==expected,(family,result)
    (ROOT/'logs/phase4_completion/retained-native-mechanics-crosscheck.json').write_text(json.dumps(rows,indent=2));print({k:v['matches'] for k,v in rows.items()})

if __name__=='__main__':main()
