"""Adaptive MCP modeling: every next operation uses verified NX results."""
from lab_paths import lab_path
import asyncio, json, os, sys, datetime
from pathlib import Path
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
ROOT=lab_path('')
MODELS=ROOT/'models/joint'
MODELS.mkdir(exist_ok=True)

async def main():
    params=StdioServerParameters(command=sys.executable,args=[str(ROOT/'bridge/mcp_server.py')],env=dict(os.environ))
    async with stdio_client(params) as (read,write):
      async with ClientSession(read,write) as session:
        await session.initialize()
        async def call(cmd):
            tool=cmd if cmd in ('nx_status','nx_inspect') else 'nx_execute'
            raw=await session.call_tool(tool,{} if tool!='nx_execute' else {'command':cmd})
            r=json.loads(next(c.text for c in raw.content if c.type=='text'))
            event={'time':datetime.datetime.now().isoformat(),'command':cmd,'response':r,'mcp_is_error':raw.isError}
            with (ROOT/'logs/joint-mcp.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(event,ensure_ascii=False)+'\n')
            print(cmd+': '+json.dumps(r,ensure_ascii=False),flush=True)
            if raw.isError or not r.get('ok'):raise RuntimeError('NX operation failed: '+cmd)
            return r
        async def rect(prefix,cx,cy,w,h,depth,z=0):
            s=(await call('nx_create_sketch XY'))['sketch_id']
            await call('nx_sketch_rectangle %s %s %s %s %s'%(s,cx,cy,w,h))
            await call('nx_dimension_rectangle %s %s %s %s'%(s,prefix,w,h))
            await call('nx_finish_sketch '+s)
            return (await call('nx_extrude %s %s 0 %s create'%(s,depth,z)))['body_id']
        async def circle(diameter,depth,z=0,target=None):
            s=(await call('nx_create_sketch XY'))['sketch_id']
            await call('nx_sketch_circle %s 0 0 %s'%(s,diameter))
            await call('nx_finish_sketch '+s)
            return (await call('nx_extrude %s %s 0 %s create%s'%(s,depth,z,(' '+target) if target else '')))['body_id']
        async def check(name):
            r=await call('nx_inspect')
            assert len(r['bodies'])==1
            assert all(b['solid'] and b['consistency_fault_count']==0 and b['volume_mm3']>0 for b in r['bodies'])
            assert all(not f['errors'] for f in r['features'])
            (ROOT/('logs/'+name+'-inspection.json')).write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8')
        async def finish(name):
            await check(name)
            await call('nx_fit_view');await call('nx_save_part')
            await call('nx_export_step '+str(MODELS/(name+'.step')))
            await call('nx_close_part 1')

        await call('nx_close_part 1')
        await call('nx_create_part '+str(MODELS/'joint_base.prt'))
        b=await rect('base',0,0,80,60,8)
        e=(await call('nx_list_edges '+b))['edges']
        vertical=','.join(str(x['index']) for x in e if x['direction']=='Z')
        await call('nx_blend_strict %s 4 %s'%(b,vertical))
        b=await circle(28,14,8,b)
        await call('nx_hole %s 0 0 12.2 22 0'%b)
        for x in (-30,30):
            for y in (-20,20):await call('nx_hole %s %s %s 5.5 8 0'%(b,x,y))
        await finish('joint_base')

        await call('nx_create_part '+str(MODELS/'joint_arm.prt'))
        b=await rect('arm',25,0,90,24,8)
        e=(await call('nx_list_edges '+b))['edges']
        vertical=','.join(str(x['index']) for x in e if x['direction']=='Z')
        await call('nx_blend_strict %s 5 %s'%(b,vertical))
        await call('nx_hole %s 0 0 12.4 8 0'%b)
        for x in (45,60):await call('nx_hole %s %s 0 5.5 8 0'%(b,x))
        await finish('joint_arm')

        await call('nx_create_part '+str(MODELS/'joint_pin.prt'))
        b=await circle(12,31.2)
        b=await circle(18,3,31.2,b)
        await finish('joint_pin')

        await call('nx_create_part '+str(MODELS/'joint_shim.prt'))
        b=await circle(24,0.2)
        await call('nx_hole %s 0 0 12.4 0.2 0'%b)
        await finish('joint_shim')

        await call('nx_create_part '+str(MODELS/'joint_assembly.prt'))
        for name,z in [('joint_base',0),('joint_arm',22.2),('joint_pin',-1),('joint_shim',22)]:
            await call('nx_add_component %s %s 0 0 %s'%(MODELS/(name+'.prt'),name,z))
        r=await call('nx_interference')
        (ROOT/'logs/joint-interference.json').write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8')
        assert r['body_occurrences']==4 and len(r['pairs'])==6
        assert all(p['nx_result'] in (2,3) for p in r['pairs']), 'Assembly has interference'
        await call('nx_inspect');await call('nx_fit_view');await call('nx_save_part')
        await call('nx_export_step '+str(MODELS/'joint_assembly.step'))

if __name__=='__main__':asyncio.run(main())
