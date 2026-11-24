import asyncio,json,shutil
from pathlib import Path
from phase3_client import Client,LOG
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());model=Path(run['models']);out=Path(run['logs']);results={}
 async with Client() as c:
  await c.call('nx_open_part '+str(model/'robot_motion.sim'));await c.call('nx_motion_setup')
  for name,point in [('EE0',[270,0,240]),('EEX',[280,0,240]),('EEY',[270,10,240]),('EEZ',[270,0,250])]:results[name]=await c.call(f'nx_motion_marker {name} Link6 '+' '.join(map(str,point)))
  await c.call('nx_motion_solution Precision normal 3 60');await c.call('nx_save_part');await c.call('nx_motion_solve Precision');await c.call('nx_motion_frame Precision 0')
  results['marker_results']=await c.call('nx_motion_marker_results EE',True)
  (out/'precision-marker-probe.json').write_text(json.dumps(results,indent=2))
  await c.call('nx_motion_frame Precision finish');await c.call('nx_save_part')
asyncio.run(main())
