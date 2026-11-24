import asyncio,json,math
from pathlib import Path
import numpy as np
from phase3_client import Client,LOG,ROOT
from phase3_results import write_results
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());out=Path(run['logs']);p=run['plan'];report={'nx_pid':int((ROOT/'logs/nx-gui-pid.txt').read_text())}
 async with Client() as c:
  status=await c.call('nx_status');assert not status.get('part')
  await c.call('nx_open_part '+run['assembly']);report['assembly']=await c.call('nx_assembly_state');assert report['assembly']['unsatisfied_constraint_count']==0 and report['assembly']['all_prototypes_fully_loaded']
  report['joint_geometry']=[]
  for i,(center,axis) in enumerate(zip(p['joint_centers_mm'],p['joint_axes']),1):
   records=[]
   for name,radius in [(f'LINK{i-1}',4.2),(f'LINK{i}',5.05)]:
    g=await c.call('nx_component_geometry '+name);candidates=[]
    for face in g['faces']:
     if face['kind']!='Cylindrical' or abs(face['radius_mm']-radius)>.0001:continue
     a=np.array(face['direction']);v=np.array(face['point_mm'])-center;parallel=abs(np.dot(a,axis));distance=np.linalg.norm(v-np.dot(v,axis)*np.array(axis))
     if parallel>.99999 and distance<.001:candidates.append(face)
    assert candidates,(i,name,g);records.append({'component':name,'actual_matching_bore':candidates[0]})
   dof=await c.call(f'nx_assembly_state LINK{i-1}');d=next(x for x in dof['components'] if x['name']==f'LINK{i}');assert d['dof_result']=='Success' and (d['translation_dof'],d['rotation_dof'])==(0,1)
   report['joint_geometry'].append({'joint':i,'expected_origin':center,'expected_axis':axis,'bore_evidence':records,'relative_dof':d})
  report['static_interference']=await c.call('nx_interference');assert len(report['static_interference']['pairs'])==300 and not any(x['nx_result']==1 for x in report['static_interference']['pairs'])
  await c.call('nx_close_part 0');await c.call('nx_open_part '+run['simulation']);await c.call('nx_motion_setup');report['motion_audit']=await c.call('nx_motion_audit')
  await c.call('nx_application motion');await asyncio.sleep(.6);await c.call('nx_view iso 0.85');await asyncio.sleep(.3);await c.call('nx_motion_frame Precision 0')
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for step in [0,30,60]:
   frame=await c.call(f'nx_motion_frame Precision {step}');collision=await c.call(f'nx_motion_collision_check {step}');assert collision['interference_count']==0;report['frames'].append({'frame':frame,'collision':collision})
  report['markers']=await c.call('nx_motion_marker_results EE');report['joints']=await c.call('nx_motion_joint_results');write_results(p,report['markers'],report['joints'],out)
  report['passed']=True;(out/'cold-restart-verification.json').write_text(json.dumps(report,indent=2));await c.call('nx_motion_frame Precision finish')
if __name__=='__main__':asyncio.run(main())
