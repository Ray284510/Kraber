import argparse,asyncio,json,math
import numpy as np
from phase4_common import Run,Client,LOG
from mechanism_agent.feeder_plan import design,REQUEST


async def main(radius,family,plan_override=None,selection_result=None):
 assert json.loads((LOG/'acceptance.json').read_text())['delta']['passed']
 plan=plan_override if plan_override is not None else design(radius);refs={};parameters=plan['parameters'];rod=parameters['rod_length'];slot_lo,slot_hi=parameters['adjustment_slot_radius_range_mm'];initial=math.radians(parameters['initial_angle_deg']);layout=plan.get('layout',{'base_x':60,'base_length':200,'guide_x':95,'guide_length':90})
 async with Client() as c:
  assert not (await c.call('nx_status')).get('part')
  selected=selection_result if selection_result is not None else await c.tool('nx_mechanism_select',{'requirement':REQUEST});assert selected['selected']==plan['selection']['selected']
  calculated=await c.tool('nx_mechanism_calculate',{'kind':'slider_crank','parameters':{'crank':radius,'rod':rod,'offset':0,'angles_deg':[0,90,180]}});assert calculated['result']['stroke_mm']==2*radius
  run=Run(family,plan);run.save('actual-mcp-selection.json',selected);run.save('actual-mcp-independent-calculation.json',calculated);O,A,B=[plan['initial_points'][x] for x in ['O','A','B']]
  async def datum(part,name,point):
   refs.setdefault(part,{})[name]=await c.call('nx_joint_reference '+' '.join(map(str,[name,*point,0,0,1])))
  async def finish(name,color):
   await run.finish(c,name,color);run.save('joint-references.json',refs)
  await c.call('nx_create_part '+str(run.model/'ground.prt'));await c.call('nx_application modeling');body=await run.shape(c,'base',[layout['base_x'],0,0],[0,0,1],layout['base_length'],4,kind='box',height=layout.get('base_width',60))
  body=await run.cylinder(c,'bearing',O,4,parameters.get('bearing_diameter',18),14,body);body=await run.cylinder(c,'bearing_bore',O,0,6.4,18,body,'subtract')
  for y in [-15,15]:body=await run.shape(c,'guide',[layout['guide_x'],y,4],[0,0,1],layout['guide_length'],14,body,kind='box',height=10)
  await datum('ground','BASE',[0,0,18.2]);await finish('ground',103)
  await c.call('nx_create_part '+str(run.model/'crank.prt'));body=await run.cylinder(c,'disk',O,18.4,parameters['crank_disk_diameter'],6);body=await run.cylinder(c,'root_bore',O,18.4,6.4,6,body,'subtract');u=np.array([math.cos(initial),math.sin(initial)]);n=np.array([-u[1],u[0]])
  for r in sorted(set([slot_lo,slot_hi])):body=await run.cylinder(c,'slot_end',(r*u).tolist(),18.4,6.4,6,body,'subtract')
  if slot_hi>slot_lo:
   points=[slot_lo*u-3.2*n,slot_hi*u-3.2*n,slot_hi*u+3.2*n,slot_lo*u+3.2*n];csvpath=run.model/'slot_web.csv';csvpath.write_text('\n'.join(','.join(map(str,p)) for p in points));res=await c.call(f'nx_profile_boolean AdjustmentSlot {csvpath} 18.4 6 subtract {body} polyline');body=res['body_id']
  await datum('crank','BASE',[0,0,18.2]);await datum('crank','A',A+[24.6]);await finish('crank',186)
  await run.bar(c,'coupler',A,B,24.8,width=14);await datum('coupler','A',A+[24.6]);await datum('coupler','B',B+[24.6]);await finish('coupler',36)
  await c.call('nx_create_part '+str(run.model/'slider.prt'));body=await run.shape(c,'slider',B+[10],[0,0,1],20,14.4,kind='box',height=18);body=await run.cylinder(c,'pivot_bore',B,10,6.4,14.4,body,'subtract')
  for dx,dy in [(-6,5),(6,-5)]:body=await run.cylinder(c,'feed_mount',[B[0]+dx,dy],10,3.2,14.4,body,'subtract')
  await datum('slider','B',B+[24.6]);await finish('slider',211)
  for name,xy,z,end,head_top in [('pin_o',O,0,24.3,0),('pin_a',A,18.2,33.1,18.2),('pin_b',B,9.8,33.1,9.8)]:
   await c.call('nx_create_part '+str(run.model/(name+'.prt')));body=await run.cylinder(c,'shaft',xy,z,6,end-z);body=await run.cylinder(c,'head',xy,head_top-2,10,2,body);await finish(name,192)
  for name,xy in [('collar_a',A),('collar_b',B)]:
   await c.call('nx_create_part '+str(run.model/(name+'.prt')));body=await run.cylinder(c,'collar',xy,31,10,2);body=await run.cylinder(c,'collar_bore',xy,31,6.1,2,body,'subtract');await finish(name,192)
  await run.add_all(c);await c.call('nx_constraint fix BaseFixed GROUND component -')
  actual={name:await c.call('nx_component_datums '+name.upper()) for name in refs};run.save('native-component-datums.json',actual)
  for name,items in refs.items():
   points={x['name'] for x in actual[name]['prototype_points'] if x['occurrence_available']};curves={x['name'] for x in actual[name]['prototype_curves'] if x['occurrence_available']}
   for item in items.values():assert item['point_name'] in points and item['curve_name'] in curves
  async def hinge(name,a,keya,b,keyb):
   for kind,geometry in [('touch','point'),('align','curve')]:
    ra=('point:' if geometry=='point' else 'curve:')+refs[a][keya][geometry+'_name'];rb=('point:' if geometry=='point' else 'curve:')+refs[b][keyb][geometry+'_name'];await c.call(f'nx_constraint {kind} {name}_{kind} {a.upper()} {ra} {b.upper()} {rb} 0 same')
  await hinge('Input','crank','BASE','ground','BASE')
  geos={name:await c.call('nx_component_geometry '+name) for name in ['SLIDER','GROUND']};run.save('guide-geometry.json',geos)
  def plane(name,axis,sign,value):return min((f for f in geos[name]['faces'] if f['kind']=='Planar' and f['direction'][axis]*sign>.99),key=lambda f:abs(f['point_mm'][axis]-value))['reference']
  await c.call(f'nx_constraint distance GuideSide SLIDER {plane("SLIDER",1,1,9)} GROUND {plane("GROUND",1,-1,10)} 1 opposite');await c.call(f'nx_constraint distance GuideHeight SLIDER {plane("SLIDER",2,-1,10)} GROUND {plane("GROUND",2,1,4)} 6 opposite')
  await hinge('CrankPin','coupler','A','crank','A');await hinge('SliderPin','coupler','B','slider','B')
  for child,parent in [('PIN_O','GROUND'),('PIN_A','CRANK'),('COLLAR_A','CRANK'),('PIN_B','SLIDER'),('COLLAR_B','SLIDER')]:await c.call(f'nx_constraint bond {child}_clamped {child} component {parent} component 0 same')
  state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);await c.call('nx_save_part');assert state['all_prototypes_fully_loaded'] and state['unsatisfied_constraint_count']==0 and all(x['dof_result']=='Success' for x in state['components'])
  inter=await c.call('nx_interference');run.save('static-interference.json',inter);await run.export(c,'assembly');await c.call('nx_fit_view');assert not any(x['nx_result']==1 for x in inter['pairs']);run.meta['build_passed']=True;run.pointer()


if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('--radius',type=float,default=20);parser.add_argument('--family',default='feeder');args=parser.parse_args();asyncio.run(main(args.radius,args.family))
