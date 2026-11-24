from lab_paths import lab_path
import asyncio,datetime,json,os,sys
from contextlib import AsyncExitStack
from pathlib import Path
from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
ROOT=lab_path('');LOG=ROOT/'logs/phase4';LOG.mkdir(parents=True,exist_ok=True)
class Client:
 def __init__(self,log_stream=None,*,strict_single_connect=False,expected_server_pid=None):
  self.log_stream=log_stream or sys.stdout
  if type(strict_single_connect) is not bool:raise ValueError('Strict flag must be bool')
  if strict_single_connect and (type(expected_server_pid) is not int or expected_server_pid<=0):raise ValueError('Explicit expected server PID required')
  self.child_env=dict(os.environ,NX_AI_LAB_STRICT_SINGLE_CONNECT='1' if strict_single_connect else '0',NX_AI_LAB_EXPECTED_SERVER_PID=str(expected_server_pid) if strict_single_connect else '')
 async def __aenter__(self):
  self.stack=AsyncExitStack();await self.stack.__aenter__();r,w=await self.stack.enter_async_context(stdio_client(StdioServerParameters(command=sys.executable,args=[str(ROOT/'bridge/mcp_server.py')],env=self.child_env)));self.session=await self.stack.enter_async_context(ClientSession(r,w));self.initialize_result=await self.session.initialize();return self
 async def __aexit__(self,*args):return await self.stack.__aexit__(*args)
 async def tool(self,name,arguments):
  start=datetime.datetime.now().astimezone().isoformat();raw=await self.session.call_tool(name,arguments);result=json.loads(next(c.text for c in raw.content if c.type=='text'))
  rec={'started':start,'finished':datetime.datetime.now().astimezone().isoformat(),'tool':name,'arguments':arguments,'response':result,'mcp_is_error':raw.isError}
  with (LOG/'actual-calls.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(rec,ensure_ascii=False)+'\n')
  print(json.dumps(rec,ensure_ascii=False),flush=True,file=self.log_stream)
  if raw.isError or not result.get('ok'):raise RuntimeError(json.dumps(result,ensure_ascii=False))
  return result
 async def call(self,cmd,allow_failure=False):
  start=datetime.datetime.now().astimezone().isoformat();name=cmd if cmd in ('nx_status','nx_inspect') else 'nx_execute'
  raw=await self.session.call_tool(name,{} if name!='nx_execute' else {'command':cmd});result=json.loads(next(c.text for c in raw.content if c.type=='text'))
  if not isinstance(result,dict):
   result={'ok':False,'outcome':'UNKNOWN_AFTER_WRITE','retry_safe':False,'malformed_response':True}
  if raw.isError:
   result=dict(result,ok=False,outcome='UNKNOWN_AFTER_WRITE',retry_safe=False,mcp_is_error=True)
  rec={'started':start,'finished':datetime.datetime.now().astimezone().isoformat(),'command':cmd,'response':result,'mcp_is_error':raw.isError}
  try:
   with (LOG/'actual-calls.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(rec,ensure_ascii=False)+'\n')
   print(json.dumps(rec,ensure_ascii=False),flush=True,file=self.log_stream)
  except (OSError,ValueError,TypeError) as exc:
   exc.transport_receipt=result.get('transport_receipt')
   raise
  if not result.get('ok') and not allow_failure:raise RuntimeError(json.dumps(result,ensure_ascii=False))
  return result
async def main():
 async with Client() as c:
  if sys.argv[1]=='--batch':
   for row in json.loads(Path(sys.argv[2]).read_text(encoding='utf-8-sig')):await c.call(row if isinstance(row,str) else row['command'],not isinstance(row,str) and row.get('allow_failure',False))
  else:await c.call(' '.join(sys.argv[1:]),True)
if __name__=='__main__':asyncio.run(main())
