import asyncio,json,datetime
from pathlib import Path
from phase2_client import Client,ROOT,LOG
MODEL=ROOT/('models/phase2_constraint_'+datetime.datetime.now().strftime('%Y%m%d_%H%M%S'))
async def main():
 MODEL.mkdir(exist_ok=False)
 async with Client() as c:
  for name,w,h,depth,hole in [('base',40,40,8,8.4),('arm',80,20,6,8.4)]:
   await c.call('nx_create_part '+str(MODEL/(name+'.prt')))
   sk=(await c.call('nx_create_sketch XY'))['sketch_id'];await c.call(f'nx_sketch_rectangle {sk} {0 if name=="base" else 30} 0 {w} {h}')
   await c.call(f'nx_dimension_rectangle {sk} {name} {w} {h}');await c.call('nx_finish_sketch '+sk)
   b=(await c.call(f'nx_extrude {sk} {depth} 0 0 create'))['body_id'];await c.call(f'nx_hole {b} 0 0 {hole} {depth} 0')
   r=await c.call('nx_inspect');assert all(s['degrees_of_freedom']==0 for s in r['sketches']),r
   await c.call('nx_save_part');await c.call('nx_close_part 1')
  await c.call('nx_create_part '+str(MODEL/'test.prt'))
  await c.call('nx_add_component '+str(MODEL/'base.prt')+' BASE 0 0 0')
  await c.call('nx_add_component '+str(MODEL/'arm.prt')+' ARM 0 0 8.3')
  await c.call('nx_constraint fix ground BASE component -')
  geos={}
  for name in ('BASE','ARM'):geos[name]=await c.call('nx_component_geometry '+name)
  (LOG/'constraint-v2-geometry.json').write_text(json.dumps(geos,indent=2),encoding='utf-8')
  axis={n:next(f['reference'] for f in g['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-4.2)<1e-5) for n,g in geos.items()}
  await c.call(f'nx_constraint axis pivot_axis ARM {axis["ARM"]} BASE {axis["BASE"]} 0 same')
  cylindrical=await c.call('nx_assembly_state');assert next(x for x in cylindrical['components'] if x['name']=='ARM')['translation_dof']==1,cylindrical
  bottom=next(f['reference'] for f in geos['ARM']['faces'] if f['kind']=='Planar' and f['direction'][2]<-.9)
  top=next(f['reference'] for f in geos['BASE']['faces'] if f['kind']=='Planar' and f['direction'][2]>.9)
  await c.call(f'nx_constraint distance gap ARM {bottom} BASE {top} 0.3 opposite')
  state=await c.call('nx_assembly_state');d=next(x for x in state['components'] if x['name']=='ARM');assert d['rotation_dof']==1 and d['translation_dof']==0,state
  (LOG/'constraint-revolute-pass.json').write_text(json.dumps(state,indent=2),encoding='utf-8')
  # Deliberate incompatible axial distances; repair the dimension, not the component position.
  fail=await c.call(f'nx_constraint distance conflicting_gap ARM {bottom} BASE {top} 2 opposite',True)
  before=await c.call('nx_assembly_state');assert before['unsatisfied_constraint_count']>0,before
  await c.call('nx_constraint_value conflicting_gap 0.3')
  after=await c.call('nx_assembly_state');assert after['unsatisfied_constraint_count']==0,after
  (LOG/'constraint-conflict-repair.json').write_text(json.dumps({'model':str(MODEL),'failure':fail,'before':before,'repair_rounds':1,'max_rounds':3,'repair':'Change conflicting_gap from 2 to 0.3 mm','after':after},indent=2),encoding='utf-8')
  await c.call('nx_constraint_suppress conflicting_gap 1')
  # Remove axial seat; a parallel side face removes rotation and leaves a true sliding DOF.
  await c.call('nx_constraint_suppress gap 1')
  geos={n:await c.call('nx_component_geometry '+n) for n in ('BASE','ARM')}
  side={n:next(f['reference'] for f in g['faces'] if f['kind']=='Planar' and f['direction'][1]>.9) for n,g in geos.items()}
  await c.call(f'nx_constraint align slide_orientation ARM {side["ARM"]} BASE {side["BASE"]} 0 same')
  slide=await c.call('nx_assembly_state');d=next(x for x in slide['components'] if x['name']=='ARM');assert (d['translation_dof'],d['rotation_dof'])==(1,0),slide
  (LOG/'constraint-slider-pass.json').write_text(json.dumps(slide,indent=2),encoding='utf-8')
  await c.call('nx_constraint_suppress slide_orientation 1');await c.call('nx_constraint_suppress gap 0')
  await c.call(f'nx_constraint angle pose_angle ARM {side["ARM"]} BASE {side["BASE"]} 30 same')
  angle=await c.call('nx_assembly_state');d=next(x for x in angle['components'] if x['name']=='ARM');assert (d['translation_dof'],d['rotation_dof'])==(0,0),angle
  await c.call('nx_constraint_suppress pose_angle 1')
  (LOG/'constraint-types-pass.json').write_text(json.dumps({'angle':angle,'model':str(MODEL)},indent=2),encoding='utf-8')
  await c.call('nx_fit_view');await c.call('nx_save_part')
if __name__=='__main__':asyncio.run(main())
