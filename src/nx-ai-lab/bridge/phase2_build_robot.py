"""Execute the separately recorded AI design through real MCP/NX operations."""
import asyncio,datetime,json,shutil
from pathlib import Path
from phase2_client import Client,ROOT,LOG
RUN='robot_'+datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
MODEL=ROOT/('models/phase2_'+RUN);OUT=LOG/RUN
PLAN=json.loads((ROOT/'work/phase2/robot-design.json').read_text(encoding='utf-8'))
async def main():
 MODEL.mkdir(parents=True);OUT.mkdir();(OUT/'design.json').write_text(json.dumps(PLAN,ensure_ascii=False,indent=2),encoding='utf-8')
 run={'models':str(MODEL),'logs':str(OUT),'design':str(OUT/'design.json')};(LOG/'current-robot.json').write_text(json.dumps(run,indent=2),encoding='utf-8')
 parts={}
 async with Client() as c:
  async def circle(dia,depth,start=0,body=None,plane='XY',cx=0,cy=0,mode='create'):
   sk=(await c.call('nx_create_sketch '+plane))['sketch_id'];await c.call(f'nx_sketch_circle {sk} {cx} {cy} {dia}');await c.call('nx_finish_sketch '+sk)
   r=await c.call(f'nx_extrude {sk} {depth} 0 {start} {mode}'+(' '+body if body else ''));return r.get('body_id',body)
  async def plate(name,cx,w,h,t):
   await c.call('nx_create_part '+str(MODEL/(name+'.prt')));sk=(await c.call('nx_create_sketch XY'))['sketch_id'];await c.call(f'nx_sketch_rectangle {sk} {cx} 0 {w} {h}');await c.call(f'nx_dimension_rectangle {sk} {name} {w} {h}');await c.call('nx_finish_sketch '+sk)
   body=(await c.call(f'nx_extrude {sk} {t} 0 0 create'))['body_id'];edges=(await c.call('nx_list_edges '+body))['edges'];ids=[str(e['index']) for e in edges if e['direction']=='Z'];assert len(ids)==4,edges
   await c.call(f'nx_blend_strict {body} 2 '+','.join(ids));return body
  async def finish(name,color):
   await c.call(f'nx_color {color}');await c.call('nx_fit_view');r=await c.call('nx_inspect')
   assert len(r['bodies'])==1 and r['bodies'][0]['solid'] and r['bodies'][0]['volume_mm3']>0 and r['bodies'][0]['consistency_fault_count']==0,r
   assert r['failed_feature_count']==0 and not any(f['errors'] for f in r['features']),r
   assert all(s['degrees_of_freedom']==0 for s in r['sketches']),r
   await c.call('nx_save_part');await c.call('nx_export_step '+str(MODEL/(name+'.step')))
   await c.call('nx_close_part 1');await c.call('nx_open_part '+str(MODEL/(name+'.prt')));reopened=await c.call('nx_inspect')
   assert abs(reopened['bodies'][0]['volume_mm3']-r['bodies'][0]['volume_mm3'])<1e-5,reopened
   parts[name]={'created':r,'reopened':reopened};(OUT/'parts-verified.json').write_text(json.dumps(parts,indent=2),encoding='utf-8');await c.call('nx_close_part 1')
  base=PLAN['base'];link=PLAN['links'];j=PLAN['joints'];th=link['thickness'];gap=j['axial_gap'];bore=link['bore_diameter']
  b=await plate('base',0,base['width'],base['height'],base['thickness']);await c.call(f'nx_hole {b} 0 0 {bore} {base["thickness"]} 0')
  for x in [-base['mounting_hole_offset'],base['mounting_hole_offset']]:
   for y in [-base['mounting_hole_offset'],base['mounting_hole_offset']]:await c.call(f'nx_hole {b} {x} {y} {base["mounting_hole_diameter"]} {base["thickness"]} 0')
  await finish('base',216)
  for i,length in enumerate(link['lengths'],1):
   name=f'link{i}';b=await plate(name,length/2,length+2*link['end_margin'],link['width'],th)
   for x in (0,length):await c.call(f'nx_hole {b} {x} 0 {bore} {th} 0')
   await finish(name,[186,36,101,211][i-1])
  for name,parent_t in [('pin_base',base['thickness']),('pin_joint',th)]:
   await c.call('nx_create_part '+str(MODEL/(name+'.prt')));shaft=parent_t+gap+th+j['collar_gap']+j['collar_thickness']+.1
   b=await circle(j['pin_diameter'],shaft);b=await circle(j['pin_head_diameter'],j['pin_head_thickness'],-j['pin_head_thickness'],b);await finish(name,192)
  await c.call('nx_create_part '+str(MODEL/'collar.prt'));b=await circle(j['collar_outer_diameter'],j['collar_thickness']);await c.call(f'nx_hole {b} 0 0 {j["collar_inner_diameter"]} {j["collar_thickness"]} 0')
  b=await circle(2.5,8,0,b,'XZ',0,j['collar_thickness']/2,'subtract');await finish('collar',192)
  await c.call('nx_create_part '+str(MODEL/'robot.prt'));await c.call('nx_add_component '+str(MODEL/'base.prt')+' BASE 0 0 0');await c.call('nx_constraint fix base_ground BASE component -')
  centers=[];x=0;parent='BASE';parent_z=0;parent_t=base['thickness'];previous_length=0
  for i,length in enumerate(link['lengths'],1):
   child=f'LINK{i}';z=parent_z+parent_t+gap;centers.append([x,0,parent_z+parent_t+gap/2])
   await c.call(f'nx_add_component {MODEL / ("link"+str(i)+".prt")} {child} {x} 0 {z}')
   g1=await c.call('nx_component_geometry '+child);g2=await c.call('nx_component_geometry '+parent)
   def axis(g):return min((f for f in g['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-bore/2)<1e-5),key=lambda f:abs(f['point_mm'][0]-x))['reference']
   bottom=next(f['reference'] for f in g1['faces'] if f['kind']=='Planar' and f['direction'][2]<-.9);top=next(f['reference'] for f in g2['faces'] if f['kind']=='Planar' and f['direction'][2]>.9)
   await c.call(f'nx_constraint axis J{i}_axis {child} {axis(g1)} {parent} {axis(g2)} 0 same');await c.call(f'nx_constraint distance J{i}_seat {child} {bottom} {parent} {top} {gap} opposite')
   state=await c.call('nx_assembly_state '+parent);d=next(v for v in state['components'] if v['name']==child);assert d['dof_result']=='Success' and (d['translation_dof'],d['rotation_dof'])==(0,1),state
   (OUT/f'joint{i}-relative-dof.json').write_text(json.dumps(state,indent=2),encoding='utf-8')
   if i==1:
    await c.call(f'nx_constraint distance wrong_seat {child} {bottom} {parent} {top} 2 opposite',True);before=await c.call('nx_assembly_state');assert before['unsatisfied_constraint_count']>0
    await c.call(f'nx_constraint_value wrong_seat {gap}');after=await c.call('nx_assembly_state');assert after['unsatisfied_constraint_count']==0
    (OUT/'constraint-repair.json').write_text(json.dumps({'before':before,'after':after,'repair':'Correct 2 mm conflicting seat dimension to design 0.4 mm','rounds':1,'max_rounds':3},indent=2),encoding='utf-8');await c.call('nx_constraint_suppress wrong_seat 1')
   pin=f'PIN{i}';collar=f'COLLAR{i}';pinfile='pin_base' if i==1 else 'pin_joint'
   await c.call(f'nx_add_component {MODEL / (pinfile+".prt")} {pin} {x} 0 {parent_z}');await c.call(f'nx_constraint bond {pin}_mount {pin} component {parent} component 0 same')
   await c.call(f'nx_add_component {MODEL / "collar.prt"} {collar} {x} 0 {z+th+j["collar_gap"]}');await c.call(f'nx_constraint bond {collar}_mount {collar} component {pin} component 0 same')
   parent=child;parent_z=z;parent_t=th;x+=length
  state=await c.call('nx_assembly_state');assert state['unsatisfied_constraint_count']==0,state
  interference=await c.call('nx_interference');(OUT/'assembly-interference.json').write_text(json.dumps(interference,indent=2),encoding='utf-8')
  (OUT/'assembly-state.json').write_text(json.dumps(state,indent=2),encoding='utf-8');(OUT/'joint-centers.json').write_text(json.dumps(centers),encoding='utf-8')
  await c.call('nx_fit_view');await c.call('nx_save_part');await c.call('nx_export_step '+str(MODEL/'robot.step'))
  run.update({'joint_centers':centers,'part_names':list(parts),'assembly':str(MODEL/'robot.prt')});(LOG/'current-robot.json').write_text(json.dumps(run,indent=2),encoding='utf-8')
if __name__=='__main__':asyncio.run(main())
