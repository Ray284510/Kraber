"""One-attempt acquisition telemetry with injected APIs only.

There is deliberately no live Win32 adapter, CLI, MCP import or NX command here.
Tests inject synthetic functions. Future native use requires separate authorization.
Never pass this helper to the production command dispatcher: it cannot read/write.
"""
import datetime
import os
import sys


class RetryForbidden(RuntimeError):
    pass


class AcquisitionDiagnostics:
    def __init__(self, *, wait_named_pipe, create_file, close_handle, pipe_name,
                 provenance, sink=None, max_attempts=1, clock=None, pid=None):
        if type(max_attempts) is not int or max_attempts != 1:
            raise ValueError('Only explicit max_attempts=1 is supported')
        if provenance not in ('synthetic/mock', 'authorized_native_acquisition'):
            raise ValueError('Explicit receipt provenance required')
        self._wait = wait_named_pipe
        self._create = create_file
        self._close = close_handle
        self.pipe_name = pipe_name
        self.provenance = provenance
        self._sink = sink
        self._clock = clock or (lambda: datetime.datetime.now().astimezone().isoformat())
        self.pid = os.getpid() if pid is None else pid
        self.events = []
        self.counts = {'WaitNamedPipe': 0, 'CreateFile': 0, 'CloseHandle': 0}
        self._original_error = None
        self._original_traceback = None
        self._owned_handle = None
        self._run_started = False
        self.cleanup_status = 'NO_HANDLE'

    def _record(self, api, event, **fields):
        row = {'time': self._clock(), 'pid': self.pid, 'pipe_name': self.pipe_name,
               'api': api, 'event': event, 'api_call_count': self.counts.get(api, 0),
               'max_attempts': 1, 'provenance': self.provenance, **fields}
        self.events.append(row)
        if self._sink is not None:
            self._sink(dict(row))

    def _capture_error(self, api, error):
        # Keep the actual exception object and traceback. Never reconstruct a WinError.
        self._original_error = error
        self._original_traceback = error.__traceback__
        try:
            self._record(api, 'error', winerror=getattr(error, 'winerror', None),
                         native_api_name=getattr(error, 'funcname', None),
                         error_type=type(error).__name__, error_message=str(error),
                         error_args=list(error.args), retry=False)
        except Exception as logging_error:
            self.events.append({'event': 'telemetry_sink_failure', 'api': api,
                                'provenance': self.provenance,
                                'error_type': type(logging_error).__name__,
                                'original_error_preserved': True})

    def _guard(self, api):
        if self._original_error is not None:
            raise self._original_error.with_traceback(self._original_traceback)
        if self.counts[api] != 0:
            raise RetryForbidden(api + ' already attempted; no second dispatch')

    def WaitNamedPipe(self, pipe_name, timeout_ms):
        if pipe_name != self.pipe_name:
            raise ValueError('Pipe identity mismatch')
        self._guard('WaitNamedPipe')
        self._record('WaitNamedPipe', 'before_call', timeout_ms=timeout_ms)
        self.counts['WaitNamedPipe'] += 1
        try:
            result = self._wait(pipe_name, timeout_ms)
        except BaseException as error:
            self._capture_error('WaitNamedPipe', error)
            raise
        self._record('WaitNamedPipe', 'success')
        return result

    def CreateFile(self, pipe_name, *args):
        if pipe_name != self.pipe_name:
            raise ValueError('Pipe identity mismatch')
        self._guard('CreateFile')
        if self.counts['WaitNamedPipe'] != 1:
            raise ValueError('Successful WaitNamedPipe required before CreateFile')
        self._record('CreateFile', 'before_call')
        self.counts['CreateFile'] += 1
        try:
            handle = self._create(pipe_name, *args)
        except BaseException as error:
            self._capture_error('CreateFile', error)
            raise
        # Own it before logging: a failed success receipt must still close this handle.
        self._owned_handle = handle
        self.cleanup_status = 'HANDLE_OWNED'
        self._record('CreateFile', 'success', handle_acquired=True)
        return handle

    def reject_retry_sleep(self, seconds):
        # No time.sleep is imported/called. Preserve a previously captured WinError.
        if self._original_error is not None:
            try:
                self._record('retry_sleep', 'blocked', seconds_requested=seconds,
                             sleep_executed=False, original_error_preserved=True)
            except Exception:
                pass
            raise self._original_error.with_traceback(self._original_traceback)
        raise RetryForbidden('Retry sleep forbidden, no sleep performed')

    def WriteFile(self, *args, **kwargs):
        raise PermissionError('Acquisition-only diagnostic cannot WriteFile')

    def ReadFile(self, *args, **kwargs):
        raise PermissionError('Acquisition-only diagnostic cannot ReadFile')

    def _cleanup(self):
        handle = self._owned_handle
        if handle is None:
            return
        self._owned_handle = None
        primary_error = sys.exc_info()[1]
        self.counts['CloseHandle'] += 1
        try:
            self._close(handle)
        except BaseException as cleanup_error:
            self.cleanup_status = 'UNKNOWN_CLOSE_FAILED'
            try:
                self._record('CloseHandle', 'error', winerror=getattr(cleanup_error, 'winerror', None),
                             error_type=type(cleanup_error).__name__, retry=False)
            except Exception:
                pass
            if primary_error is None:
                raise
        else:
            self.cleanup_status = 'CLOSED'
            try:
                self._record('CloseHandle', 'success')
            except Exception:
                if primary_error is None:
                    raise

    def acquire_once(self, *, timeout_ms=500, create_args=()):
        """Acquire then close, no command I/O. Only injected mocks used in this batch."""
        if self._run_started:
            raise RetryForbidden('Diagnostic instance consumed; never replay')
        self._run_started = True
        try:
            self.WaitNamedPipe(self.pipe_name, timeout_ms)
            self.CreateFile(self.pipe_name, *create_args)
        finally:
            self._cleanup()
        return {'acquired': True, 'closed': self.cleanup_status == 'CLOSED',
                'WriteFile_calls': 0, 'ReadFile_calls': 0, 'provenance': self.provenance}
