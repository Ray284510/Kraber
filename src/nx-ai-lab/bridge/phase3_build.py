"""Execute the recorded spatial design using the real NX MCP bridge."""
import asyncio,datetime,json,math,shutil
from pathlib import Path
from phase3_client import Client,ROOT,LOG
PLAN=json.loads((ROOT/'bridge/phase3-design.json').read_text())
STAMP=datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
MODEL=ROOT/('models/phase3_robot_'+STAMP);OUT=LOG/('robot_'+STAMP)
def add(a,b,s=1):return [a[i]+s*b[i] for i in range(3)]
def unit(v):
 n=math.sqrt(sum(x*x for x in v));return [x/n for x in v]
def radial(p,axis,target):
 v=add(target,p,-1);dot=sum(x*y for x,y in zip(v,axis));v=add(v,axis,-dot)
 if sum(x*x for x in v)<1e-8:v=[1,0,0] if abs(axis[0])<.9 else [0,1,0]
 return add(p,unit(v),10)
async def main():
 MODEL.mkdir(parents=True);OUT.mkdir();(OUT/'design-before-modeling.json').write_text(json.dumps(PLAN,indent=2))
 run={'models':str(MODEL),'logs':str(OUT),'plan':PLAN,'assembly':str(MODEL/'robot.prt')};(LOG/'current-robot.json').write_text(json.dumps(run,indent=2))
 centers=PLAN['joint_centers_mm'];axes=PLAN['joint_axes'];parts={}
 async with Client() as c:
  status=await c.call('nx_status');assert not status.get('part'),status
  async def shape(name,o,axis,w,depth,body=None,mode='create',kind='cylinder',h=1):
   name=name+'_'+str(shape.counter);shape.counter+=1
   r=await c.call('nx_shape '+' '.join(map(str,[name,kind,*o,*axis,w,h,depth,mode,body or '-'])));return r['body_id']
  shape.counter=0
  async def cylinder(name,center,axis,start,end,dia,body=None,mode='create'):return await shape(name,add(center,axis,start),axis,dia,end-start,body,mode)
  async def beam(name,p,q,body):
   d=add(q,p,-1);return await shape(name,p,unit(d),PLAN['link_strut_diameter_mm'],math.sqrt(sum(x*x for x in d)),body)
  async def finish(name,color):
   await c.call(f'nx_color {color}');await c.call('nx_fit_view');r=await c.call('nx_inspect')
   (OUT/(name+'-inspection.json')).write_text(json.dumps(r,indent=2))
   assert len(r['bodies'])==1 and r['bodies'][0]['solid'] and r['bodies'][0]['consistency_fault_count']==0 and r['bodies'][0]['volume_mm3']>0,r
   assert r['failed_feature_count']==0 and not any(f['errors'] for f in r['features']),r
   assert all(s['degrees_of_freedom']==0 for s in r['sketches']),r
   await c.call('nx_save_part');await c.call('nx_export_step '+str(MODEL/(name+'.step')))
   for wait_step in range(60):
    export=MODEL/(name+'.step')
    if export.exists() and 'END-ISO-10303-21;' in export.read_text(errors='replace')[-200:]:break
    await asyncio.sleep(1)
   else:raise RuntimeError('STEP export did not finish before part close')
   await c.call('nx_close_part 0');await c.call('nx_open_part '+str(MODEL/(name+'.prt')))
   rr=await c.call('nx_inspect');assert abs(rr['bodies'][0]['volume_mm3']-r['bodies'][0]['volume_mm3'])<1e-5
   parts[name]={'created':r,'reopened':rr};(OUT/'parts-verified.json').write_text(json.dumps(parts,indent=2));await c.call('nx_close_part 0')
  for i in range(7):
   name=f'link{i}';await c.call('nx_create_part '+str(MODEL/(name+'.prt')))
   if i==0:
    body=await shape('base_plate',[0,0,0],[0,0,1],70,10,kind='box',h=70)
    edges=(await c.call('nx_list_edges '+body))['edges'];ids=[str(e['index']) for e in edges if e['direction']=='Z'];await c.call(f'nx_blend_strict {body} 3 '+','.join(ids))
    body=await cylinder('pedestal',centers[0],axes[0],-20,-8,32,body)
    body=await cylinder('parent_boss',centers[0],axes[0],-8,-.3,28,body)
    body=await cylinder('shaft_bore',centers[0],axes[0],-8,-.3,8.4,body,'subtract')
    body=await cylinder('head_access',centers[0],axes[0],-30,-8,12.4,body,'subtract')
    for x in [-25,25]:
     for y in [-25,25]:body=await shape('mount_hole',[x,y,0],[0,0,1],5.5,10,body,'subtract')
   else:
    center=centers[i-1];axis=axes[i-1];body=await cylinder('child_boss',center,axis,.3,8,28)
    if i<6:
     nxt=centers[i];na=axes[i];p=add(center,axis,4.15);q=add(nxt,na,-4.15)
     a=radial(p,axis,q);b=radial(q,na,p)
     body=await beam('spatial_strut',a,b,body);body=await cylinder('parent_boss',nxt,na,-8,-.3,28,body)
     body=await cylinder('parent_bore',nxt,na,-12,12,8.4,body,'subtract')
    else:
     p=add(center,axis,4.15);body=await beam('tool_standoff',add(p,[1,0,0],10),[280,0,238],body)
     body=await shape('tool_flange',[270,0,236],[0,0,1],26,4,body,kind='box',h=26)
     for dx in [-7,7]:
      for dy in [-7,7]:body=await shape('tool_mount',[270+dx,dy,236],[0,0,1],3.2,4,body,'subtract')
    body=await cylinder('sleeve_bore',center,axis,-12,12,10.1,body,'subtract')
   await finish(name,[216,186,36,101,211,186,36][i])
  for i,(center,axis) in enumerate(zip(centers,axes),1):
   for typ in ['pin','bushing','collar']:
    name=f'{typ}{i}';await c.call('nx_create_part '+str(MODEL/(name+'.prt')))
    if typ=='pin':
     body=await cylinder('shaft',center,axis,-8,11.3,8);body=await cylinder('shaft_head',center,axis,-10,-8,12,body)
    else:
     start,end,od,id=(.3,8,10,8.4) if typ=='bushing' else (8.2,11.2,12,8.1)
     body=await cylinder('sleeve_outer',center,axis,start,end,od);body=await cylinder('sleeve_inner',center,axis,start,end,id,body,'subtract')
    await finish(name,192 if typ!='bushing' else 6)
  run['part_names']=list(parts);(LOG/'current-robot.json').write_text(json.dumps(run,indent=2))
  await c.call('nx_create_part '+str(MODEL/'robot.prt'))
  for name in parts:await c.call(f'nx_add_component {MODEL/(name+".prt")} {name.upper()} 0 0 0')
  await c.call('nx_constraint fix Grounded LINK0 component -')
  for i in range(1,7):
   child=f'LINK{i}';parent=f'LINK{i-1}';center=centers[i-1];axis=axes[i-1]
   cg=await c.call('nx_component_geometry '+child);pg=await c.call('nx_component_geometry '+parent)
   def bore(g,radius):
    rows=[f for f in g['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-radius)<1e-4 and abs(sum(f['direction'][k]*axis[k] for k in range(3)))>.999]
    def distance(f):
     v=add(f['point_mm'],center,-1);d=sum(v[k]*axis[k] for k in range(3));return sum((v[k]-d*axis[k])**2 for k in range(3))
    return min(rows,key=distance)['reference']
   def seat(g,offset,sign):
    return min((f for f in g['faces'] if f['kind']=='Planar' and sum(f['direction'][k]*axis[k] for k in range(3))*sign>.999),key=lambda f:abs(sum((f['point_mm'][k]-center[k])*axis[k] for k in range(3))-offset))['reference']
   cb=bore(cg,5.05);pb=bore(pg,4.2);cs=seat(cg,.3,-1);ps=seat(pg,-.3,1)
   await c.call(f'nx_constraint axis J{i}_axis {child} {cb} {parent} {pb} 0 same')
   await c.call(f'nx_constraint distance J{i}_seat {child} {cs} {parent} {ps} 0.6 opposite')
   state=await c.call('nx_assembly_state '+parent);d=next(x for x in state['components'] if x['name']==child);assert d['dof_result']=='Success' and (d['translation_dof'],d['rotation_dof'])==(0,1),state
   (OUT/f'joint{i}-relative-dof.json').write_text(json.dumps(state,indent=2))
   for hardware,owner in [(f'PIN{i}',parent),(f'COLLAR{i}',f'PIN{i}'),(f'BUSHING{i}',child)]:await c.call(f'nx_constraint bond {hardware}_fixed {hardware} component {owner} component 0 same')
   if i==1:
    await c.call(f'nx_constraint distance FaultSeat {child} {cs} {parent} {ps} 2 opposite',True)
    before=await c.call('nx_assembly_state');assert before['unsatisfied_constraint_count']>0
    await c.call('nx_save_part');shutil.copy2(MODEL/'robot.prt',MODEL/'constraint_fault_before.prt')
    await c.call('nx_constraint_value FaultSeat 0.6');after=await c.call('nx_assembly_state');assert after['unsatisfied_constraint_count']==0
    await c.call('nx_constraint_suppress FaultSeat 1');await c.call('nx_save_part');shutil.copy2(MODEL/'robot.prt',MODEL/'constraint_fault_after.prt')
    (OUT/'constraint-repair.json').write_text(json.dumps({'before':before,'after':after,'rounds':1,'max_rounds':3,'cause':'Conflicting 2 mm seat constraint versus designed 0.6 mm axial clearance','repair':'Correct dimension, then suppress redundant diagnostic constraint; no component translation'},indent=2))
  state=await c.call('nx_assembly_state');assert state['unsatisfied_constraint_count']==0
  inter=await c.call('nx_interference');(OUT/'assembly-interference.json').write_text(json.dumps(inter,indent=2));(OUT/'assembly-state.json').write_text(json.dumps(state,indent=2))
  await c.call('nx_fit_view');await c.call('nx_save_part');await c.call('nx_export_step '+str(MODEL/'robot.step'))
  assert not any(p['nx_result']==1 for p in inter['pairs']),inter
if __name__=='__main__':asyncio.run(main())

