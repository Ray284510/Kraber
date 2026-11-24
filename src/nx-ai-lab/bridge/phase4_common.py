import asyncio,datetime,json,math,sys,shutil,hashlib,subprocess
from pathlib import Path
from phase4_client import Client,ROOT,LOG
sys.path.insert(0,str(ROOT))
class Run:
 def __init__(self,family,plan):
  from mechanism_agent.contract import validate_plan
  validate_plan(plan)
  stamp=datetime.datetime.now().strftime('%Y%m%d_%H%M%S');self.family=family;self.model=ROOT/f'models/phase4_{family}_{stamp}';self.out=LOG/f'{family}_{stamp}';self.model.mkdir(parents=True);self.out.mkdir();self.plan=plan;self.parts={};self.counter=0
  self.meta={'family':family,'models':str(self.model),'logs':str(self.out),'plan':plan,'assembly':str(self.model/'assembly.prt')};self.save('design-before-modeling.json',plan)
  files=[p for folder in ['bridge','mechanism_agent'] for p in (ROOT/folder).glob('*') if p.suffix in ('.py','.cs')]+[ROOT/'nx-user/startup/NX12_Loader.dll']
  provenance={'captured_before_modeling':datetime.datetime.now().astimezone().isoformat(),'git_commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),'git_status':subprocess.check_output(['git','status','--porcelain'],cwd=ROOT,text=True),'sha256':{str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in files if p.exists()}}
  self.save('source-before-modeling.json',provenance);self.meta['source_snapshot']=str(self.out/'source-before-modeling.json');self.pointer()
 def save(self,name,value): (self.out/name).write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
 def pointer(self): (LOG/f'current-{self.family}.json').write_text(json.dumps(self.meta,ensure_ascii=False,indent=2),encoding='utf-8')
 async def shape(self,c,name,o,axis,width,depth,body=None,mode='create',kind='cylinder',height=1):
  self.counter+=1;r=await c.call('nx_shape '+' '.join(map(str,[name+'_'+str(self.counter),kind,*o,*axis,width,height,depth,mode,body or '-'])));await c.call('nx_save_part');return r['body_id']
 async def cylinder(self,c,name,xy,z,diameter,depth,body=None,mode='create'):return await self.shape(c,name,[*xy,z],[0,0,1],diameter,depth,body,mode)
 async def bar(self,c,name,a,b,z,width=18,thickness=6,bore=6.4):
  await c.call('nx_create_part '+str(self.model/(name+'.prt')))
  body=await self.cylinder(c,'end1',a,z,width,thickness);d=[b[i]-a[i] for i in range(2)];length=math.hypot(*d)
  body=await self.shape(c,'web',[*a,z+thickness/2],[*d,0],thickness,length,body)
  body=await self.cylinder(c,'end2',b,z,width,thickness,body)
  for p in [a,b]:body=await self.cylinder(c,'bore',p,z,bore,thickness,body,'subtract')
  return body
 async def finish(self,c,name,color=186):
  await c.call(f'nx_color {color}');await c.call('nx_fit_view');r=await c.call('nx_inspect');self.save(name+'-inspection.json',r)
  assert len(r['bodies'])==1 and r['bodies'][0]['solid'] and r['bodies'][0]['volume_mm3']>0 and r['bodies'][0]['consistency_fault_count']==0
  assert r['failed_feature_count']==0 and not any(f['errors'] for f in r['features']);assert all(s['degrees_of_freedom']==0 for s in r['sketches'])
  await c.call('nx_save_part');await self.export(c,name);await c.call('nx_close_part 0');await c.call('nx_open_part '+str(self.model/(name+'.prt')));rr=await c.call('nx_inspect');assert math.isclose(rr['bodies'][0]['volume_mm3'],r['bodies'][0]['volume_mm3'],rel_tol=1e-9,abs_tol=1e-5)
  self.parts[name]={'created':r,'reopened':rr};self.save('parts-verified.json',self.parts);self.meta['part_names']=list(self.parts);self.pointer();await c.call('nx_close_part 0')
 async def export(self,c,name):
  path=self.model/(name+'.step');await c.call('nx_export_step '+str(path))
  for _ in range(60):
   if path.exists() and 'END-ISO-10303-21;' in path.read_text(errors='replace')[-200:]:return
   await asyncio.sleep(1)
  raise RuntimeError('STEP not completed within bounded wait')
 async def add_all(self,c):
  await c.call('nx_create_part '+self.meta['assembly'])
  for name in self.parts:await c.call(f'nx_add_component {self.model/(name+".prt")} {name.upper()} 0 0 0')
 async def revolute(self,c,name,child,parent,xy,child_bottom,parent_top,diagnostic=False):
  cg=await c.call('nx_component_geometry '+child);pg=await c.call('nx_component_geometry '+parent)
  def bore(g):return min((f for f in g['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-3.2)<1e-4),key=lambda f:sum((f['point_mm'][i]-xy[i])**2 for i in range(2)))['reference']
  def seat(g,z,sign):return min((f for f in g['faces'] if f['kind']=='Planar' and f['direction'][2]*sign>.99),key=lambda f:abs(f['point_mm'][2]-z))['reference']
  cb,pb=bore(cg),bore(pg);cs,ps=seat(cg,child_bottom,-1),seat(pg,parent_top,1);gap=child_bottom-parent_top
  await c.call(f'nx_constraint axis {name}_axis {child} {cb} {parent} {pb} 0 same');await c.call(f'nx_constraint distance {name}_seat {child} {cs} {parent} {ps} {gap} opposite')
  if diagnostic:
   await c.call(f'nx_constraint distance FaultSeat {child} {cs} {parent} {ps} {gap+1} opposite',True);before=await c.call('nx_assembly_state');assert before['unsatisfied_constraint_count']>0;await c.call('nx_save_part');shutil.copy2(self.model/'assembly.prt',self.model/'fault_before.prt')
   await c.call(f'nx_constraint_value FaultSeat {gap}');after=await c.call('nx_assembly_state');assert after['unsatisfied_constraint_count']==0;await c.call('nx_constraint_suppress FaultSeat 1');await c.call('nx_save_part');shutil.copy2(self.model/'assembly.prt',self.model/'fault_after.prt');self.save('constraint-repair.json',{'before':before,'after':after,'rounds':1,'max_rounds':3,'repair':'Correct contradictory axial clearance expression; no component translation'})
