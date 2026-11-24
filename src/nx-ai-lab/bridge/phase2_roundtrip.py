import asyncio,json,math
from pathlib import Path
from phase2_client import Client,ROOT,LOG
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());model=Path(run['models']);out=Path(run['logs']);parts=json.loads((out/'parts-verified.json').read_text());result={}
 async with Client() as c:
  for name in run['part_names']+['robot']:
   step=model/(name+'.step');assert step.exists() and 'END-ISO-10303-21;' in step.read_text(errors='replace')[-200:],str(step)
   await c.call('nx_import_step '+str(step)+' '+str(model/(name+'_step_reopened.prt')))
   inspect=await c.call('nx_inspect');assert inspect['bodies'] and all(b['solid'] and b['consistency_fault_count']==0 for b in inspect['bodies']),inspect
   volume=sum(b['volume_mm3'] for b in inspect['bodies'])
   expected=(parts[name]['created']['bodies'][0]['volume_mm3'] if name!='robot' else sum(parts[n]['created']['bodies'][0]['volume_mm3'] for n in ['base','link1','link2','link3','link4','pin_base']+['pin_joint']*3+['collar']*4))
   assert math.isclose(volume,expected,rel_tol=1e-7,abs_tol=.001),(name,volume,expected)
   assert len(inspect['bodies'])==(13 if name=='robot' else 1),(name,inspect)
   await c.call('nx_save_part');await c.call('nx_close_part 1');await c.call('nx_open_part '+str(model/(name+'_step_reopened.prt')));reopened=await c.call('nx_inspect')
   result[name]={'step':str(step),'imported':inspect,'reopened':reopened,'expected_volume_mm3':expected,'volume_error_mm3':volume-expected,'passed':True};(out/'step-roundtrip.json').write_text(json.dumps(result,indent=2),encoding='utf-8');await c.call('nx_close_part 1')
  await c.call('nx_open_part '+run['assembly']);state=await c.call('nx_assembly_state');(out/'assembly-reopened.json').write_text(json.dumps(state,indent=2),encoding='utf-8');assert state['unsatisfied_constraint_count']==0,state
  await c.call('nx_close_part 1')
if __name__=='__main__':asyncio.run(main())
