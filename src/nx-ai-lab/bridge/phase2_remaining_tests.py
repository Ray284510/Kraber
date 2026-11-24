import asyncio,json,datetime
from pathlib import Path
from phase2_client import Client,ROOT,LOG
async def main():
 model=ROOT/('models/phase2_extra_'+datetime.datetime.now().strftime('%H%M%S'));model.mkdir();report={}
 async with Client() as c:
  await c.call('nx_create_part '+str(model/'test.prt'))
  for name,z in [('BASE',0),('ARM',8.3)]:await c.call(f"nx_add_component {ROOT / 'models/phase2_constraint_20261107_133212' / (name.lower() + '.prt')} {name} 0 0 {z}")
  await c.call('nx_constraint fix grounded BASE component -')
  gs={n:await c.call('nx_component_geometry '+n) for n in ('BASE','ARM')}
  axis={n:next(f['reference'] for f in g['faces'] if f['kind']=='Cylindrical') for n,g in gs.items()}
  bottom=next(f['reference'] for f in gs['ARM']['faces'] if f['direction'][2]<-.9 and f['kind']=='Planar');top=next(f['reference'] for f in gs['BASE']['faces'] if f['direction'][2]>.9 and f['kind']=='Planar')
  await c.call(f'nx_constraint axis axis ARM {axis["ARM"]} BASE {axis["BASE"]} 0 same');await c.call(f'nx_constraint touch contact ARM {bottom} BASE {top} 0 opposite')
  report['touch']=await c.call('nx_assembly_state');assert report['touch']['unsatisfied_constraint_count']==0
  await c.call('nx_constraint_suppress contact 1');await c.call('nx_constraint_suppress axis 1')
  gs={n:await c.call('nx_component_geometry '+n) for n in ('BASE','ARM')}
  ae=next(e['reference'] for e in gs['ARM']['edges'] if e['kind']=='Circular' and abs(e['bbox_mm'][2]-8)<1e-5);be=next(e['reference'] for e in gs['BASE']['edges'] if e['kind']=='Circular' and abs(e['bbox_mm'][2]-8)<1e-5)
  report['concentric_create']=await c.call(f'nx_constraint concentric circles ARM {ae} BASE {be} 0 same',True);report['concentric']=await c.call('nx_assembly_state')
  (LOG/'remaining-types.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
  assert report['concentric_create']['ok'],report
  await c.call('nx_constraint_suppress circles 1');await c.call('nx_constraint_suppress axis 0');await c.call(f'nx_constraint distance clearance ARM {bottom} BASE {top} 0.3 opposite');await c.call('nx_save_part')
  await c.call('nx_motion_new '+str(model/'slider.sim'));await c.call('nx_motion_setup');await c.call('nx_motion_link Link0 1 BASE');await c.call('nx_motion_link Link1 0 ARM');await c.call('nx_motion_joint Slider1 Slider Link1 Link0 0 0 8.15 0 0 1')
  report['slider_free']=await c.call('nx_motion_solution SliderFree articulation 2 20');assert report['slider_free']['gruebler_dof']==1
  report['slider_driver']=await c.call('nx_motion_driver SlideDrive Slider1 slider 1');report['slider_driven']=await c.call('nx_motion_solution SliderRun normal 2 20');assert report['slider_driven']['gruebler_dof']==0
  await c.call('nx_save_part');await c.call('nx_motion_solve SliderRun');report['slider_frames']=[]
  for i in [0,10,20]:report['slider_frames'].append(await c.call(f'nx_motion_frame SliderRun {i}'))
  await c.call('nx_motion_frame SliderRun finish');await c.call('nx_save_part');await c.call('nx_close_part 1');await c.call('nx_open_part '+str(model/'slider.sim'));await c.call('nx_motion_setup');report['slider_reopened']=await c.call('nx_motion_state')
  report['model']=str(model);(LOG/'remaining-types.json').write_text(json.dumps(report,indent=2),encoding='utf-8');await c.call('nx_close_part 1')
if __name__=='__main__':asyncio.run(main())
