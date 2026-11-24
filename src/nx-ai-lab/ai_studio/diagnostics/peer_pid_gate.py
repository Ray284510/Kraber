"""Isolated peer-PID preparation: injected functions and synthetic harness only.

No native factory, CLI, Win32/NX/MCP import, file store, or live authorization.
These checks prevent ordinary misuse; they are not an in-process security boundary.
The existing StatusOnlyAdapter/Win32 factory are deliberately not changed.
"""
from copy import deepcopy
from dataclasses import dataclass
from datetime import datetime
import hashlib
import json
import re
import threading
import uuid
from pathlib import Path
import os


CONSUMED_HISTORICAL_UUID_SHA256 = frozenset({
    '1cedaeaf5ae0fe0fd1d9716be67da769f1a53333906f0a9e9e5e81cf0c3c8f16',
    '9161714502b1b9621c82a1e8da25148d23863413030c23eb466f737e1535941f',
})
def previously_consumed(attempt_uuid):
    return isinstance(attempt_uuid, str) and hashlib.sha256(attempt_uuid.encode()).hexdigest() in CONSUMED_HISTORICAL_UUID_SHA256


IDENTITY_FIELDS = frozenset({
    'expected_server_pid', 'expected_nx_pid', 'nx_executable_path',
    'nx_executable_sha256', 'nx_version', 'process_start_time',
    'expected_pipe_name', 'probe_path', 'probe_sha256', 'launch_receipt_sha256',
})
TICKET_FIELDS = IDENTITY_FIELDS | {
    'schema_version', 'provenance', 'attempt_uuid', 'request_nonce',
    'command', 'readonly_max_attempts', 'peer_pid_gate_required',
    'authorization_reference',
}
P13_FIXED_FIELDS = {
    'expected_server_pid': 41001, 'expected_nx_pid': 41001,
    'nx_executable_path': str(Path(os.environ.get("UGII_BASE_DIR","NX_INSTALLATION_UNCONFIGURED")) / "nxbin/ugraf.exe"),
    'nx_executable_sha256': 'f1a70d9a6de63b3538602d9db31289db9a6df4362cea666f21ce927bd573f350',
    'nx_version': '12.0.2.9', 'expected_pipe_name': r'\\.\pipe\nx12_ai_lab',
    'probe_path': str(Path(__file__).resolve().parents[2] / "models/p13_diagnostics/broker_probe.prt"),
    'probe_sha256': '8c7977e1a9089bc9f4a9d06b8d4978d6e0822a22c2f0ff7b7f4f92b3431dbbb4',
}


class PeerGateRejected(ValueError):
    pass


class ReplayForbidden(PermissionError):
    pass


def canonical_digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                                    allow_nan=False, ensure_ascii=False).encode()).hexdigest()


