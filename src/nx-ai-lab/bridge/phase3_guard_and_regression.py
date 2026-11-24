import asyncio,json,shutil,datetime,hashlib
from pathlib import Path
from phase3_client import Client,LOG,ROOT
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());out=Path(run['logs']);model=Path(run['models']);report={}
 async with Client() as c:
  await c.call('nx_close_part 0')
  source=Path(run['simulation']);original=hashlib.sha256(source.read_bytes()).hexdigest();guard=model/('range_guard_'+datetime.datetime.now().strftime('%H%M%S')+'.sim');assert not guard.exists();shutil.copy2(source,guard)
  await c.call('nx_open_part '+str(guard));await c.call('nx_motion_setup');await c.call('nx_motion_solution RangeGuard normal 100 60')
  report['range_rejection']=await c.call('nx_motion_solve RangeGuard',True)
  assert report['range_rejection'].get('execution_started') is False and 'range exceeded' in report['range_rejection'].get('validation_error','')
  await c.call('nx_save_part');await c.call('nx_close_part 0');assert hashlib.sha256(source.read_bytes()).hexdigest()==original
  report['guard_model']=str(guard);report['accepted_sim_unchanged']=True
  old=json.loads((ROOT/'logs/phase2/current-robot.json').read_text());await c.call('nx_open_part '+old['simulation']);await c.call('nx_motion_setup')
  report['phase2_audit']=await c.call('nx_motion_audit');await c.call('nx_motion_frame DrivenMotion 0');report['phase2_collision_setup']=await c.call('nx_motion_collision_setup');assert report['phase2_collision_setup']['pair_count']==66
  report['phase2_collision_checks']=[]
  for step in [0,80]:
   await c.call(f'nx_motion_frame DrivenMotion {step}');r=await c.call(f'nx_motion_collision_check {step}');assert r['interference_count']==0;report['phase2_collision_checks'].append(r)
  await c.call('nx_motion_frame DrivenMotion finish');await c.call('nx_close_part 0')
  tools=await c.session.list_tools();report['mcp_tools']=[{'name':x.name,'inputSchema':x.inputSchema} for x in tools.tools]
  await c.call('nx_open_part '+run['simulation']);await c.call('nx_motion_setup');report['typed_calls']=[]
  for name,args in [('nx_motion_audit',{}),('nx_motion_state',{}),('nx_motion_frame',{'solution':'Precision','frame':0}),('nx_motion_marker_results',{'prefix':'EE'}),('nx_motion_joint_results',{}),('nx_motion_transforms',{'frame':0})]:
   raw=await c.session.call_tool(name,args);r=json.loads(next(x.text for x in raw.content if x.type=='text'));assert not raw.isError and r['ok'];report['typed_calls'].append({'tool':name,'args':args,'response':r,'mcp_is_error':raw.isError})
  await c.call('nx_motion_frame Precision finish');report['passed']=True;(out/'guard-regression-mcp.json').write_text(json.dumps(report,indent=2))
if __name__=='__main__':asyncio.run(main())
