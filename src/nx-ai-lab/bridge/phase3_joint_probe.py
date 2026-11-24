import asyncio,json
from pathlib import Path
from phase3_client import Client,LOG
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());out=Path(run['logs'])
 async with Client() as c:
  await c.call('nx_open_part '+str(Path(run['models'])/'robot_motion.sim'));await c.call('nx_motion_setup');await c.call('nx_motion_frame Precision 0')
  r=await c.call('nx_motion_joint_results',True);(out/'native-joint-series.json').write_text(json.dumps(r,indent=2))
  await c.call('nx_motion_frame Precision finish')
asyncio.run(main())
