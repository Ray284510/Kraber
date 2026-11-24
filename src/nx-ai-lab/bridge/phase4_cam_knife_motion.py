import asyncio,json,re
from pathlib import Path
import numpy as np
from phase4_common import Client,ROOT,LOG
from mechanism_agent.cam_plan import law
async def main():
 p=LOG/'current-cam_knife.json';run=json.loads(p.read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);report={};plan=run['plan']
 def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
 async with Client() as c:
  assert run['build_passed'];assert (await c.call('nx_status'))['part']==run['assembly']
  await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
  for name,fixed,parts in [('Link0',1,['GROUND']),('Cam',0,['CAM','CAMSHAFT']),('Follower',0,['FOLLOWER'])]:await c.call(f'nx_motion_link {name} {fixed} '+' '.join(parts))
  for j in plan['joints']:await c.call(f'nx_motion_joint {j["name"]} {j["type"]} {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
  cam=await c.call('nx_component_geometry CAM');report['geometry']={'cam':cam};save()
  ce=min((e for e in cam['edges'] if e['kind']=='Spline'),key=lambda e:abs(e['bbox_mm'][2]-20))['reference']
  report['contact']=await c.call(f'nx_motion_point_curve CamContact Follower 0 35 20 CAM {ce}');report['higher_pairs']=await c.call('nx_motion_higher_pairs');report['unactuated']=await c.call('nx_motion_solution Free articulation 4 240');await c.call('nx_motion_driver Input CamAxis revolute 90');await c.call('nx_motion_marker CMFollower Follower 0 35 20');await c.call('nx_motion_marker CMCam Cam 20 0 24');report['driven']=await c.call('nx_motion_solution Cycle normal 4 240');report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save()
  report['solve']=await c.call('nx_motion_solve Cycle');save();minf=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in minf.splitlines() if any(t in line.lower() for t in ['degrees of freedom','number of drivers','redundan','constraint violation','error'])];save()
  counts=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',minf,re.M);assert counts and int(counts[-1])==0;assert re.search(r'Number of Drivers\s*=\s*1',minf);assert report['audit']['solver_parameters']['solver']==1
  await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.7');await asyncio.sleep(.5);await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results CM');save()
  markers={row['marker']:np.array([a['values'] for a in row['coordinates']]).T for row in report['markers']['markers']};a=markers['CMFollower'];theta=np.linspace(0,2*np.pi,241);expected=np.array([35+law(t)[0] for t in theta]);assert len(a)==241
  error=float(np.max(abs(a[:,1]-expected)));cross=float(np.max(np.abs(a[:,[0,2]]-np.array([0,20]))));report['cross_validation']={'max_follower_position_error_mm':error,'cross_axis_error_mm':cross,'native_stroke_mm':float(np.ptp(a[:,1])),'expected_stroke_mm':12,'independent_expected_y_mm':expected.tolist()};save();assert error<.02 and cross<.01
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in range(241):
   await c.call(f'nx_motion_frame Cycle {frame}');r=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(r);save()
   if r['interference_count']:
    volumes=await c.call(f'nx_motion_collision_volumes {frame}');report['frames'][-1]['native_intersections']=volumes;save()
    allowed={frozenset(['GROUND','CAMSHAFT']),frozenset(['CAM','FOLLOWER'])}
    bad=[v for v in volumes['intersections'] if frozenset([v['a'],v['b']]) not in allowed or v.get('body_count',-1)!=0 or v.get('volume_mm3',-1)!=0]
    if bad:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Positive native intersection or unplanned contact: '+str(bad))
  await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update({'simulation':str(m/'mechanism.sim'),'solution':'Cycle','motion_passed':True});p.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main())
