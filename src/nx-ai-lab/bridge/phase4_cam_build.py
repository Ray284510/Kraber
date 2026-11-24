import asyncio,json,math
from phase4_common import Run,Client,ROOT,LOG
from mechanism_agent.cam_plan import design,profile
async def main():
 assert json.loads((LOG/'acceptance.json').read_text())['fourbar']['passed']
 run=Run('cam',design());run.model.joinpath('cam_profile.csv').write_text('\n'.join(','.join(f'{x:.12f}' for x in profile(i*math.pi/360)) for i in range(720)))
 async with Client() as c:
  assert not (await c.call('nx_status')).get('part'), 'A part is already open; preserve it before starting a new build'
  await c.call('nx_create_part '+str(run.model/'cam.prt'));await c.call('nx_application modeling');await asyncio.sleep(1);r=await c.call(f'nx_profile_solid WorkingCam {run.model/"cam_profile.csv"} 20 8');body=r['body_id'];body=await run.cylinder(c,'shaft_bore',[0,0],20,8.4,8,body,'subtract');await run.finish(c,'cam',186)
  await c.call('nx_create_part '+str(run.model/'ground.prt'));body=await run.shape(c,'base',[0,20,0],[0,0,1],140,5,kind='box',height=170)
  body=await run.cylinder(c,'bearing',[0,0],5,20,14.6,body);body=await run.cylinder(c,'bore',[0,0],0,8.4,19.6,body,'subtract')
  for x in [-12,12]:body=await run.shape(c,'guide',[x,80,5],[0,0,1],10,40,body,kind='box',height=40)
  await run.finish(c,'ground',103)
  await c.call('nx_create_part '+str(run.model/'camshaft.prt'));body=await run.cylinder(c,'shaft',[0,0],0,8,28);body=await run.cylinder(c,'head',[0,0],-2,12,2,body);await run.finish(c,'camshaft',36)
  await c.call('nx_create_part '+str(run.model/'carriage.prt'));body=await run.shape(c,'slider',[0,65,30],[0,0,1],10,6,kind='box',height=40)
  body=await run.shape(c,'arm',[0,40,33],[0,1,0],6,25,body);body=await run.cylinder(c,'pivot',[0,40],28.4,10,7.6,body);body=await run.cylinder(c,'pin',[0,40],20.4,4,8,body);await run.finish(c,'carriage',211)
  await c.call('nx_create_part '+str(run.model/'roller.prt'));body=await run.cylinder(c,'roller',[0,40],20,10,8);body=await run.cylinder(c,'bore',[0,40],20,4.2,8,body,'subtract');await run.finish(c,'roller',14)
  await run.add_all(c);await c.call('nx_constraint fix Base GROUND component GROUND component 0 same')
  geos={n:await c.call('nx_component_geometry '+n) for n in ['CAM','GROUND','CARRIAGE','ROLLER']};run.save('assembly-geometry.json',geos)
  def cylinder(n,r):return min((f for f in geos[n]['faces'] if f['kind']=='Cylindrical'),key=lambda f:abs(f['radius_mm']-r))['reference']
  def plane(n,axis,sign,value):return min((f for f in geos[n]['faces'] if f['kind']=='Planar' and f['direction'][axis]*sign>.99),key=lambda f:abs(f['point_mm'][axis]-value))['reference']
  await c.call(f'nx_constraint axis CamAxis CAM {cylinder("CAM",4.2)} GROUND {cylinder("GROUND",4.2)} 0 same')
  await c.call(f'nx_constraint distance CamSeat CAM {plane("CAM",2,-1,20)} GROUND {plane("GROUND",2,1,19.6)} 0.4 opposite')
  await c.call('nx_constraint bond ShaftBond CAMSHAFT component CAM component 0 same')
  await c.call(f'nx_constraint distance GuideSide CARRIAGE {plane("CARRIAGE",0,1,5)} GROUND {plane("GROUND",0,-1,7)} 2 opposite')
  await c.call(f'nx_constraint distance GuideHeight CARRIAGE {plane("CARRIAGE",2,-1,30)} GROUND {plane("GROUND",2,1,5)} 25 opposite')
  await c.call(f'nx_constraint axis RollerAxis ROLLER {cylinder("ROLLER",2.1)} CARRIAGE {cylinder("CARRIAGE",2)} 0 same')
  await c.call(f'nx_constraint distance RollerSeat ROLLER {plane("ROLLER",2,1,28)} CARRIAGE {plane("CARRIAGE",2,-1,28.4)} 0.4 opposite')
  state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0
  interference=await c.call('nx_interference');run.save('static-interference.json',interference);assert all(x['nx_result']!=1 for x in interference['pairs'])
  await c.call('nx_save_part');await run.export(c,'assembly');run.meta['build_passed']=True;run.pointer()
if __name__=='__main__':asyncio.run(main())
