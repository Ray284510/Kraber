import asyncio,json,datetime,shutil
from pathlib import Path
from phase3_client import Client,ROOT,LOG
async def main():
 old=json.loads((ROOT/'logs/phase2/current-robot.json').read_text());source=Path(old['models']);dest=ROOT/('models/phase3_phase2_cold_'+datetime.datetime.now().strftime('%Y%m%d_%H%M%S'));dest.mkdir()
 for name in old['part_names']+['robot']:shutil.copy2(source/(name+'.prt'),dest/(name+'.prt'))
 shutil.copy2(Path(old['simulation']),dest/'robot_motion.sim');report={'pid':int((ROOT/'logs/nx-gui-pid.txt').read_text()),'model_copy':str(dest),'native_results_copied':False}
 async with Client() as c:
  status=await c.call('nx_status');assert not status.get('part')
  await c.call('nx_open_part '+str(dest/'robot_motion.sim'));await c.call('nx_motion_setup');report['audit']=await c.call('nx_motion_audit');report['solve']=await c.call('nx_motion_solve DrivenMotion')
  await c.call('nx_motion_frame DrivenMotion 0');report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for frame in [0,40,80]:
   f=await c.call(f'nx_motion_frame DrivenMotion {frame}');t=await c.call(f'nx_motion_transforms {frame}');collision=await c.call(f'nx_motion_collision_check {frame}');assert collision['interference_count']==0;report['frames'].append({'frame':f,'transforms':t,'collision':collision})
  await c.call('nx_motion_frame DrivenMotion finish');await c.call('nx_close_part 0')
 report['native_result_files']=[{'path':str(x),'size':x.stat().st_size,'mtime':x.stat().st_mtime} for x in dest.glob('robot_motion-drivenmotion.*')];assert any(x['path'].endswith('.mres') and x['size']>0 for x in report['native_result_files'])
 report['passed']=True;(LOG/'phase2-cold-solve.json').write_text(json.dumps(report,indent=2))
if __name__=='__main__':asyncio.run(main())
