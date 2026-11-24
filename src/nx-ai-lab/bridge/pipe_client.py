import sys,json,datetime,time,os
import hashlib
import win32file, win32pipe, win32event, pywintypes
PIPE=r'\\.\pipe\nx12_ai_lab'
def error_args(exc):
    # UnicodeDecodeError includes raw bytes in args; they cannot cross JSON/MCP.
    result=[]
    for item in exc.args:
        if isinstance(item,bytes):
            result.append({'byte_length':len(item),'sha256':hashlib.sha256(item).hexdigest(),
                           'hex_prefix':item[:32].hex(),'truncated':len(item)>32})
        elif item is None or isinstance(item,(str,int,bool)):
            result.append(item)
        else:
            result.append({'type':type(item).__name__,'message':str(item)})
    return result


def call(command, *, strict_single_connect=None, expected_server_pid=None):
    # Explicit child-process configuration; absent config preserves legacy acquisition.
    if strict_single_connect is None:
        value=os.environ.get('NX_AI_LAB_STRICT_SINGLE_CONNECT','0')
        if value not in ('0','1'):raise ValueError('Invalid strict connection configuration')
        strict_single_connect=value=='1'
    if type(strict_single_connect) is not bool:raise ValueError('Strict flag must be bool')
    if strict_single_connect:
        if expected_server_pid is None:
            raw=os.environ.get('NX_AI_LAB_EXPECTED_SERVER_PID','')
            if not raw.isdecimal():raise ValueError('Explicit expected server PID required')
            expected_server_pid=int(raw)
        if type(expected_server_pid) is not int or expected_server_pid<=0:
            raise ValueError('Strict positive expected PID required')
    response_wait_ms=360000 if command.startswith('nx_motion_solve ') else 60000
    counts={k:0 for k in ('WaitNamedPipe','CreateFile','GetNamedPipeServerProcessId','WriteFile','ReadFile','CloseHandle','event_CloseHandle')}
    receipt={'strict_single_connect':strict_single_connect,'counts':counts,'peer_pid':None,
             'retry':False,'outcome':'NOT_SENT','cleanup_errors':[]}
    h=None;event=None;primary=None
    def invoke(name,fn,*args):
        counts[name]+=1
        return fn(*args)
    def close_event():
        nonlocal event
        owned=event;event=None
        if owned is not None:
            try:invoke('event_CloseHandle',win32file.CloseHandle,owned)
            except BaseException as exc:
                receipt['cleanup_errors'].append({'api':'event_CloseHandle','type':type(exc).__name__,'winerror':getattr(exc,'winerror',None),'args':error_args(exc)})
                receipt['outcome']='UNKNOWN_CLOSE_FAILED'
                raise
    try:
        deadline=time.monotonic()+5
        while True:
            try:
                invoke('WaitNamedPipe',win32pipe.WaitNamedPipe,PIPE,500)
                h=invoke('CreateFile',win32file.CreateFile,PIPE,win32file.GENERIC_READ|win32file.GENERIC_WRITE,0,None,win32file.OPEN_EXISTING,win32file.FILE_FLAG_OVERLAPPED,None)
                break
            except pywintypes.error as e:
                if strict_single_connect or e.winerror not in (2,121,231) or time.monotonic()>=deadline:raise
                receipt['retry']=True
                time.sleep(.05)  # Legacy only: never retry after command dispatch.
        if strict_single_connect:
            # Same acquired handle; never use client PID or accept a broker fallback.
            pid=invoke('GetNamedPipeServerProcessId',win32pipe.GetNamedPipeServerProcessId,h)
            receipt['peer_pid']=pid
            if type(pid) is not int or pid<=0 or pid!=expected_server_pid:
                raise PermissionError('Pipe server PID invalid/mismatched; no command sent')
        ov=pywintypes.OVERLAPPED();event=win32event.CreateEvent(None,True,False,None);ov.hEvent=event
        receipt['outcome']='UNKNOWN_AFTER_WRITE'
        frame=(command+'\n').encode('utf-8')
        invoke('WriteFile',win32file.WriteFile,h,frame,ov)
        if win32event.WaitForSingleObject(event,5000)!=win32event.WAIT_OBJECT_0:
            win32file.CancelIo(h);raise TimeoutError('NX write timed out; outcome unknown')
        if win32file.GetOverlappedResult(h,ov,True)!=len(frame):
            raise OSError('Incomplete write; mutation outcome unknown')
        close_event()
        data=b''
        read_deadline = time.monotonic() + response_wait_ms/1000 if command.startswith('nx_p13_once ') else None
        while not data.endswith(b'\n'):
            ov=pywintypes.OVERLAPPED();event=win32event.CreateEvent(None,True,False,None);ov.hEvent=event
            buf=win32file.AllocateReadBuffer(65536)
            invoke('ReadFile',win32file.ReadFile,h,buf,ov)
            remaining = response_wait_ms if read_deadline is None else max(0, int((read_deadline-time.monotonic())*1000))
            if (read_deadline is not None and remaining == 0) or win32event.WaitForSingleObject(event,remaining)!=win32event.WAIT_OBJECT_0:
                win32file.CancelIo(h);raise TimeoutError('NX response timed out; outcome unknown; no automatic resend')
            size=win32file.GetOverlappedResult(h,ov,True);close_event()
            if size==0:raise EOFError('NX pipe closed without complete response')
            data+=bytes(buf[:size])
            if len(data)>4000000:raise RuntimeError('Oversize response after dispatch; outcome unknown')
        result = decode_response(data)
        if not isinstance(result,dict) or type(result.get('ok')) is not bool:
            raise ValueError('Malformed native response envelope')
        if not result.get('response_decode_error'):
            receipt['outcome']='RESPONSE_RECEIVED'
    except BaseException as exc:
        primary=exc
    finally:
        for kind,owned in [('event_CloseHandle',event),('CloseHandle',h)]:
            if owned is None:continue
            try:invoke(kind,win32file.CloseHandle,owned)
            except BaseException as exc:
                receipt['cleanup_errors'].append({'api':kind,'type':type(exc).__name__,'winerror':getattr(exc,'winerror',None),'args':error_args(exc)})
                receipt['outcome']='UNKNOWN_CLOSE_FAILED'
                if primary is None:primary=exc
    if primary is not None:
        receipt['error']={'type':type(primary).__name__,'winerror':getattr(primary,'winerror',None),'funcname':getattr(primary,'funcname',None),'args':error_args(primary)}
        primary.transport_receipt=receipt
        raise primary
    result['transport_receipt']=receipt
    return result

def decode_response(data):
    raw=data.decode('utf-8-sig',errors='strict')
    def unique(pairs):
        value={}
        for key,item in pairs:
            if key in value:raise ValueError('Duplicate native response field')
            value[key]=item
        return value
    def invalid_constant(value):
        raise ValueError('Invalid JSON constant: '+value)
    try:return json.loads(raw,object_pairs_hook=unique,parse_constant=invalid_constant)
    except json.JSONDecodeError as exc:
        return {'ok':False,'response_decode_error':str(exc),'raw_response':raw,'execution_started':True,'retry_safe':False,'outcome':'UNKNOWN_AFTER_WRITE'}
if __name__=='__main__':
    cmd=' '.join(sys.argv[1:])
    try: result=call(cmd)
    except Exception as e: result={'ok':False,'transport_error':str(e)}
    record={'time':datetime.datetime.now().isoformat(),'command':cmd,'response':result}
    from lab_paths import lab_path
    with open(lab_path('logs/commands.jsonl'),'a',encoding='utf-8') as f: f.write(json.dumps(record,ensure_ascii=False)+'\n')
    print(json.dumps(result,ensure_ascii=False))
