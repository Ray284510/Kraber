import asyncio,json,re,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,ROOT,LOG
from mechanism_agent.cam_plan import law
async def main(family='cam_groove',law_fn=law):
 p=LOG/f'current-{family}.json';run=json.loads(p.read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);report={};plan=run['plan']
 base=plan['parameters'].get('pitch_base_radius',40);lift=plan['parameters'].get('lift',12);duration=plan['drive']['duration_s'];steps=plan['drive']['steps'];velocity=plan['drive'].get('velocity_deg_s',360/duration)
 def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
 async with Client() as c:
  if '--resume' not in sys.argv:
   assert run['build_passed'];assert (await c.call('nx_status'))['part']==run['assembly']
   await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
   for name,fixed,parts in [('Link0',1,['GROUND']),('Cam',0,['CAM','CAMSHAFT']),('Carriage',0,['CARRIAGE']),('Roller',0,['ROLLER'])]:await c.call(f'nx_motion_link {name} {fixed} '+' '.join(parts))
   for j in plan['joints']:await c.call(f'nx_motion_joint {j["name"]} {j["type"]} {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
  else:assert (await c.call('nx_status'))['part']==str(m/'mechanism.sim')
  report['contact']=await c.call(f'nx_motion_point_curve GrooveGuide Carriage 0 {base} 26 CAM curve:PITCHCURVE');report['higher_pairs']=await c.call('nx_motion_higher_pairs');report['unactuated']=await c.call(f'nx_motion_solution Free articulation {duration} {steps}');await c.call(f'nx_motion_driver Input CamAxis revolute {velocity}');await c.call('nx_motion_driver LocalSpin RollerAxis revolute 0');await c.call(f'nx_motion_marker CMCarriage Carriage 0 {base} 26');await c.call('nx_motion_marker CMCam Cam 20 0 24');report['driven']=await c.call(f'nx_motion_solution Cycle normal {duration} {steps}');report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save()
  if family!='cam_groove':
   report['sampling_refinement']=await c.tool('nx_motion_refine_point_curve_sampling',{'name':'GrooveGuide','spacing_mm':.1});report['integration_precision']=await c.tool('nx_motion_refine_integrator',{'solution':'Cycle','tolerance':1e-9,'maximum_step_s':.001});save()
  report['solve']=await c.call('nx_motion_solve Cycle');save();minf=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in minf.splitlines() if any(t in line.lower() for t in ['degrees of freedom','number of drivers','redundan','constraint violation','error'])];save()
  counts=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',minf,re.M);assert counts and int(counts[-1])==0;assert re.search(r'Number of Drivers\s*=\s*2',minf);assert report['audit']['solver_parameters']['solver']==1
  await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.7');await asyncio.sleep(.5);await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results CM');save()
  markers={row['marker']:np.array([a['values'] for a in row['coordinates']]).T for row in report['markers']['markers']};a=markers['CMCarriage'];theta=np.linspace(0,2*np.pi,steps+1);expected=np.array([base+law_fn(t)[0] for t in theta]);assert len(a)==steps+1
  error=float(np.max(abs(a[:,1]-expected)));cross=float(np.max(np.abs(a[:,[0,2]]-np.array([0,26]))));report['cross_validation']={'max_follower_position_error_mm':error,'cross_axis_error_mm':cross,'native_stroke_mm':float(np.ptp(a[:,1])),'expected_stroke_mm':lift,'independent_expected_y_mm':expected.tolist()};save();assert error<.02 and cross<.01
  if family=='completion_cam_groove_quintic':
   difference=float(max(abs(a[:,1]-np.array([40+law(t)[0] for t in theta]))));report['cross_validation']['native_deviation_from_previous_cycloidal_law_mm']=difference;save();assert difference>.1
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in range(steps+1):
   await c.call(f'nx_motion_frame Cycle {frame}');r=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(r);save()
   if r['interference_count']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual cam mechanism collision; retain fault before repair')
  await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part')
  await c.call('nx_motion_driver_velocity LocalSpin 137');await c.call(f'nx_motion_solution LocalSpinTest normal {duration} {steps}');await c.call('nx_motion_tighten LocalSpinTest 1e-9');await c.call('nx_motion_solve LocalSpinTest');await c.call('nx_motion_frame LocalSpinTest 0');spin=await c.call('nx_motion_marker_results CM');sp={row['marker']:np.array([v['values'] for v in row['coordinates']]).T for row in spin['markers']};delta=float(np.max(np.abs(sp['CMCarriage']-a)));report['local_dof_validation']={'total_mobility':2,'functional_mobility':1,'local_spin_dof':1,'baseline_spin_deg_s':0,'changed_spin_deg_s':137,'max_follower_change_mm':delta,'native_markers':spin};save();assert delta<.01
  await c.call('nx_motion_frame LocalSpinTest finish');await c.call('nx_motion_driver_velocity LocalSpin 0');await c.call('nx_motion_solve Cycle');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update({'simulation':str(m/'mechanism.sim'),'solution':'Cycle','motion_passed':True});p.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main())
