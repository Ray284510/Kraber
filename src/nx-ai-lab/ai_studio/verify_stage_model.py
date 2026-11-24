"""Real NX validation through the new scoped MCP, with server restart and dedup."""
import argparse
import asyncio
import json
import os
import sqlite3
from contextlib import asynccontextmanager
from pathlib import Path

from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
from .nx_worker import ROOT,Client
from .checkpoint_demo import valid_part


async def verify(folder):
    spec=json.loads((folder/'task_spec.json').read_text());models=Path(spec['models'])
    out=folder/'model-broker';out.mkdir()
    stage={'id':'broker-model','allowed_commands':['nx_create_part','nx_shape','nx_save_part',
           'nx_close_part','nx_open_part','nx_fit_view'],'output_tokens':2048,'max_generations':4,
           'goal':'Broker acceptance, directly driven by official Codex; no paid model.'}
    (folder/'stages/broker-model.json').write_text(json.dumps(stage,indent=2))
    part=models/'broker_probe.prt';assert not part.exists(),'Fresh model required'
    # Close only the known saved recovery fixture, never any unrelated/unsaved work.
    async with Client() as c:
        before=await c.tool('nx_session_parts',{})
        expected=ROOT/'models/phase4_phase5_checkpoint_20261108_192724'
        assert before['all_modified_flags_known'] and not before['any_modified']
        assert all(Path(p['path']).parent==expected for p in before['parts'])
        closed=await c.call('nx_close_part 0')
        (out/'bootstrap.json').write_text(json.dumps({'before':before,'closed':closed},indent=2))
    calls=[]

    @asynccontextmanager
    async def connection():
        params=StdioServerParameters(command=str(ROOT/'.venv/Scripts/python.exe'),
            args=['-m','ai_studio.stage_mcp','--task',str(folder),'--stage','broker-model','--provider','codex'],
            cwd=str(ROOT),env=dict(os.environ,NX_AI_LAB_ROOT=str(ROOT),TEMP=str(ROOT/'tmp'),TMP=str(ROOT/'tmp')))
        async with stdio_client(params) as (r,w):
            async with ClientSession(r,w) as s:
                await s.initialize();yield s

    async def call(session,name,args):
        raw=await session.call_tool(name,args)
        value=json.loads(next(c.text for c in raw.content if c.type=='text'))
        calls.append({'tool':name,'args':args,'is_error':bool(raw.isError),'actual':value})
        (out/'actual-calls.json').write_text(json.dumps(calls,ensure_ascii=False,indent=2),encoding='utf-8')
        assert not raw.isError and value.get('ok',True),value
        return value

    async def action(s,step,command):return await call(s,'nx_action',{'step':step,'attempt':0,'command':command})
    async with connection() as s:
        await action(s,'create','nx_create_part '+str(part))
        shape=await action(s,'solid','nx_shape broker_plate box 0 0 0 0 0 1 24 16 4 create -')
        actual=await call(s,'nx_observe',{'command':'nx_inspect'});valid_part(actual)
        assert abs(actual['bodies'][0]['volume_mm3']-1536)<1e-6
        await action(s,'view','nx_fit_view')
        await action(s,'save','nx_save_part');await action(s,'close','nx_close_part 0')
    # Restart the MCP broker process; never restart NX or reuse an old live session.
    async with connection() as s:
        await action(s,'create','nx_create_part '+str(part))
        repeated=await action(s,'solid','nx_shape broker_plate box 0 0 0 0 0 1 24 16 4 create -')
        assert repeated==shape
        current=await call(s,'nx_observe',{'command':'nx_status'});assert not current['part']
        await action(s,'reopen','nx_open_part '+str(part))
        reopened=await call(s,'nx_observe',{'command':'nx_inspect'});valid_part(reopened)
        assert len(reopened['features'])==len(actual['features'])
        assert abs(reopened['bodies'][0]['volume_mm3']-1536)<1e-6
        checkpoint=await call(s,'stage_checkpoint',{'summary':'Actual broker restart and completed-operation dedup; one valid solid reopens at 1536 mm3. Codex, no API.'})
    with sqlite3.connect(folder/'task.sqlite') as db:
        duplicates=db.execute("SELECT COUNT(*) FROM events WHERE kind='duplicate_not_executed'").fetchone()[0]
        unfinished=db.execute("SELECT COUNT(*) FROM operations WHERE state IN ('running','unknown')").fetchone()[0]
    assert duplicates==2 and unfinished==0
    evidence={'passed':True,'provider':'codex','paid_requests':0,'prt':str(part),
              'reopened':reopened,'broker_process_restarted':True,'nx_process_restarted':False,
              'duplicates_not_executed':duplicates,'unresolved':unfinished,'checkpoint':checkpoint,
              'scope':'Single simple solid through stage broker, not DSH/API/surface acceptance'}
    (out/'acceptance.json').write_text(json.dumps(evidence,indent=2))
    print(json.dumps({'passed':True,'evidence':str(out/'acceptance.json'),'paid_requests':0}))


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('folder',type=Path);a=p.parse_args();asyncio.run(verify(a.folder))
