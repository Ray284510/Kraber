"""Replace internal mesh sign inference with NX native two-joint Willis coupling."""
import asyncio,json,math,re,shutil
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
async def main():
 pointer=LOG/'current-planetary.json';run=json.loads(pointer.read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);plan=run['plan'];report={'repair_round':3,'direction_fault_attempt':2,'diagnosis':'Carrier-axis convention change was coordinate invariant; use explicit native relative-angle coupling','equation':'planet_relative + 4*carrier_absolute = 0','change':'Three true external gear couplers plus one native rotational joint coupler for fixed-ring internal mesh. No geometry/component translations.'}
 def save():(out/'motion-repair-3.json').write_text(json.dumps(report,indent=2))
 backup=out/'before_willis_repair';backup.mkdir(exist_ok=False)
 for file in m.glob('mechanism*'):
  if file.is_file():shutil.copy2(file,backup/file.name)
 async with Client() as c:
  assert not (await c.call('nx_status'))['part'];await c.call('nx_open_part '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
  report['axis']=await c.call('nx_motion_joint_axis CarrierAxis 0 0 1');report['driver']=await c.call('nx_motion_driver_velocity Input 30')
  report['coupler']=await c.call('nx_motion_joint_coupler RingWillis PlanetAxis1 CarrierAxis 1 4')
  report['mobility']=await c.call('nx_motion_solution WillisFree articulation 4 240');report['suppression']=[]
  for obj in ['RingMesh1','RingMesh2','RingMesh3','Input']:report['suppression'].append(await c.call(f'nx_motion_suppress WillisFree {obj} 1'))
  assert report['suppression'][-1]['gruebler_dof']==1
  report['driven']=await c.call('nx_motion_solution Willis normal 4 240')
  for obj in ['RingMesh1','RingMesh2','RingMesh3']:report['suppression'].append(await c.call(f'nx_motion_suppress Willis {obj} 1'))
  assert report['suppression'][-1]['gruebler_dof']==0
  report['precision']=await c.call('nx_motion_tighten Willis 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();report['solve']=await c.call('nx_motion_solve Willis',True);save();assert report['solve']['ok']
  minf=(m/'mechanism-willis.minf').read_text(errors='replace');report['native_summary']=[l.strip() for l in minf.splitlines() if any(k in l.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save();counts=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',minf,re.M);assert counts and int(counts[-1])==0;assert re.search(r'Number of Drivers\s*=\s*1',minf);assert report['audit']['solver_parameters']['solver']==1
  await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.75');await c.call('nx_motion_frame Willis 0');report['markers']=await c.call('nx_motion_marker_results G');save();data={r['marker']:np.array([a['values'] for a in r['coordinates']]).T for r in report['markers']['markers']};t=np.linspace(0,4,241);angles={}
  def angle(v):return np.degrees(np.unwrap(np.arctan2(v[:,1],v[:,0])))
  angles['sun']=angle(data['GSun'][:,:2]);angles['carrier']=angle(data['GCarrier'][:,:2])-90
  checks={'sun_angle_error_deg':float(max(abs(angles['sun']-90*t))),'carrier_angle_error_deg':float(max(abs(angles['carrier']-30*t)))}
  for i in range(1,4):
   center=data[f'GC{i}'][:,:2];tip=data[f'GT{i}'][:,:2];angles[f'planet{i}']=angle(tip-center);checks[f'planet{i}_angle_error_deg']=float(max(abs(angles[f'planet{i}']+90*t)));phi=np.radians(30*t+120*(i-1));expected=45*np.array([np.cos(phi),np.sin(phi)]).T;checks[f'planet{i}_center_error_mm']=float(max(np.linalg.norm(center-expected,axis=1)))
  report['cross_validation']={'checks':checks,'angles_deg':{k:v.tolist() for k,v in angles.items()},'independent_ratio':3,'native_sun_to_carrier_ratio':float(angles['sun'][-1]/angles['carrier'][-1])};save();assert all(v<.02 for v in checks.values()),str(checks)
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in range(241):
   state=await c.call(f'nx_motion_frame Willis {frame}');assert state['active_solution']=='Willis';check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
   if check['interference_count']:await c.call('nx_motion_frame Willis finish');await c.call('nx_save_part');raise RuntimeError('Actual planetary collision')
  await c.call('nx_motion_frame Willis finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update({'simulation':str(m/'mechanism.sim'),'solution':'Willis','motion_passed':True});pointer.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main())