def validate_synthetic_ticket(value, expected_identity, bound_digest):
    """Validate a copy against separately supplied context/digest, not user consent.

    Future live integration must retain real RecoveryState/history verification,
    current process checks and durable canonical consumption. This is not that path.
    """
    if not isinstance(value, dict) or set(value) != TICKET_FIELDS:
        raise PermissionError('Exact peer ticket fields required')
    ticket = deepcopy(value)
    if ticket['schema_version'] != 'p13-peer-ticket-v1' or ticket['provenance'] != 'synthetic/mock':
        raise PermissionError('Preparation accepts synthetic/mock only; no live ticket')
    attempt = ticket['attempt_uuid']
    if not isinstance(attempt, str) or str(uuid.UUID(attempt)) != attempt:
        raise PermissionError('Canonical UUID required')
    if previously_consumed(attempt):
        raise ReplayForbidden('Historical consumed UUID cannot be used')
    nonce = ticket['request_nonce']
    if not isinstance(nonce, str) or not re.fullmatch(r'[A-Za-z0-9_-]{16,128}', nonce):
        raise PermissionError('Explicit bounded nonce required')
    if ticket['command'] != 'nx_status':
        raise PermissionError('Only fixed nx_status command is designed here')
    if type(ticket['readonly_max_attempts']) is not int or ticket['readonly_max_attempts'] != 1:
        raise PermissionError('One attempt required')
    if ticket['peer_pid_gate_required'] is not True:
        raise PermissionError('Peer gate cannot be optional')
    for key in ('expected_server_pid', 'expected_nx_pid'):
        if type(ticket[key]) is not int or ticket[key] <= 0:
            raise PermissionError('Strict positive PID required')
    if any(ticket[key] != value for key, value in P13_FIXED_FIELDS.items()):
        raise PermissionError('P13 fixed identity contract mismatch')
    if ticket['expected_server_pid'] != ticket['expected_nx_pid']:
        raise PermissionError('No automatic broker PID substitution')
    for key in ('nx_executable_sha256', 'probe_sha256', 'launch_receipt_sha256'):
        if not isinstance(ticket[key], str) or not re.fullmatch('[0-9a-f]{64}', ticket[key]):
            raise PermissionError('Lowercase SHA-256 required')
    for key in ('authorization_reference', 'nx_executable_path', 'probe_path', 'expected_pipe_name', 'nx_version'):
        if not isinstance(ticket[key], str) or not ticket[key].strip():
            raise PermissionError('Nonempty binding field required')
    start = ticket['process_start_time']
    if not isinstance(start, str) or datetime.fromisoformat(start).utcoffset() is None:
        raise PermissionError('Timezone-aware StartTime required')
    # Compare the original string, not a precision-reduced parsed datetime.
    if not isinstance(expected_identity, dict) or set(expected_identity) != IDENTITY_FIELDS:
        raise PermissionError('Independent complete expected identity required')
    if any(ticket[k] != expected_identity[k] for k in IDENTITY_FIELDS):
        raise PermissionError('Ticket/context identity mismatch')
    if canonical_digest(ticket) != bound_digest:
        raise PermissionError('Ticket digest mismatch')
    return ticket


@dataclass(frozen=True)
class VerifiedPeer:
    server_pid: int
    expected_server_pid: int
    scope: str = 'CONNECTED_HANDLE_SERVER_CREATOR_PID_ONLY'
    work_part_verified: bool = False


def verify_peer_pid(handle, get_named_pipe_server_process_id, expected_server_pid):
    """Query the supplied handle once; preserve any original exception unchanged."""
    if handle is None or type(expected_server_pid) is not int or expected_server_pid <= 0:
        raise PeerGateRejected('Known handle and strict positive expected PID required')
    actual = get_named_pipe_server_process_id(handle)
    if type(actual) is not int or actual <= 0:
        raise PeerGateRejected('Server PID missing, non-integer, bool or non-positive')
    if actual != expected_server_pid:
        raise PeerGateRejected(f'Server PID mismatch: expected {expected_server_pid}, observed {actual}')
    return VerifiedPeer(actual, expected_server_pid)


class SyntheticAttemptStore:
    """Memory-only mock consumption; never writes the canonical/live store."""
    def __init__(self):
        self.consumed = {}
        self._lock = threading.Lock()

    def consume(self, attempt_uuid, digest):
        with self._lock:
            if attempt_uuid in self.consumed or previously_consumed(attempt_uuid):
                raise ReplayForbidden('Synthetic attempt already consumed')
            self.consumed[attempt_uuid] = digest


