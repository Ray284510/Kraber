from lab_paths import lab_path
import asyncio,datetime,json,os,sys
from contextlib import AsyncExitStack
from pathlib import Path
from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
ROOT=lab_path('');LOG=ROOT/'logs/phase3';LOG.mkdir(parents=True, exist_ok=True)
class Client:
 async def __aenter__(self):
  self.stack=AsyncExitStack();await self.stack.__aenter__();r,w=await self.stack.enter_async_context(stdio_client(StdioServerParameters(command=sys.executable,args=[str(ROOT/'bridge/mcp_server.py')],env=dict(os.environ))));self.session=await self.stack.enter_async_context(ClientSession(r,w));await self.session.initialize();return self
 async def __aexit__(self,*args):return await self.stack.__aexit__(*args)
 async def call(self,cmd,allow_failure=False):
  start=datetime.datetime.now().astimezone().isoformat();name=cmd if cmd in ('nx_status','nx_inspect') else 'nx_execute'
  raw=await self.session.call_tool(name,{} if name!='nx_execute' else {'command':cmd});result=json.loads(next(c.text for c in raw.content if c.type=='text'))
  rec={'started':start,'finished':datetime.datetime.now().astimezone().isoformat(),'command':cmd,'response':result,'mcp_is_error':raw.isError}
  with (LOG/'actual-calls.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(rec,ensure_ascii=False)+'\n')
  print(json.dumps(rec,ensure_ascii=False),flush=True)
  if (raw.isError or not result.get('ok')) and not allow_failure:raise RuntimeError(json.dumps(result,ensure_ascii=False))
  return result
async def main():
 async with Client() as c:
  if sys.argv[1]=='--batch':
   for row in json.loads(Path(sys.argv[2]).read_text(encoding='utf-8-sig')):await c.call(row if isinstance(row,str) else row['command'],not isinstance(row,str) and row.get('allow_failure',False))
  else:await c.call(' '.join(sys.argv[1:]),True)
if __name__=='__main__':asyncio.run(main())
