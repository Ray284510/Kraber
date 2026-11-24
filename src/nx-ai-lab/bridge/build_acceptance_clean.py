from lab_paths import lab_path
import asyncio,json,os,sys,datetime
from pathlib import Path
from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
ROOT=lab_path('')
async def main():
 async with stdio_client(StdioServerParameters(command=sys.executable,args=[str(ROOT/'bridge/mcp_server.py')],env=dict(os.environ))) as (read,write):
  async with ClientSession(read,write) as session:
   await session.initialize()
   async def call(cmd):
    name=cmd if cmd in ('nx_status','nx_inspect') else 'nx_execute'
    raw=await session.call_tool(name,{} if name!='nx_execute' else {'command':cmd})
    r=json.loads(next(c.text for c in raw.content if c.type=='text'))
    rec={'time':datetime.datetime.now().isoformat(),'command':cmd,'response':r}
    with (ROOT/'logs/acceptance-clean-mcp.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(rec,ensure_ascii=False)+'\n')
    print(json.dumps(rec,ensure_ascii=False),flush=True)
    assert r.get('ok') and not raw.isError
    return r
   path=ROOT/'models/acceptance_plate_final.prt'
   await call('nx_create_part '+str(path))
   sk=(await call('nx_create_sketch XY'))['sketch_id']
   await call('nx_sketch_rectangle %s 0 0 80 50'%sk)
   await call('nx_dimension_rectangle %s plate 80 50'%sk)
   await call('nx_finish_sketch '+sk)
   b=(await call('nx_extrude %s 10 0 0 create'%sk))['body_id']
   await call('nx_hole %s 0 0 8 10 0'%b)
   edges=(await call('nx_list_edges '+b))['edges']
   selected=[e['index'] for e in edges if e['curve_type']=='Linear' and abs(e['start'][2]-10)<1e-6 and abs(e['end'][2]-10)<1e-6]
   assert len(selected)==4
   await call('nx_blend_strict %s 2 %s'%(b,','.join(map(str,selected))))
   before=await call('nx_inspect')
   assert len(before['bodies'])==1 and before['bodies'][0]['consistency_fault_count']==0
   assert all(not f['errors'] and not f['warnings'] for f in before['features'])
   assert before['sketches'][0]['degrees_of_freedom']==0
   await call('nx_fit_view');await call('nx_save_part')
   await call('nx_export_step '+str(path.with_suffix('.step')))
   await call('nx_close_part 1');await call('nx_open_part '+str(path))
   after=await call('nx_inspect')
   assert abs(before['bodies'][0]['volume_mm3']-after['bodies'][0]['volume_mm3'])<1e-6
   assert all(not f['errors'] for f in after['features'])
   (ROOT/'logs/acceptance-final-inspection.json').write_text(json.dumps(after,ensure_ascii=False,indent=2),encoding='utf-8')
   await call('nx_fit_view')
if __name__=='__main__':asyncio.run(main())
