import asyncio,json,re,math,csv,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from mechanism_agent.calculations import slider_crank,constraint_rank


async def main(family):
 pointer=LOG/f'current-{family}.json';run=json.loads(pointer.read_text(encoding='utf-8'));p=run['plan'];m=Path(run['models']);out=Path(run['logs']);report={};radius=p['parameters']['crank_radius'];rod=p['parameters']['rod_length'];A=p['initial_points']['A'];B=p['initial_points']['B']
 steps=p['drive']['steps'];duration=p['drive']['duration_s'];velocity=p['drive']['velocity_deg_s'];initial=p['parameters']['initial_angle_deg']
 def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
 async with Client() as c:
  assert run['build_passed'] and (await c.call('nx_status'))['part']==run['assembly'];await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
  for name,fixed,components in [('Link0',1,['GROUND','PIN_O']),('Crank',0,['CRANK','PIN_A','COLLAR_A']),('Coupler',0,['COUPLER']),('Slider',0,['SLIDER','PIN_B','COLLAR_B'])]:await c.call(f'nx_motion_link {name} {fixed} '+' '.join(components))
  for j in p['joints']:await c.call('nx_motion_joint '+' '.join(map(str,[j['name'],j['type'],j['first'],j['second'],*j['origin'],*j['axis']])))
  report['unactuated']=await c.call(f'nx_motion_solution Free articulation {duration} {steps}');await c.call(f'nx_motion_driver InputDrive Input revolute {velocity}')
  for name,link,point in [('FInput','Crank',A+[24.6]),('FOutput','Slider',B+[24.6]),('FRodA','Coupler',A+[24.6]),('FRodB','Coupler',B+[24.6])]:await c.call('nx_motion_marker '+' '.join(map(str,[name,link,*point])))
  report['driven']=await c.call(f'nx_motion_solution Cycle normal {duration} {steps}');report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
  minf=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in minf.splitlines() if any(k in line.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save();assert re.search(r'Number of Degrees of Freedom\s*=\s*0',minf) and re.search(r'Number of Drivers\s*=\s*1',minf);assert report['audit']['solver_parameters']['solver']==1
  blocks=minf.split('The following are redundant constraints and will be deleted from the system:')[1:];removed=[re.findall(r'Type:\s+([^\r\n]+)',block.split('The remaining constraints')[0]) for block in blocks];report['redundancy_audit']={'native_blocks':len(removed),'removed_constraint_counts':list(map(len,removed)),'all_rotational':all('rotation' in row for rows in removed for row in rows)};save();assert removed and all(len(rows)==3 for rows in removed) and report['redundancy_audit']['all_rotational']
  await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.75');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results F');save();data={r['marker']:np.array([a['values'] for a in r['coordinates']]).T for r in report['markers']['markers']};assert all(len(x)==steps+1 for x in data.values())
  a=data['FInput'];b=data['FOutput'];times=np.linspace(0,duration,steps+1);expected_angle=initial+velocity*times;theta=np.degrees(np.unwrap(np.arctan2(a[:,1],a[:,0])));pred=np.array(slider_crank(radius,rod,np.radians(expected_angle))['x']);lo=int(np.argmin(b[:-1,0]));hi=int(np.argmax(b[:-1,0]));arc=(hi-lo)%steps;ratio=max(arc,steps-arc)/min(arc,steps-arc);stroke=float(np.ptp(b[:,0]));ranks=[]
  for k in range(steps+1):
   joints=json.loads(json.dumps(p['joints']));joints[1]['origin']=a[k].tolist();joints[2]['origin']=b[k].tolist();joints[3]['origin']=[b[k,0],0,17.2];rank=constraint_rank(['Link0','Crank','Coupler','Slider'],joints,'Link0');ranks.append(rank['functional_dof']);assert rank['functional_dof']==1
  closure=max(float(max(np.linalg.norm(a-data['FRodA'],axis=1))),float(max(np.linalg.norm(b-data['FRodB'],axis=1))));pressure=np.degrees(abs(np.arctan2(a[:,1]-b[:,1],b[:,0]-a[:,0])))
  checks={'output_position_error_mm':float(max(abs(b[:,0]-pred))),'output_cross_axis_error_mm':float(max(abs(b[:,[1,2]]-np.array([0,24.6])).ravel())),'input_angle_error_deg':float(max(abs(theta-expected_angle))),'joint_closure_error_mm':closure,'native_stroke_mm':stroke,'expected_stroke_mm':2*radius,'native_quick_return_ratio':ratio,'native_dead_center_angles_deg':[float(theta[lo]),float(theta[hi])],'native_max_pressure_angle_deg':float(max(pressure)),'independent_max_pressure_angle_deg':p['independent_calculation']['maximum_pressure_angle_deg']}
  report['cross_validation']={'checks':checks,'functional_rank_dof':ranks,'note':'Raw spatial Gruebler count includes three dependent constraints; native reduced solver with one driver has zero remaining DOF. Platform motion is not prescribed.'};save();assert checks['output_position_error_mm']<.02 and checks['output_cross_axis_error_mm']<.02 and checks['input_angle_error_deg']<.02 and closure<.02 and abs(stroke-2*radius)<.02 and abs(ratio-1)<.005
  with (out/'native-feeding-trajectory.csv').open('w',newline='') as stream:
   w=csv.writer(stream);w.writerow(['time_s','input_absolute_deg','output_x_mm','output_y_mm','output_z_mm','pressure_angle_deg'])
   for k,t in enumerate(times):w.writerow([t,theta[k],*b[k],pressure[k]])
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in range(steps+1):
   state=await c.call(f'nx_motion_frame Cycle {frame}');assert state['active_solution']=='Cycle';check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
   if check['interference_count']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual feeder collision; retain model before repair')
  await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True);pointer.write_text(json.dumps(run,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__=='__main__':asyncio.run(main(sys.argv[1] if len(sys.argv)>1 else 'feeder'))
