import asyncio,json,math,sys
from pathlib import Path
from phase4_common import Run,Client,LOG
from mechanism_agent.gear_plan import design
async def main():
 assert json.loads((LOG/'acceptance.json').read_text())['cam_groove']['passed'];plan,profiles=design()
 if '--resume-carrier' in sys.argv:
  meta=json.loads((LOG/'current-planetary.json').read_text(encoding='utf-8'));run=Run.__new__(Run);run.family='planetary';run.meta=meta;run.plan=meta['plan'];run.model=Path(meta['models']);run.out=Path(meta['logs']);run.parts=json.loads((run.out/'parts-verified.json').read_text());run.counter=100;plan=run.plan
 else:run=Run('planetary',plan)
 for name,points in ({} if '--resume-carrier' in sys.argv else profiles).items():(run.model/(name+'.csv')).write_text('\n'.join(','.join(f'{x:.12f}' for x in p) for p in points))
 async with Client() as c:
  if '--resume-carrier' in sys.argv:
   assert not (await c.call('nx_status')).get('part');await c.call('nx_open_part '+str(run.model/'carrier.prt'));await run.finish(c,'carrier',36)
  else:
   assert not (await c.call('nx_status')).get('part'), 'A part is already open; preserve it before starting a new build'
   await c.call('nx_create_part '+str(run.model/'ground.prt'));await c.call('nx_application modeling');await asyncio.sleep(1);body=await run.cylinder(c,'base',[0,0],0,150,5)
   for deg in [60,180,300]:body=await run.cylinder(c,'ring_mount',[65*math.cos(math.radians(deg)),65*math.sin(math.radians(deg))],5,6,15,body)
   body=await run.cylinder(c,'bearing_bore',[0,0],0,12.4,5,body,'subtract');await run.finish(c,'ground',103)
   await c.call('nx_create_part '+str(run.model/'ring.prt'));body=await run.cylinder(c,'ring_blank',[0,0],20,136,8);r=await c.call(f'nx_profile_boolean InternalTeeth {run.model/"ring_void.csv"} 20 8 subtract {body} polyline');await run.finish(c,'ring',211)
   await c.call('nx_create_part '+str(run.model/'carrier.prt'));body=await run.cylinder(c,'carrier_plate',[0,0],10,106,6);body=await run.cylinder(c,'bearing_hub',[0,0],0,12,10,body);body=await run.cylinder(c,'sun_bore',[0,0],0,8.4,16,body,'subtract')
   for xy in plan['planet_centers']:body=await run.cylinder(c,'planet_pin',xy,16,6,12,body)
   await run.finish(c,'carrier',36)
  await c.call('nx_create_part '+str(run.model/'sun.prt'));r=await c.call(f'nx_profile_solid SunTeeth {run.model/"sun.csv"} 20 8 polyline');body=await run.cylinder(c,'sun_shaft',[0,0],0,8,20,r['body_id']);await run.finish(c,'sun',186)
  for i,xy in enumerate(plan['planet_centers']):
   name=f'planet{i+1}';await c.call('nx_create_part '+str(run.model/(name+'.prt')));r=await c.call(f'nx_profile_solid PlanetTeeth {run.model/(name+".csv")} 20 8 polyline');body=await run.cylinder(c,'planet_bore',xy,20,6.4,8,r['body_id'],'subtract');await run.finish(c,name,14+i*20)
  await run.add_all(c);await c.call('nx_constraint fix Base GROUND component GROUND component 0 same');await c.call('nx_constraint bond RingFixed RING component GROUND component 0 same')
  geos={n:await c.call('nx_component_geometry '+n) for n in ['GROUND','CARRIER','SUN','PLANET1','PLANET2','PLANET3']}
  def cyl(n,r,xy):return min((f for f in geos[n]['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-r)<1e-5),key=lambda f:sum((f['point_mm'][i]-xy[i])**2 for i in range(2)))['reference']
  def plane(n,z,sign):return min((f for f in geos[n]['faces'] if f['kind']=='Planar' and f['direction'][2]*sign>.99),key=lambda f:abs(f['point_mm'][2]-z))['reference']
  for name,child,parent,xy,rc,rp,zc,zp in [('Carrier','CARRIER','GROUND',[0,0],6,6.2,10,5),('Sun','SUN','CARRIER',[0,0],4,4.2,20,16)]+[(f'Planet{i+1}',f'PLANET{i+1}','CARRIER',xy,3.2,3,20,16) for i,xy in enumerate(plan['planet_centers'])]:
   await c.call(f'nx_constraint axis {name}Axis {child} {cyl(child,rc,xy)} {parent} {cyl(parent,rp,xy)} 0 same');await c.call(f'nx_constraint distance {name}Seat {child} {plane(child,zc,-1)} {parent} {plane(parent,zp,1)} {zc-zp} opposite')
  state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0
  interference=await c.call('nx_interference');run.save('static-interference.json',interference);assert all(x['nx_result']!=1 for x in interference['pairs']);await c.call('nx_save_part');await run.export(c,'assembly');run.meta['build_passed']=True;run.pointer()
if __name__=='__main__':asyncio.run(main())
