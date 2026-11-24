import asyncio,json,datetime
from pathlib import Path
from phase2_client import Client,ROOT,LOG
async def main():
 async with Client() as c:
  await c.call(('nx_open_part '+str(ROOT / 'models\\phase2_constraint_20261107_133212\\test.prt')))
  path=ROOT/('models/phase2_constraint_20261107_133212/motion_axis_test_'+datetime.datetime.now().strftime('%H%M%S')+'.sim')
  await c.call('nx_motion_new '+str(path));await c.call('nx_motion_setup')
  await c.call('nx_motion_link Link0 1 BASE');await c.call('nx_motion_link Link1 0 ARM')
  geometry=await c.call('nx_component_geometry ARM')
  hole=next(f for f in geometry['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-4.2)<1e-6)
  await c.call('nx_motion_joint Joint1 Revolute Link1 Link0 0 0 8.15 1 0 0')
  await c.call('nx_motion_driver Drive1 Joint1 revolute 30')
  await c.call('nx_motion_solution BadAxis normal 2 40');await c.call('nx_save_part');await c.call('nx_motion_solve BadAxis')
  await c.call('nx_motion_frame BadAxis 0');await c.call('nx_motion_collision_setup')
  before=[]
  for i in range(0,41,10):
   await c.call(f'nx_motion_frame BadAxis {i}');before.append(await c.call(f'nx_motion_collision_check {i}'))
  assert any(x['interference_count']>0 for x in before),'Positive interference control did not trigger'
  await c.call('nx_motion_frame BadAxis finish')
  diagnosis={'observed_hole_axis':hole['direction'],'incorrect_joint_axis':[1,0,0],'reason':'The revolute axis must align with the cylindrical bearing bore, not the plate length direction. Correct joint definition, not component position.'}
  await c.call('nx_motion_joint_axis Joint1 0 0 1')
  await c.call('nx_motion_solution RepairedAxis normal 2 40');await c.call('nx_save_part');await c.call('nx_motion_solve RepairedAxis')
  after=[]
  for i in range(41):
   await c.call(f'nx_motion_frame RepairedAxis {i}');after.append(await c.call(f'nx_motion_collision_check {i}'))
  assert all(x['interference_count']==0 for x in after),after
  await c.call('nx_motion_frame RepairedAxis finish');await c.call('nx_save_part');await c.call('nx_close_part 1')
  await c.call('nx_open_part '+str(path));await c.call('nx_motion_setup');state=await c.call('nx_motion_state');await c.call('nx_motion_frame RepairedAxis 40')
  report={'simulation':str(path),'before':before,'diagnosis':diagnosis,'repair_rounds':1,'maximum_repair_rounds':3,'after':after,'reopened_state':state,'passed':True}
  (LOG/'motion-axis-repair.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
  await c.call('nx_motion_frame RepairedAxis finish');await c.call('nx_close_part 1')
if __name__=='__main__':asyncio.run(main())