class SyntheticPeerGatedStatus:
    """One-run injected Mock harness. Not wired into any live factory.

    A matching PID permits exactly one injected status write, never session/Worker.
    Legacy replies cannot prove response association, Work Part or global readiness.
    """
    def __init__(self, *, wait, create, server_pid, close, write, read, store,
                 expected_identity, bound_ticket_digest, client_pid, clock, sink=None):
        if type(store) is not SyntheticAttemptStore:
            raise PermissionError('Only memory synthetic store in preparation harness')
        if type(client_pid) is not int or client_pid <= 0:
            raise ValueError('Explicit synthetic client PID required')
        self.wait, self.create, self.server_pid = wait, create, server_pid
        self.close, self.write, self.read = close, write, read
        self.store, self.expected = store, deepcopy(expected_identity)
        self.bound_digest = bound_ticket_digest
        self.client_pid, self.clock, self.sink = client_pid, clock, sink
        self.counts = {k: 0 for k in ('WaitNamedPipe', 'CreateFile', 'GetNamedPipeServerProcessId', 'WriteFile', 'ReadResponse', 'CloseHandle')}
        self.events = []
        self.original_error = self.close_error = None
        self.receipt = None
        self._run_lock = threading.Lock()
        self._used = False

    def _record(self, api, event, **extra):
        row = {'api': api, 'event': event, 'time': self.clock(),
               'client_pid': self.client_pid, 'pipe': self.expected['expected_pipe_name'],
               'provenance': 'synthetic/mock', **extra}
        self.events.append(row)
        if self.sink is not None:
            self.sink(deepcopy(row))

    def _error(self, api, error):
        # Caller keeps the actual object and traceback; log failure cannot replace it.
        try:
            self._record(api, 'error', error_type=type(error).__name__,
                         winerror=getattr(error, 'winerror', None),
                         funcname=getattr(error, 'funcname', None), args=list(error.args),
                         retry=False)
        except BaseException as log_error:
            self.events.append({'api': api, 'event': 'sink_failure',
                                'error_type': type(log_error).__name__,
                                'original_error_preserved': True, 'provenance': 'synthetic/mock'})

    def run(self, ticket):
        with self._run_lock:
            if self._used:
                raise ReplayForbidden('Harness cannot be replayed')
            self._used = True
        bound = validate_synthetic_ticket(ticket, self.expected, self.bound_digest)
        handle = None
        primary = traceback = None
        cleanup = 'NO_HANDLE'
        written = known = consumed = peer_pass = False
        observed_pid = None
        response = None
        api = 'SyntheticAttemptStore'
        try:
            self.store.consume(bound['attempt_uuid'], self.bound_digest)
            consumed = True
            api = 'WaitNamedPipe'
            self._record(api, 'before_call'); self.counts[api] += 1
            self.wait(bound['expected_pipe_name'], 500)
            self._record(api, 'success')
            api = 'CreateFile'
            self._record(api, 'before_call'); self.counts[api] += 1
            handle = self.create(bound['expected_pipe_name'])
            if handle is None:
                raise PeerGateRejected('CreateFile did not return a known handle')
            cleanup = 'OWNED'
            self._record(api, 'success')
            api = 'GetNamedPipeServerProcessId'
            self._record(api, 'before_call'); self.counts[api] += 1

            def query_same_handle(h):
                nonlocal observed_pid
                observed_pid = self.server_pid(h)
                return observed_pid

            peer = verify_peer_pid(handle, query_same_handle, bound['expected_server_pid'])
            peer_pass = True
            self._record(api, 'success', server_pid=peer.server_pid, scope=peer.scope)
            api = 'WriteFile'
            self._record(api, 'before_call'); self.counts[api] += 1; written = True
            # Legacy request does not carry a nonce. Never claim end-to-end correlation.
            count = self.write(handle, b'nx_status\n')
            if type(count) is not int or count != 10:
                raise IOError('Incomplete write; no remainder resend')
            api = 'ReadResponse'
            self._record(api, 'before_call'); self.counts[api] += 1
            raw = self.read(handle)
            if not isinstance(raw, bytes) or len(raw) > 1048576 or not raw.endswith(b'\n'):
                raise ValueError('Incomplete response')
            response = json.loads(raw.decode('utf-8-sig'), parse_constant=lambda x: (_ for _ in ()).throw(ValueError('Nonfinite JSON')))
            if not isinstance(response, dict):
                raise ValueError('Response must be an object')
            known = True
            self._record(api, 'known_legacy_response', response=response)
        except BaseException as error:
            primary, traceback = error, error.__traceback__
            self.original_error = error
            self._error(api, error)
        finally:
            if handle is not None:
                self.counts['CloseHandle'] += 1
                try:
                    self.close(handle)
                    cleanup = 'CLOSED'
                    self._record('CloseHandle', 'success')
                except BaseException as error:
                    if cleanup != 'CLOSED':
                        cleanup = 'UNKNOWN_CLOSE_FAILED'; self.close_error = error
                    self._error('CloseHandle', error)
                    if primary is None:
                        primary, traceback = error, error.__traceback__
                        self.original_error = error
                handle = None
            state = 'LEGACY_RESPONSE_OBSERVED_IDENTITY_UNVERIFIED'
            if primary is not None:
                state = 'UNKNOWN_AFTER_WRITE' if written and not known else 'STOPPED'
            if cleanup == 'UNKNOWN_CLOSE_FAILED':
                state = 'UNKNOWN_CLOSE_FAILED'
            self.receipt = {'state': state, 'provenance': 'synthetic/mock',
                            'attempt_uuid': bound['attempt_uuid'], 'consumed_in_memory': consumed,
                            'counts': dict(self.counts), 'events': deepcopy(self.events),
                            'peer_pid_pass': peer_pass, 'server_pid': observed_pid,
                            'client_pid': self.client_pid, 'expected_server_pid': bound['expected_server_pid'],
                            'write_attempted': written, 'known_response': known,
                            'response': response, 'cleanup': cleanup,
                            'response_correlation': 'UNKNOWN_LEGACY_PROTOCOL',
                            'work_part_verified': False, 'session_identity_verified': False,
                            'loader_hash_verified': False, 'Worker_ready': False,
                            'replay_allowed': False}
        if primary is not None:
            raise primary.with_traceback(traceback)
        return self.receipt


