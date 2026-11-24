import asyncio,json
from pathlib import Path
from phase3_client import Client,LOG
from phase3_results import write_results
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());model=Path(run['models']);out=Path(run['logs']);report={'accepted_solution':'Precision'}
 async with Client() as c:
  report['audit']=await c.call('nx_motion_audit');assert report['audit']['solver_parameters']['solver']==1
  dofs={s['name']:s['gruebler_dof'] for s in report['audit']['solutions']};assert dofs['FreeMotion']==6 and dofs['Precision']==0
  await c.call('nx_application motion');await asyncio.sleep(.6);await c.call('nx_view iso 0.8');await asyncio.sleep(.3);await c.call('nx_motion_frame Precision 0')
  report['markers']=await c.call('nx_motion_marker_results EE');report['joints']=await c.call('nx_motion_joint_results');write_results(run['plan'],report['markers'],report['joints'],out)
  report['collision_setup']=await c.call('nx_motion_collision_setup');assert report['collision_setup']['body_count']==25 and report['collision_setup']['pair_count']==266
  report['frames']=[]
  for step in range(61):
   frame=await c.call(f'nx_motion_frame Precision {step}');collision=await c.call(f'nx_motion_collision_check {step}')
   report['frames'].append({'frame':frame,'collision':collision});(out/'accepted-motion-results.json').write_text(json.dumps(report,indent=2));assert collision['interference_count']==0
  await c.call('nx_motion_frame Precision finish');await c.call('nx_save_part');await c.call('nx_close_part 0')
  run['simulation']=str(model/'robot_motion.sim');run['solution']='Precision';(LOG/'current-robot.json').write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main())
