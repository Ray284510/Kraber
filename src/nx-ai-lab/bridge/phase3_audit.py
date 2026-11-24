import asyncio,json
from pathlib import Path
from phase3_client import Client,LOG
async def main():
 async with Client() as c:
  out={}
  for cmd in [('nx_open_part '+str(ROOT / 'models\\phase2_robot_20261107_134652\\robot_motion.sim')),'nx_motion_setup','nx_motion_audit','nx_motion_state','nx_motion_frame DrivenMotion 0','nx_motion_transforms 0','nx_motion_frame DrivenMotion 80','nx_motion_transforms 80','nx_motion_frame DrivenMotion finish','nx_close_part 0']:
   out[cmd]=await c.call(cmd)
  (LOG/'phase2-cold-audit.json').write_text(json.dumps(out,indent=2))
asyncio.run(main())