class NonceCorrelator:
    """Strict one-response CLIENT model; not wired to legacy global RespQueue.

    Dropped entries remain in the audit list; mismatch closes the current request.
    No polling, second response read, resend or queue clearing is implied/allowed.
    Matching nonce is correlation only, not server/session/geometry authentication.
    """
    def __init__(self):
        self.active = None
        self.seen = set()
        self.closed = {}
        self.operation_outcomes = {}
        self.audit = []

    def begin(self, nonce):
        if not isinstance(nonce, str) or not re.fullmatch(r'[A-Za-z0-9_-]{16,128}', nonce):
            raise ValueError('Bounded nonce required')
        if self.active is not None or nonce in self.seen:
            raise ReplayForbidden('Active or reused nonce')
        self.seen.add(nonce); self.active = nonce
        self.operation_outcomes[nonce] = 'UNKNOWN'

    def timeout(self):
        if self.active is None:
            raise ValueError('No active request')
        self.closed[self.active] = 'UNKNOWN_AFTER_WRITE'
        self.operation_outcomes[self.active] = 'UNKNOWN_AFTER_WRITE'
        self.audit.append({'event': 'timeout', 'nonce': self.active, 'replay_allowed': False})
        self.active = None

    def accept(self, response):
        nonce = response.get('request_nonce') if isinstance(response, dict) else None
        # Future identity schema uses an evidence-bearing field wrapper. Full
        # schema/identity validation is a separate gate, never implied here.
        if isinstance(nonce, dict):
            nonce = nonce.get('value') if nonce.get('status') == 'KNOWN' else None
        if not isinstance(nonce, str) or self.active is None or nonce != self.active or nonce in self.closed:
            self.audit.append({'event': 'discard_stale_or_mismatched', 'active': self.active,
                               'response': deepcopy(response), 'replay_allowed': False})
            if self.active is not None:
                self.closed[self.active] = 'STOPPED_NONCE_MISMATCH'
                # Another request's complete JSON proves nothing about this one.
                # The pure correlator has no write receipt: do not invent an outcome.
                self.operation_outcomes[self.active] = 'UNKNOWN'
                self.active = None
            return False
        self.closed[nonce] = 'RESPONSE_MATCHED_ONLY'
        self.operation_outcomes[nonce] = 'CORRELATED_RESPONSE_ONLY_NOT_EXECUTION_ACCEPTANCE'
        self.audit.append({'event': 'matched', 'nonce': nonce})
        self.active = None
        return True
