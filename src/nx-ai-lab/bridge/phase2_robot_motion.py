import asyncio,json,math
from pathlib import Path
from phase2_client import Client,ROOT,LOG
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());model=Path(run['models']);out=Path(run['logs']);plan=json.loads(Path(run['design']).read_text(encoding='utf-8'));motion=plan['motion'];report={}
 async with Client() as c:
  state=await c.call('nx_assembly_state');assert state['unsatisfied_constraint_count']==0
  check=json.loads((out/'assembly-interference.json').read_text());assert not any(x['nx_result']==1 for x in check['pairs']),check
  await c.call('nx_motion_new '+str(model/'robot_motion.sim'));await c.call('nx_motion_setup')
  for i in range(5):
   comps=['BASE' if i==0 else f'LINK{i}']
   if i<4:comps += [f'PIN{i+1}',f'COLLAR{i+1}']
   await c.call(f'nx_motion_link Link{i} {int(i==0)} '+' '.join(comps))
  for i,center in enumerate(run['joint_centers'],1):
   lo,hi=motion['joint_range_degrees'][i-1];await c.call(f'nx_motion_joint Joint{i} Revolute Link{i} Link{i-1} '+' '.join(map(str,center))+f' 0 0 1 {lo} {hi}')
  r=await c.call('nx_motion_solution FreeMotion articulation 4 80');assert r['gruebler_dof']==4,r;report['unactuated']=await c.call('nx_motion_state')
  for i,vel in enumerate(motion['velocity_degrees_per_second'],1):
   d=await c.call(f'nx_motion_driver Drive{i} Joint{i} revolute {vel}');assert d['velocity_unit']=='DegreesPerSecond',d
  s=await c.call(f'nx_motion_solution DrivenMotion normal {motion["duration_seconds"]} {motion["steps"]}');assert s['gruebler_dof']==0,s
  report['driven']=await c.call('nx_motion_state');await c.call('nx_save_part');report['solve']=await c.call('nx_motion_solve DrivenMotion');await c.call('nx_motion_frame DrivenMotion 0');await c.call('nx_fit_view')
  report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for step in range(motion['steps']+1):
   frame=await c.call(f'nx_motion_frame DrivenMotion {step}');assert frame['number_steps']==motion['steps']+1
   collision=await c.call(f'nx_motion_collision_check {step}');report['frames'].append({'frame':frame,'collision':collision})
   (out/'motion-results.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
   if collision['interference_count']:raise RuntimeError('Actual NX motion interference; stop and diagnose before repair: '+json.dumps(collision))
  await c.call('nx_motion_frame DrivenMotion finish')
  # Reference linear edges are selected from actual NX geometry after simulation creation.
  edges={}
  for name in ['BASE','LINK1','LINK2','LINK3','LINK4']:
   g=await c.call('nx_component_geometry '+name)
   edges[name]=next(e['reference'] for e in g['edges'] if e['kind']=='Linear' and e['bbox_mm'][3]-e['bbox_mm'][0]>10 and abs(e['bbox_mm'][4]-e['bbox_mm'][1])<1e-6 and abs(e['bbox_mm'][5]-e['bbox_mm'][2])<1e-6)
  report['angles']=[]
  for step in [0,motion['steps']]:
   await c.call(f'nx_motion_frame DrivenMotion {step}')
   for i in range(1,5):
    parent='BASE' if i==1 else f'LINK{i-1}';child=f'LINK{i}';a=await c.call(f'nx_motion_angle {child} {edges[child]} {parent} {edges[parent]} {step}');report['angles'].append({'joint':i,'measurement':a})
  await c.call('nx_motion_frame DrivenMotion finish');await c.call('nx_save_part');await c.call('nx_close_part 1');await c.call('nx_open_part '+str(model/'robot_motion.sim'));await c.call('nx_motion_setup');report['reopened']=await c.call('nx_motion_state');await c.call('nx_motion_frame DrivenMotion 80')
  report['saved_reopened']=True;(out/'motion-results.json').write_text(json.dumps(report,indent=2),encoding='utf-8');run['simulation']=str(model/'robot_motion.sim');(LOG/'current-robot.json').write_text(json.dumps(run,indent=2),encoding='utf-8')
if __name__=='__main__':asyncio.run(main())
