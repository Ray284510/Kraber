import asyncio,json,math
from phase4_common import Run,Client,ROOT,LOG
from mechanism_agent.cam_plan import design,profile
async def main(design_fn=design,profile_fn=profile,family='cam_groove'):
 assert json.loads((LOG/'acceptance.json').read_text())['fourbar']['passed']
 plan=design_fn();plan['parameters'].update({'follower':'roller_in_closed_groove','groove_radial_clearance':.2,'groove_depth':4,'roller_thickness':3.6,'disk_radius':65,'floor_clearance':.2});plan['higher_pair']={'type':'PointOnCurve','link':'Carriage','curve':'PitchCurve','point':[0,40,26]};plan['independent_calculation'].update({'total_mobility':2,'local_roller_spin_dof':1,'functional_mobility':1,'closure':'Physical closed groove with 0.2 mm radial running clearance; ideal nominal pitch-line kinematics. Contact forces, backlash dynamics and no-slip rolling are not validated.'});plan['construction']='Groove has inner and outer offset walls and an intact floor. Roller moves within real clearance. Local spin is gauged separately and tested for no effect on follower travel.';plan['redesign_reason']='Third contact-check repair: choose positive-closure grooved disk instead of zero-gap external contact; create real 0.2 mm running clearance, not altered checks or component translations.';plan['parameters'].update({'guide_center_y':90,'base_center_y':25,'base_length':180});plan['guide_repair']='Shift guide geometry from y=80 to y=90 and extend base, because disk radius 65 intersects original rails starting at y=60; components stay at identity transforms.'
 if family!='cam_groove':plan['redesign_reason']='Additional motion law in a new independent configuration, reusing the accepted physical groove construction; no inherited acceptance.'
 run=Run(family,plan)
 for name,base,roller in [('inner',34.8,5.2),('outer',45.2,-5.2),('pitch',40,0)]:run.model.joinpath(name+'.csv').write_text('\n'.join(','.join(f'{x:.12f}' for x in profile_fn(i*math.pi/360,base=base,roller=roller)) for i in range(720)))
 async with Client() as c:
  assert not (await c.call('nx_status')).get('part'), 'A part is already open; preserve it before starting a new build'
  await c.call('nx_create_part '+str(run.model/'cam.prt'));await c.call('nx_application modeling');await asyncio.sleep(1);body=await run.cylinder(c,'disk',[0,0],20,130,8)
  r=await c.call(f'nx_profile_boolean GrooveOuter {run.model/"outer.csv"} 24 4 subtract {body}');body=r['body_id'];r=await c.call(f'nx_profile_boolean GrooveInner {run.model/"inner.csv"} 24 4 unite {body}');body=r['body_id']
  body=await run.cylinder(c,'shaft_bore',[0,0],20,8.4,8,body,'subtract');await c.call(f'nx_profile_curve PitchCurve {run.model/"pitch.csv"} 26');await run.finish(c,'cam',186)
  await c.call('nx_create_part '+str(run.model/'ground.prt'));body=await run.shape(c,'base',[0,25,0],[0,0,1],140,5,kind='box',height=180)
  body=await run.cylinder(c,'bearing',[0,0],5,20,14.6,body);body=await run.cylinder(c,'bore',[0,0],0,8.4,19.6,body,'subtract')
  for x in [-12,12]:body=await run.shape(c,'guide',[x,90,5],[0,0,1],10,40,body,kind='box',height=40)
  await run.finish(c,'ground',103)
  await c.call('nx_create_part '+str(run.model/'camshaft.prt'));body=await run.cylinder(c,'shaft',[0,0],-.2,8,28.2);body=await run.cylinder(c,'head',[0,0],-2.2,12,2,body);await run.finish(c,'camshaft',36)
  await c.call('nx_create_part '+str(run.model/'carriage.prt'));body=await run.shape(c,'slider',[0,65,30],[0,0,1],10,6,kind='box',height=40)
  body=await run.shape(c,'arm',[0,40,33],[0,1,0],6,25,body);body=await run.cylinder(c,'pivot',[0,40],28.4,10,7.6,body);body=await run.cylinder(c,'pin',[0,40],24.4,4,4,body);await run.finish(c,'carriage',211)
  await c.call('nx_create_part '+str(run.model/'roller.prt'));body=await run.cylinder(c,'roller',[0,40],24.2,10,3.6);body=await run.cylinder(c,'bore',[0,40],24.2,4.2,3.6,body,'subtract');await run.finish(c,'roller',14)
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
  await c.call(f'nx_constraint distance RollerSeat ROLLER {plane("ROLLER",2,1,27.8)} CARRIAGE {plane("CARRIAGE",2,-1,28.4)} 0.6 opposite')
  state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0
  interference=await c.call('nx_interference');run.save('static-interference.json',interference);assert all(x['nx_result']!=1 for x in interference['pairs'])
  await c.call('nx_save_part');await run.export(c,'assembly');run.meta['build_passed']=True;run.pointer()
if __name__=='__main__':asyncio.run(main())
