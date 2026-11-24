"""Prepared Win32 binding. No platform imports or API instances at module import.

Only build_after_separate_authorization imports Win32 modules. It is not called
by the launcher, tests or this preparation batch. Backend tests inject fakes.
"""
import time
from .status_only import StatusOnlyAdapter, State, PIPE, FileAttemptStore

# Keep buffers/OVERLAPPED/event handles alive if cancellation completion is unknown.
_QUARANTINED = []


class CleanupUncertain(OSError):
    pass


class Win32StatusIO:
    def __init__(self, file_api, pipe_api, event_api, types_api, *, monotonic=time.monotonic):
        self.f=file_api;self.p=pipe_api;self.e=event_api;self.t=types_api;self.clock=monotonic
        self.pending=[];self.cleanup_faults=[];self.events=[]

    def wait(self, name, timeout):
        return self.p.WaitNamedPipe(name,timeout)

    def create(self, name):
        return self.f.CreateFile(name,self.f.GENERIC_READ|self.f.GENERIC_WRITE,0,None,
                                 self.f.OPEN_EXISTING,self.f.FILE_FLAG_OVERLAPPED,None)

    def _cancel(self, ctx):
        if ctx not in self.pending:self.pending.append(ctx)
        try:self.f.CancelIo(ctx['handle']);self.events.append({'api':'CancelIo','requested':True})
        except BaseException as error:
            self.events.append({'api':'CancelIo','error':str(error),'winerror':getattr(error,'winerror',None)})

    def _io(self, handle, buffer, timeout, write):
        ov=self.t.OVERLAPPED();ev=self.e.CreateEvent(None,True,False,None);ov.hEvent=ev
        ctx={'handle':handle,'ov':ov,'event':ev,'buffer':buffer}
        issued=False;completed=False;primary=None
        api='WriteFile' if write else 'ReadFile'
        try:
            issued=True;self.events.append({'api':api,'event':'issue'})
            if write:self.f.WriteFile(handle,buffer,ov)
            else:self.f.ReadFile(handle,buffer,ov)
            if self.e.WaitForSingleObject(ev,timeout)!=self.e.WAIT_OBJECT_0:
                self._cancel(ctx)
                raise TimeoutError(api+' wait did not complete; outcome unknown, no replay')
            completed=True
            count=self.f.GetOverlappedResult(handle,ov,True)
            self.events.append({'api':api,'event':'completion','bytes':count})
            return count
        except BaseException as error:
            primary=error
            if issued and not completed and ctx not in self.pending:self._cancel(ctx)
            raise
        finally:
            if completed or not issued:
                try:self.f.CloseHandle(ev)
                except BaseException as close_error:
                    self.cleanup_faults.append(close_error);_QUARANTINED.append(ctx)
                    if primary is None:raise

    def write_once(self, handle, data, timeout):
        if data!=b'nx_status\n':raise PermissionError('Only fixed nx_status bytes')
        return self._io(handle,data,timeout,True)

    def read_response_once(self, handle, timeout, max_bytes):
        deadline=self.clock()+timeout/1000;data=b''
        for _ in range(64):
            remaining=int((deadline-self.clock())*1000)
            if remaining<=0:raise TimeoutError('Status response deadline exceeded; no replay')
            buf=self.f.AllocateReadBuffer(min(65536,max_bytes-len(data)+1))
            size=self._io(handle,buf,remaining,False)
            if size==0:raise EOFError('Pipe closed before complete status')
            data+=bytes(buf[:size])
            if len(data)>max_bytes:raise ValueError('Status response too large')
            if data.endswith(b'\n'):return data
        raise ValueError('Status response chunk bound exceeded; no command replay')

    def close(self, handle):
        try:self.f.CloseHandle(handle)
        except BaseException:
            _QUARANTINED.extend(self.pending);self.pending=[]
            raise
        # Closing the pipe requests cancellation. Wait once for each already-issued
        # operation to finish before freeing its buffers; never reissue the command.
        for ctx in self.pending:
            try:
                if self.e.WaitForSingleObject(ctx['event'],1000)!=self.e.WAIT_OBJECT_0:
                    raise CleanupUncertain('Cancellation completion unknown')
                self.f.CloseHandle(ctx['event'])
            except BaseException as error:
                _QUARANTINED.append(ctx);self.cleanup_faults.append(error)
        self.pending=[]
        if self.cleanup_faults:raise self.cleanup_faults[0]


def build_after_separate_authorization(flow, attempt_store=None, *, sink=None):
    """Future-only binding; construction is forbidden in the preparation batch."""
    if flow.state!=State.STATUS_ATTEMPT_AUTHORIZED or flow.provenance!='authorized_native_acquisition' or not flow.authorization_reference:
        raise PermissionError('Separate explicit native status authorization required')
    flow.validate_authorization()
    if attempt_store is not None:
        raise PermissionError('Native factory owns the canonical store; injection forbidden')
    attempt_store=FileAttemptStore()
    attempt_store.validate_native()
    import win32file
    import win32pipe
    import win32event
    import pywintypes
    io=Win32StatusIO(win32file,win32pipe,win32event,pywintypes)
    adapter=StatusOnlyAdapter(wait_named_pipe=io.wait,create_file=io.create,close_handle=io.close,
        pipe_name=PIPE,provenance='authorized_native_acquisition',sink=sink,
        write_once=io.write_once,read_response_once=io.read_response_once,
        attempt_store=attempt_store,readonly_max_attempts=1)
    adapter.transport_events=io.events
    return adapter
