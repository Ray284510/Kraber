import asyncio,json,math,re
from pathlib import Path
import numpy as np
from phase4_common import Client,ROOT,LOG
from mechanism_agent.calculations import fourbar_pose,constraint_rank
async def main():
 run=json.loads((LOG/'current-fourbar.json').read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);p=run['plan'];report={}
 def save(): (out/'motion-results.json').write_text(json.dumps(report,indent=2))
 async with Client() as c:
  status=await c.call('nx_status');assert status['part']==run['assembly'];assert run['build_passed']
  await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
  for name,fixed,components in [('Ground',1,['GROUND','PIN_O','PIN_D']),('Crank',0,['CRANK','PIN_A','COLLAR_A']),('Coupler',0,['COUPLER']),('Rocker',0,['ROCKER','PIN_C','COLLAR_C'])]:
   # Ground is reserved by NX; Link0 is the model's fixed link name.
   await c.call(f'nx_motion_link {"Link0" if fixed else name} {fixed} '+' '.join(components))
  for j in p['joints']:await c.call(f'nx_motion_joint {j["name"]} Revolute {j["first"]} {"Link0" if j["second"]=="Ground" else j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
  report['unactuated']=await c.call('nx_motion_solution Free articulation 4 120');save()
  await c.call('nx_motion_driver Input J0 revolute 90')
  for marker in p['output_markers']:await c.call(f'nx_motion_marker FB{marker["name"]} {marker["link"]} '+' '.join(map(str,marker['point'])))
  report['driven']=await c.call('nx_motion_solution Cycle normal 4 120');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save()
  report['solve']=await c.call('nx_motion_solve Cycle');save()
  minf=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in minf.splitlines() if any(s in line.lower() for s in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save()
  counts=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',minf,re.M);assert counts and int(counts[-1])==0,minf
  assert re.search(r'Number of Drivers\s*=\s*1',minf);assert report['audit']['solver_parameters']['solver']==1
  await c.call('nx_application motion');await asyncio.sleep(.6);await c.call('nx_view iso 0.8');await asyncio.sleep(.3);await c.call('nx_motion_frame Cycle 0')
  report['markers']=await c.call('nx_motion_marker_results FB');report['joints']=await c.call('nx_motion_joint_results');save()
  markers={row['marker']:np.array([a['values'] for a in row['coordinates']]).T for row in report['markers']['markers']}
  actualA=markers['FBA'][:,:2];actualC=markers['FBC'][:,:2];expected=[];theta=np.linspace(30,390,121);mu=[];ranklist=[]
  for i,angle in enumerate(theta):
   pose=fourbar_pose(100,25,90,70,math.radians(angle));expected.append(pose['C'])
   u=actualA[i]-actualC[i];v=np.array([100,0])-actualC[i];ang=math.degrees(math.acos(float(np.clip(u@v/np.linalg.norm(u)/np.linalg.norm(v),-1,1))));mu.append(min(ang,180-ang))
   joints=json.loads(json.dumps(p['joints']));joints[1]['origin'][:2]=actualA[i].tolist();joints[2]['origin'][:2]=actualC[i].tolist();ranklist.append(constraint_rank(p['rigid_links'],joints)['functional_dof'])
  err=float(np.max(np.linalg.norm(actualC-np.array(expected),axis=1)));input_angle=np.degrees(np.unwrap(np.arctan2(actualA[:,1],actualA[:,0])));input_error=float(np.max(abs(input_angle-theta)))
  output=np.unwrap(np.arctan2(actualC[:,1],actualC[:,0]-100));extrema=[]
  for i in range(1,120):
   if (output[i]-output[i-1])*(output[i+1]-output[i])<0:
    shift=(output[i-1]-output[i+1])/(2*(output[i-1]-2*output[i]+output[i+1]));extrema.append(float((theta[i]+shift*3)%360))
  extrema.sort();assert len(extrema)==2;arcs=[extrema[1]-extrema[0],360-extrema[1]+extrema[0]];ratio=max(arcs)/min(arcs);calc=p['independent_calculation']
  report['cross_validation']={'max_marker_error_mm':err,'max_input_angle_error_deg':input_error,'native_interpolated_dead_centers_deg':extrema,'native_quick_return_ratio':ratio,'native_min_transmission_angle_deg':min(mu),'independent':calc,'spatial_rank_functional_dof_each_frame':ranklist,'note':'NX nominal Gruebler is separately reported; native reduced solver DOF zero with one driver corroborates functional mobility one.'};save()
  assert err<p['tolerances']['marker_position_mm'] and input_error<p['tolerances']['angle_deg'];assert max(abs(a-b) for a,b in zip(extrema,calc['dead_center_input_deg']))<p['tolerances']['dead_center_input_deg'];assert abs(ratio-calc['quick_return_ratio'])<p['tolerances']['quick_return_ratio'];assert set(ranklist)=={1}
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in range(121):
   f=await c.call(f'nx_motion_frame Cycle {frame}');check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append({'frame':f,'collision':check});save()
   if check['interference_count']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual collision; preserve before repair')
  await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update({'simulation':str(m/'mechanism.sim'),'solution':'Cycle','motion_passed':True});(LOG/'current-fourbar.json').write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main())
