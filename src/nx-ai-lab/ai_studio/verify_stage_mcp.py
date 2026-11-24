"""Live, read-only MCP acceptance of the stage broker; no OpenAI/DSH invocation."""
import argparse
import asyncio
import json
import os
import sqlite3
import sys
from pathlib import Path

from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
from .stage_mcp import ROOT,valid_stage


async def verify(folder):
    folder,_=valid_stage(folder,'inspect')
    transcript=[]
    params=StdioServerParameters(command=str(ROOT/'.venv/Scripts/python.exe'),
        args=['-m','ai_studio.stage_mcp','--task',str(folder),'--stage','inspect'],
        cwd=str(ROOT),env=dict(os.environ,NX_AI_LAB_ROOT=str(ROOT),TEMP=str(ROOT/'tmp'),TMP=str(ROOT/'tmp')))
    async with stdio_client(params) as (r,w):
        async with ClientSession(r,w) as session:
            initialized=await session.initialize();listing=await session.list_tools()
            names=[t.name for t in listing.tools]
            assert set(names)=={'nx_stage_state','nx_observe','nx_action','nx_batch','design_artifact','stage_checkpoint'},names
            for name,args in [('nx_stage_state',{}),('nx_observe',{'command':'nx_status'}),
                              ('nx_observe',{'command':'nx_inspect'}),
                              ('nx_action',{'step':'forbidden','attempt':0,'command':'nx_save_part'})]:
                raw=await session.call_tool(name,args)
                texts=[c.text for c in raw.content if c.type=='text']
                transcript.append({'tool':name,'arguments':args,'is_error':bool(raw.isError),'texts':texts})
                if name=='nx_action':assert raw.isError,'Read-only stage admitted mutation'
                else:assert not raw.isError
            actual=json.loads(transcript[1]['texts'][0])
            assert actual['ok'] and actual['nx']=='12.0.2.9'
    with sqlite3.connect((folder/'task.sqlite').as_uri()+'?mode=ro',uri=True) as db:
        operations=db.execute('SELECT COUNT(*) FROM operations').fetchone()[0]
    assert operations==0,'Mutation must be rejected before claim/dispatch'
    evidence={'test':'Actual local MCP stage broker, NOT DSH/API acceptance','tools':names,
              'initialize':initialized.model_dump(mode='json'),'actual_calls':transcript,
              'mutation_operations':operations,'paid_requests':0,'passed':True}
    target=folder/'stage-mcp-readonly-acceptance.json'
    target.write_text(json.dumps(evidence,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'passed':True,'evidence':str(target),'paid_requests':0,'mutation_operations':0}))


if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('folder',type=Path);a=p.parse_args();asyncio.run(verify(a.folder))
