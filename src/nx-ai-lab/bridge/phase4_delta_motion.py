"""Native NX Delta simulation and independent closed-loop validation."""
import asyncio,json,re,math,csv
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from mechanism_agent.delta_plan import forward,joints_at
from mechanism_agent.calculations import constraint_rank


async def main():
 pointer=LOG/'current-delta.json';run=json.loads(pointer.read_text(encoding='utf-8'));p=run['plan'];m=Path(run['models']);out=Path(run['logs']);report={}
 def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
 async with Client() as c:
  assert run['build_passed'] and (await c.call('nx_status'))['part']==run['assembly']
  await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
  components={'Link0':'GROUND',**{f'Upper{i}':f'UPPER{i}' for i in range(1,4)},**{f'Rod{i}{s}':f'ROD{i}{s}' for i in range(1,4) for s in ['L','R']},'Platform':'PLATFORM'}
  for link,component in components.items():await c.call(f'nx_motion_link {link} {int(link=="Link0")} {component}')
  report['joints']=[]
  for j in p['joints']:
   if j['type']=='Universal':cmd='nx_motion_universal '+' '.join(map(str,[j['name'],j['first'],j['second'],*j['origin'],*j['axis'],*j['axis2']]))
   else:cmd='nx_motion_joint '+' '.join(map(str,[j['name'],j['type'],j['first'],j['second'],*j['origin'],*j['axis'],*(p['drive']['relative_limits_deg'] if j['type']=='Revolute' else [])]))
   report['joints'].append(await c.call(cmd));save()
  report['unactuated']=await c.call('nx_motion_solution Free articulation 2 60');save();assert report['unactuated']['gruebler_dof']==3
  for i,velocity in enumerate(p['drive']['velocity_deg_s'],1):await c.call(f'nx_motion_driver Input{i} Base{i} revolute {velocity}')
  async def marker(name,link,point):return await c.call('nx_motion_marker '+' '.join(map(str,[name,link,*point])))
  center=np.array(p['initial']['platform'])
  for name,delta in [('D0',[0,0,0]),('DX',[10,0,0]),('DY',[0,10,0]),('DZ',[0,0,10])]:await marker(name,'Platform',center+delta)
  for i in range(1,4):await marker(f'DE{i}',f'Upper{i}',p['initial']['elbows'][i-1])
  for j in p['joints']:
   if j['type']!='Universal':continue
   name=j['name'][1:];bottom=next(x for x in p['joints'] if x['name']=='S'+name)['origin'];B=np.array(j['origin']);n=np.array(j['axis2'])
   for marker_name,link,point in [(f'DU{name}',j['first'],B),(f'DR{name}B',j['second'],B),(f'DR{name}C',j['second'],bottom),(f'DR{name}N',j['second'],B+10*n),(f'DP{name}','Platform',bottom)]:await marker(marker_name,link,point)
  report['driven']=await c.call('nx_motion_solution Cycle normal 2 60');save();assert report['driven']['gruebler_dof']==0
  report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save()
  report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok'],str(report['solve'])
  minf=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in minf.splitlines() if any(k in line.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save();assert re.search(r'Number of Degrees of Freedom\s*=\s*0',minf) and re.search(r'Number of Drivers\s*=\s*3',minf);assert report['audit']['solver_parameters']['solver']==1
  await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.75');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results D');save()
  data={row['marker']:np.array([a['values'] for a in row['coordinates']]).T for row in report['markers']['markers']};assert all(len(x)==61 for x in data.values())
  times=np.linspace(0,2,61);expected=np.array([row['platform'] for row in p['independent_trajectory']]);checks={'platform_position_error_mm':float(max(np.linalg.norm(data['D0']-expected,axis=1)))}
  orientations=[];ranks=[];angles=[]
  for frame,time in enumerate(times):
   basis=np.column_stack([(data[key][frame]-data['D0'][frame])/10 for key in ['DX','DY','DZ']]);u,s,v=np.linalg.svd(basis);rot=u@v;assert np.linalg.det(rot)>0 and max(abs(s-1))<1e-4;orientations.append(math.degrees(math.acos(np.clip((np.trace(rot)-1)/2,-1,1))))
   q=[]
   for i in range(3):
    vector=data[f'DE{i+1}'][frame]-p['initial']['anchors'][i];q.append(math.degrees(math.atan2(-vector[2],vector@np.array(p['initial']['radial'][i]))))
   angles.append(q);pose=forward(q);rank=constraint_rank(p['rigid_links'],joints_at(pose),'Link0');ranks.append(rank['functional_dof']);assert rank['functional_dof']==3 and pose['A_min_singular']>.2 and pose['B_min_abs_normalized']>.2
  checks['platform_orientation_error_deg']=max(orientations);angles=np.array(angles);expected_angles=np.array([30,30,30])+times[:,None]*p['drive']['velocity_deg_s'];checks['base_angle_error_deg']=float(max(abs(angles-expected_angles).ravel()));assert np.all(angles>=20-1e-4) and np.all(angles<=45+1e-4)
  jointchecks=[]
  for i in range(1,4):
   tangent=np.array(p['initial']['tangent'][i-1])
   for side in ['L','R']:
    name=f'{i}{side}';upper=float(max(np.linalg.norm(data[f'DU{name}']-data[f'DR{name}B'],axis=1)));lower=float(max(np.linalg.norm(data[f'DP{name}']-data[f'DR{name}C'],axis=1)));axis=float(max(abs(((data[f'DR{name}N']-data[f'DR{name}B'])/10)@tangent)));jointchecks.append({'rod':name,'upper_center_gap_mm':upper,'lower_center_gap_mm':lower,'universal_cross_axis_dot':axis});assert max(upper,lower)<.02 and axis<1e-4
  report['cross_validation']={'checks':checks,'joint_closure':jointchecks,'platform_orientation_deg':orientations,'actual_base_angles_deg':angles.tolist(),'independent_rank_functional_dof':ranks,'source':'NX native marker positions compared to independent sphere-intersection FK; no host-generated motion'};save();assert checks['platform_position_error_mm']<.02 and checks['platform_orientation_error_deg']<.01 and checks['base_angle_error_deg']<.01,str(checks)
  with (out/'native-platform-trajectory.csv').open('w',newline='') as stream:
   writer=csv.writer(stream);writer.writerow(['time_s','x_mm','y_mm','z_mm','orientation_error_deg','q1_deg','q2_deg','q3_deg'])
   for k,t in enumerate(times):writer.writerow([t,*data['D0'][k],orientations[k],*angles[k]])
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in range(61):
   state=await c.call(f'nx_motion_frame Cycle {frame}');assert state['active_solution']=='Cycle';check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
   if check['interference_count']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual Delta collision; preserve before repair')
  await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True);pointer.write_text(json.dumps(run,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__=='__main__':asyncio.run(main())
