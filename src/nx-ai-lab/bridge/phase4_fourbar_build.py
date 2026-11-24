import asyncio,json
from phase4_common import Run,Client,ROOT
from mechanism_agent.fourbar_plan import design
async def main():
 run=Run('fourbar',design());p=run.plan['initial_points'];O,A,C,D=[p[k] for k in ['O','A','C','D']]
 async with Client() as c:
  status=await c.call('nx_status');assert not status.get('part')
  await c.call('nx_create_part '+str(run.model/'ground.prt'));b=await run.shape(c,'base',[50,0,0],[0,0,1],140,4,kind='box',height=35)
  edges=(await c.call('nx_list_edges '+b))['edges'];await c.call(f'nx_blend_strict {b} 2 '+','.join(str(e['index']) for e in edges if e['direction']=='Z'))
  for pt in [O,D]:b=await run.cylinder(c,'pedestal',pt,4,18,14,b);b=await run.cylinder(c,'bearing_bore',pt,0,6.4,18,b,'subtract')
  await run.finish(c,'ground',216)
  for name,a,b,z,color in [('crank',O,A,18.4,186),('coupler',A,C,24.8,36),('rocker',D,C,18.4,211)]:await run.bar(c,name,a,b,z);await run.finish(c,name,color)
  for name,xy,z,end in [('pin_o',O,0,24.3),('pin_d',D,0,24.3),('pin_a',A,18.4,33.1),('pin_c',C,18.4,33.1)]:
   await c.call('nx_create_part '+str(run.model/(name+'.prt')));b=await run.cylinder(c,'shaft',xy,z,6,end-z);b=await run.cylinder(c,'head',xy,z-2,9,2,b);await run.finish(c,name,192)
  for name,xy in [('collar_a',A),('collar_c',C)]:
   await c.call('nx_create_part '+str(run.model/(name+'.prt')));b=await run.cylinder(c,'collar',xy,31,9,2);b=await run.cylinder(c,'collar_bore',xy,31,6.1,2,b,'subtract');await run.finish(c,name,192)
  await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -')
  await run.revolute(c,'J0','CRANK','GROUND',O,18.4,18,True);await run.revolute(c,'J3','ROCKER','GROUND',D,18.4,18)
  await run.revolute(c,'J1','COUPLER','CRANK',A,24.8,24.4);await run.revolute(c,'J2','COUPLER','ROCKER',C,24.8,24.4)
  for child,parent in [('PIN_O','GROUND'),('PIN_D','GROUND'),('PIN_A','CRANK'),('COLLAR_A','CRANK'),('PIN_C','ROCKER'),('COLLAR_C','ROCKER')]:await c.call(f'nx_constraint bond {child}_rigid {child} component {parent} component 0 same')
  state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0
  inter=await c.call('nx_interference');run.save('static-interference.json',inter);await c.call('nx_save_part');await run.export(c,'assembly');await c.call('nx_fit_view');assert not any(x['nx_result']==1 for x in inter['pairs'])
  run.meta['build_passed']=True;run.pointer()
if __name__=='__main__':asyncio.run(main())
