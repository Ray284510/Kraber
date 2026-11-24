"""Read actual target faces and establish outward normal by native solid containment."""
import asyncio,json,math,sys,hashlib
from pathlib import Path
import numpy as np
from phase4_client import Client,LOG

async def query_normals(c,run):
    audit=await c.call('nx_motion_audit');geometry=await c.call('nx_component_geometry WHEEL')
    markers=json.loads((Path(run['logs'])/'motion-results.json').read_text())['markers'];row=next(v for v in markers['markers'] if v['marker']=='RHoldTip');tip=np.array([v['values'][0] for v in row['coordinates']])[:2]
    joint=next(v for v in audit['joints'] if v['name']=='HoldPawlAxis');pivot=np.array([joint['origin1']['X'],joint['origin1']['Y']]);lever=tip-pivot;a=math.radians(run['plan']['parameters']['hold_face_deg']);center=np.array([40.,0.]);rad=np.array([math.cos(a),math.sin(a)])
    edge=22*np.array([math.cos(a+math.radians(12)),math.sin(a+math.radians(12))])-25*rad
    definitions=[('reverse_face',center+23.5*rad,np.array([math.sin(a),-math.cos(a)])),('forward_ramp',center+25*rad+edge/2,np.array([edge[1],-edge[0]])/np.linalg.norm(edge))];result={'actual_audit':audit,'actual_component_geometry':geometry,'actual_tip':tip.tolist(),'actual_pivot':pivot.tolist(),'faces':[]}
    for label,xy,expected in definitions:
        requested=np.array([*xy,24.]);faces=[v for v in geometry['faces'] if v['kind']=='Planar' and np.linalg.norm((np.array(v['bbox_mm'][:3])+v['bbox_mm'][3:])/2-requested)<1e-6];assert len(faces)==1,(label,faces)
        f=faces[0];native=await c.call(f'nx_research_face_normal WHEEL {f["reference"]} '+' '.join(map(str,requested)))
        assert native['point_on_body']==3 and {native['plus_normal_containment'],native['minus_normal_containment']}=={1,2}
        raw=np.array(native['unit_face_normal']);normal=raw if native['plus_normal_containment']==2 else -raw
        assert np.dot(normal[:2],expected)>.999999
        torque=float(lever[0]*normal[1]-lever[1]*normal[0]);result['faces'].append(dict(label=label,matched_native_face=f,native_normal_probe=native,solid_outward_normal=normal.tolist(),moment_per_unit_normal_force_mm=torque))
    result['passed']=result['faces'][0]['moment_per_unit_normal_force_mm']<0 and result['faces'][1]['moment_per_unit_normal_force_mm']>0
    result['scope']='Target face selected by actual trimmed-face bounds; orientation verified by actual solid inside/outside samples. Unit-force moment signs only. No loaded stop, spring dynamics or unilateral reaction-force claim.'
    return result

async def main(family):
    run=json.loads((LOG/f'current-{family}.json').read_text());out=Path(run['logs']);sim=Path(run['simulation']);before=hashlib.sha256(sim.read_bytes()).hexdigest()
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');await c.call('nx_open_part '+str(sim));await c.call('nx_motion_setup');result=await query_normals(c,run);await c.call('nx_close_part 0')
    result['simulation_sha256_unchanged']=hashlib.sha256(sim.read_bytes()).hexdigest()==before;assert result['simulation_sha256_unchanged'];(out/'native-contact-normal-verification.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2));assert result['passed']
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
