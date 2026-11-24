import asyncio,json,shutil
from pathlib import Path
from phase4_common import Run,Client,LOG
from mechanism_agent.repair import RepairBudget
async def main():
 meta=json.loads((LOG/'current-delta.json').read_text(encoding='utf-8'));run=Run.__new__(Run);run.family='delta';run.meta=meta;run.plan=meta['plan'];run.model=Path(meta['models']);run.out=Path(meta['logs']);run.parts=json.loads((run.out/'parts-verified.json').read_text());run.counter=200;refs=json.loads((run.out/'joint-references.json').read_text());backup=run.out/'before_datum_repair'
 for name in ['joint-references.json','native-component-datums.json']:shutil.copy2(run.out/name,backup/name)
 async with Client() as c:
  assert not (await c.call('nx_status'))['part']
  for part,items in refs.items():
   await c.call('nx_open_part '+str(run.model/(part+'.prt')))
   for name,row in list(items.items()):
    pt=[row['origin'][x] for x in ['X','Y','Z']];axis=[row['axis'][x] for x in ['X','Y','Z']];items[name]=await c.call('nx_joint_reference '+' '.join(map(str,[name+'R1',*pt,*axis])))
   await c.call('nx_save_part');await c.call('nx_close_part 0')
  run.save('joint-references.json',refs);await c.call('nx_open_part '+run.meta['assembly'])
  actual={name:await c.call('nx_component_datums '+name.upper()) for name in run.parts};run.save('native-component-datums-repaired.json',actual)
  for name,rs in refs.items():
   points={x['name'] for x in actual[name]['prototype_points'] if x['occurrence_available']};curves={x['name'] for x in actual[name]['prototype_curves'] if x['occurrence_available']}
   for r in rs.values():assert r['point_name'] in points and r['curve_name'] in curves
  await c.call('nx_save_part')
  after=run.out/'after_datum_repair';after.mkdir()
  for file in run.model.glob('*.prt'):shutil.copy2(file,after/file.name)
  RepairBudget(run.out/'repairs','datum-persistence').finish(1,{'all_named_points_and_axes_reopened':True},after/'assembly.prt',run.out/'native-component-datums-repaired.json',True)
  def ref(part,key,kind):return ('point:' if kind=='point' else 'curve:')+refs[part][key][kind+'_name']
  async def constrain(kind,name,a,keya,b,keyb,geometry='point',value=0):
   return await c.call(f'nx_constraint {kind} {name} {a.upper()} {ref(a,keya,geometry)} {b.upper()} {ref(b,keyb,geometry)} {value} same')
  for i in range(1,4):
   upper=f'upper{i}';await constrain('touch',f'Base{i}_center',upper,'BASE','ground',f'BASE{i}');await constrain('align',f'Base{i}_axis',upper,'BASE','ground',f'BASE{i}','curve')
   for side in ['L','R']:
    rod=f'rod{i}{side.lower()}';await constrain('touch',f'U{i}{side}_center',upper,'U'+side,rod,'U');await constrain('angle',f'U{i}{side}_crosspin',upper,'U'+side,rod,'U','curve',90);await constrain('touch',f'S{i}{side}_center',rod,'S','platform',f'S{i}{side}')
  state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);await c.call('nx_save_part');assert state['all_prototypes_fully_loaded'] and state['unsatisfied_constraint_count']==0
  inter=await c.call('nx_interference');run.save('static-interference.json',inter);await run.export(c,'assembly');await c.call('nx_fit_view');assert not any(x['nx_result']==1 for x in inter['pairs'])
  run.meta['build_passed']=True;run.pointer()


if __name__=='__main__':asyncio.run(main())
