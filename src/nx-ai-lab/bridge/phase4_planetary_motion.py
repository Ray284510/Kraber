"""Real NX planetary transmission; independent Willis comparison of native markers."""
import asyncio,json,math,re
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
async def main():
 pointer=LOG/'current-planetary.json';run=json.loads(pointer.read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);plan=run['plan'];report={}
 def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
 async with Client() as c:
  assert run['build_passed'];assert (await c.call('nx_status'))['part']==run['assembly'];await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
  for name,fixed,components in [('Link0',1,['GROUND','RING']),('Carrier',0,['CARRIER']),('Sun',0,['SUN'])]+[(f'Planet{i}',0,[f'PLANET{i}']) for i in range(1,4)]:await c.call(f'nx_motion_link {name} {fixed} '+' '.join(components))
  for j in plan['joints']:await c.call(f'nx_motion_joint {j["name"]} Revolute {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
  report['couplers']=[]
  for i in range(1,4):
   phi=(i-1)*2*math.pi/3
   report['couplers'].append(await c.call(f'nx_motion_gear_pair SunMesh{i} SunAxis PlanetAxis{i} 30 15 {30*math.cos(phi)} {30*math.sin(phi)} 24'));save()
  report['couplers'].append(await c.call('nx_motion_joint_coupler RingWillis PlanetAxis1 CarrierAxis 1 4'));save()
  report['unactuated']=await c.call('nx_motion_solution Free articulation 4 240');assert report['unactuated']['gruebler_dof']==1
  await c.call('nx_motion_driver Input CarrierAxis revolute 30')
  await c.call('nx_motion_marker GSun Sun 20 0 24');await c.call('nx_motion_marker GCarrier Carrier 0 35 13')
  for i,xy in enumerate(plan['planet_centers'],1):
   await c.call(f'nx_motion_marker GC{i} Planet{i} {xy[0]} {xy[1]} 24');await c.call(f'nx_motion_marker GT{i} Planet{i} {xy[0]+8} {xy[1]} 24')
  report['driven']=await c.call('nx_motion_solution Cycle normal 4 240');assert report['driven']['gruebler_dof']==0;report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();report['solve']=await c.call('nx_motion_solve Cycle');save()
  minf=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[l.strip() for l in minf.splitlines() if any(k in l.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save();counts=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',minf,re.M);assert counts and int(counts[-1])==0;assert re.search(r'Number of Drivers\s*=\s*1',minf);assert report['audit']['solver_parameters']['solver']==1
  await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.75');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results G');save();data={r['marker']:np.array([a['values'] for a in r['coordinates']]).T for r in report['markers']['markers']};t=np.linspace(0,4,241);angles={}
  def angle(v):return np.degrees(np.unwrap(np.arctan2(v[:,1],v[:,0])))
  angles['sun']=angle(data['GSun'][:,:2]);angles['carrier']=angle(data['GCarrier'][:,:2])-90
  checks={'sun_angle_error_deg':float(max(abs(angles['sun']-90*t))),'carrier_angle_error_deg':float(max(abs(angles['carrier']-30*t)))}
  for i in range(1,4):
   center=data[f'GC{i}'][:,:2];tip=data[f'GT{i}'][:,:2];angles[f'planet{i}']=angle(tip-center);checks[f'planet{i}_angle_error_deg']=float(max(abs(angles[f'planet{i}']+90*t)));phi=np.radians(30*t+120*(i-1));expected=45*np.array([np.cos(phi),np.sin(phi)]).T;checks[f'planet{i}_center_error_mm']=float(max(np.linalg.norm(center-expected,axis=1)))
  report['cross_validation']={'checks':checks,'angles_deg':{k:v.tolist() for k,v in angles.items()},'independent_ratio':3,'native_sun_to_carrier_ratio':float(angles['sun'][-1]/angles['carrier'][-1])};save();assert all(v<.02 for v in checks.values()),str(checks)
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in range(241):
   state=await c.call(f'nx_motion_frame Cycle {frame}');assert state['active_solution']=='Cycle';check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
   if check['interference_count']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual planetary collision')
  await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update({'simulation':str(m/'mechanism.sim'),'solution':'Cycle','motion_passed':True});pointer.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main())
