"""Offline-designed status-only protocol. All Win32 calls must be explicitly injected.

No live API factory, CLI, MCP/Worker import or automatic native execution exists.
An acquisition-only helper is reused for telemetry, never as a command dispatcher.
"""
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path, PureWindowsPath
import datetime
import json
import os
import threading
import stat
import uuid
import copy
import hashlib
from .pipe_acquisition import AcquisitionDiagnostics, RetryForbidden

from .peer_pid_gate import P13_FIXED_FIELDS, previously_consumed

RETIRED_AUTHORIZATION_UUID_SHA256 = frozenset({
    '302ed97421269e61ac48239402114bc52158b24ee4e35fb64fcd58ecdf215d38',
    'ed749619b39a75385c871b6b4370e70aa6bcd9de77b491c4d8628e46303cba32',
})

PIPE = r'\\.\pipe\nx12_ai_lab'
PROBE = P13_FIXED_FIELDS['probe_path']
PROBE_SHA256 = '8c7977e1a9089bc9f4a9d06b8d4978d6e0822a22c2f0ff7b7f4f92b3431dbbb4'
NX_PATH = P13_FIXED_FIELDS['nx_executable_path']
NX_SHA256 = 'f1a70d9a6de63b3538602d9db31289db9a6df4362cea666f21ce927bd573f350'


def canonical_hash(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':'),ensure_ascii=False,allow_nan=False).encode()).hexdigest()


def extend_chain(chain, row):
    return chain+(canonical_hash({'previous':chain[-1] if chain else None,'row':row}),)


def read_history_for_audit(rows):
    """Lossless old-format display, never reconstructs authorization from logs."""
    return [{'read_status':('typed_unverified' if isinstance(row,dict) and row.get('record_type') in ('transition','observation') else 'legacy_unknown'),
             'authorization_eligible':False,'original':copy.deepcopy(row)} for row in rows]


def validate_launch_receipt(receipt, provenance):
    if provenance != 'synthetic/mock' and (not os.environ.get('UGII_BASE_DIR') or not Path(PROBE).is_file()):
        raise PermissionError('Native status requires the installed NX and the privately supplied pinned probe')
    if not isinstance(receipt,dict):raise ValueError('Launch receipt required')
    source='synthetic/mock' if provenance=='synthetic/mock' else 'native_process_launch_after_explicit_authorization'
    expected={'state':'NX_STARTED_WAITING_FOR_USER','provenance':source,'nx_path':NX_PATH,
              'probe_path':PROBE,'probe_sha256':PROBE_SHA256,'nx_sha256':NX_SHA256,'nx_version':'12.0.2.9'}
    if any(receipt.get(k)!=v for k,v in expected.items()):raise ValueError('Launch receipt identity/hash/provenance mismatch')
    if type(receipt.get('nx_pid')) is not int or receipt['nx_pid']<=0:raise ValueError('Positive launch PID required')
    return canonical_hash(receipt)


class State(str, Enum):
    PREPARED_NOT_STARTED = 'PREPARED_NOT_STARTED'
    NX_STARTED_WAITING_FOR_USER = 'NX_STARTED_WAITING_FOR_USER'
    USER_OPENED_PROBE_UNVERIFIED = 'USER_OPENED_PROBE_UNVERIFIED'
    STATUS_ATTEMPT_AUTHORIZED = 'STATUS_ATTEMPT_AUTHORIZED'
    STATUS_ATTEMPTED = 'STATUS_ATTEMPTED'
    STATUS_SUCCESS = 'STATUS_SUCCESS'
    STOPPED = 'STOPPED'
    UNKNOWN_AFTER_WRITE = 'UNKNOWN_AFTER_WRITE'


@dataclass
class RecoveryState:
    provenance: str
    state: State = State.PREPARED_NOT_STARTED
    attempt_uuid: str | None = None
    authorization_reference: str | None = None
    launch_receipt: dict | None = None
    history: list = field(default_factory=list)
    authorization_ticket: dict | None = field(default=None,init=False)
    _chain: tuple = field(default=(),init=False,repr=False)
    _observation_chain: tuple = field(default=(),init=False,repr=False)
    _history_chain: tuple = field(default=(),init=False,repr=False)

    def __post_init__(self):
        if self.provenance not in ('synthetic/mock', 'authorized_native_acquisition'):
            raise ValueError('Explicit flow provenance required')
        if self.state!=State.PREPARED_NOT_STARTED or self.history or self.launch_receipt or self.attempt_uuid or self.authorization_reference:
            raise ValueError('RecoveryState must begin PREPARED_NOT_STARTED without injected history')

    def _set(self, state, evidence):
        self.state = state
        row={'record_type':'transition','sequence':len(self.history),'state':state.value,'evidence':copy.deepcopy(evidence),'provenance':self.provenance}
        self.history.append(row)
        self._chain=extend_chain(self._chain,row)
        self._history_chain=extend_chain(self._history_chain,row)

    def _verified_transitions(self):
        transitions=[];tc=();oc=();hc=();current=State.PREPARED_NOT_STARTED.value
        for index,row in enumerate(self.history):
            if not isinstance(row,dict) or row.get('record_type') not in ('transition','observation'):
                raise PermissionError('Unknown/legacy_unknown history record; no authorization migration')
            if row.get('state') not in {s.value for s in State} or row.get('provenance')!=self.provenance or type(row.get('sequence')) is not int or row['sequence']!=index:
                raise PermissionError('Invalid history state/provenance/sequence')
            if row['record_type']=='transition':
                if 'evidence' not in row or 'observation' in row or 'promoted' in row:
                    raise PermissionError('Malformed transition')
                current=row['state'];transitions.append(row);tc=extend_chain(tc,row)
            else:
                if row.get('promoted') is not False or row['state']!=current or not isinstance(row.get('observation'),str) or not row['observation'].strip() or 'evidence' not in row:
                    raise PermissionError('Observation cannot promote or misstate current state')
                oc=extend_chain(oc,row)
            hc=extend_chain(hc,row)
        if tc!=self._chain or oc!=self._observation_chain or hc!=self._history_chain:
            raise PermissionError('Transition/observation/history integrity mismatch')
        return transitions

    def record_launch(self, receipt):
        if self.state != State.PREPARED_NOT_STARTED:
            raise ValueError('Invalid launch transition')
        validate_launch_receipt(receipt,self.provenance)
        self.launch_receipt = copy.deepcopy(receipt)
        self._set(State.NX_STARTED_WAITING_FOR_USER, receipt)

    def user_opened_probe(self, reference):
        if self.state != State.NX_STARTED_WAITING_FOR_USER or not reference.strip():
            raise ValueError('User opening confirmation must follow launch')
        self._set(State.USER_OPENED_PROBE_UNVERIFIED, {'user_reference': reference, 'native_work_part_verified': False})

    def authorize_status(self, attempt_uuid, user_reference):
        if self.state != State.USER_OPENED_PROBE_UNVERIFIED or not user_reference.strip():
            raise PermissionError('Separate explicit status-only authorization required')
        canonical = str(uuid.UUID(attempt_uuid))
        if canonical != attempt_uuid:
            raise ValueError('Canonical new UUID required')
        self.attempt_uuid = canonical
        self.authorization_reference = user_reference
        self.authorization_ticket={'schema':'p13-status-authorization-v1','attempt_uuid':canonical,
            'command':'nx_status','authorization_reference':user_reference,'launch_receipt_sha256':validate_launch_receipt(self.launch_receipt,self.provenance),
            'probe_path':PROBE,'probe_sha256':PROBE_SHA256,'provenance':self.provenance}
        self._set(State.STATUS_ATTEMPT_AUTHORIZED,self.authorization_ticket)

    def validate_authorization(self):
        """Misuse guard, not a security boundary against arbitrary in-process code."""
        if self.state!=State.STATUS_ATTEMPT_AUTHORIZED or not isinstance(self.authorization_reference,str) or not self.authorization_reference.strip():
            raise PermissionError('Separate nonempty authorization required')
        try:
            if str(uuid.UUID(self.attempt_uuid))!=self.attempt_uuid:raise ValueError('Noncanonical UUID')
            receipt_hash=validate_launch_receipt(self.launch_receipt,self.provenance)
        except (ValueError,TypeError,AttributeError) as e:raise PermissionError('Invalid launch/attempt identity') from e
        rows=self._verified_transitions()
        states=['NX_STARTED_WAITING_FOR_USER','USER_OPENED_PROBE_UNVERIFIED','STATUS_ATTEMPT_AUTHORIZED']
        if [r.get('state') for r in rows]!=states:
            raise PermissionError('Required transition evidence missing or mutated')
        if rows[0]['evidence']!=self.launch_receipt or not isinstance(rows[1]['evidence'].get('user_reference'),str) or not rows[1]['evidence']['user_reference'].strip():
            raise PermissionError('Launch or user-opening evidence mismatch')
        expected={'schema':'p13-status-authorization-v1','attempt_uuid':self.attempt_uuid,'command':'nx_status',
            'authorization_reference':self.authorization_reference,'launch_receipt_sha256':receipt_hash,
            'probe_path':PROBE,'probe_sha256':PROBE_SHA256,'provenance':self.provenance}
        if self.authorization_ticket!=expected or rows[2]['evidence']!=expected:
            raise PermissionError('Authorization ticket binding mismatch')
        return copy.deepcopy(expected)

    def note_unverified_observation(self, kind, evidence):
        # PID, old logs, namespace presence and MCP initialize never promote state.
        if not isinstance(kind,str) or not kind.strip():raise ValueError('Observation kind required')
        row={'record_type':'observation','sequence':len(self.history),'state':self.state.value,
             'observation':kind,'evidence':copy.deepcopy(evidence),'promoted':False,'provenance':self.provenance}
        self.history.append(row)
        self._observation_chain=extend_chain(self._observation_chain,row)
        self._history_chain=extend_chain(self._history_chain,row)


class FileAttemptStore:
    """Exclusive, durable consumption in a dedicated directory, never a formal ledger."""
    DEFAULT_DIRECTORY = Path(__file__).resolve().parents[2] / 'logs/phase5/p13-status-attempts'

    def __init__(self, directory=None, *, synthetic=False):
        self.directory = Path(directory or self.DEFAULT_DIRECTORY)
        self.synthetic = synthetic
        if not synthetic and self.directory.resolve() != self.DEFAULT_DIRECTORY.resolve():
            raise ValueError('Native attempts use the single canonical store; do not bypass UUID consumption')

    def validate_native(self):
        if type(self) is not FileAttemptStore or self.synthetic or self.directory.resolve()!=self.DEFAULT_DIRECTORY.resolve():
            raise PermissionError('Native attempts require canonical persistent FileAttemptStore')

    def require_unspent(self, attempt_uuid):
        if str(uuid.UUID(attempt_uuid)) != attempt_uuid:
            raise ValueError('Canonical UUID required')
        if hashlib.sha256(attempt_uuid.encode()).hexdigest() in RETIRED_AUTHORIZATION_UUID_SHA256 or previously_consumed(attempt_uuid):
            raise FileExistsError('Previously retired or consumed authorization')
        names = {attempt_uuid + suffix for suffix in (
            '.attempt-started.json','.started.json','.attempt.json','.consumed.json',
            '.terminal.json','.retired.json')}
        generic = {'attempt-started.json','preflight-attempt-started.json','retired.json',
                   'runner-invocation-started.json'}
        def check(directory):
            for item in directory.iterdir():
                if item.name.lower() in names:
                    raise FileExistsError('Prior attempt evidence: ' + str(item))
                if item.name.lower() in generic:
                    info=item.lstat()
                    if not stat.S_ISREG(info.st_mode) or getattr(info,'st_file_attributes',0) & 0x400:
                        raise PermissionError('Unverifiable legacy attempt marker')
                    record=json.loads(item.read_bytes())
                    ids={record[k] for k in ('attempt_uuid','uuid') if k in record}
                    args=record.get('argv',[])
                    if '--attempt-uuid' in args:
                        ids.add(args[args.index('--attempt-uuid')+1])
                    if len(ids)!=1 or any(str(uuid.UUID(value))!=value for value in ids):
                        raise PermissionError('Unverifiable legacy marker identity')
                    if attempt_uuid in ids:
                        raise FileExistsError('Legacy attempt has already started: ' + str(item))

        # New writers share one CreateNew path. Older standalone evidence lives
        # directly in task/report directories; do not open unrelated artifacts.
        try:
            info=self.directory.lstat()
            if not stat.S_ISDIR(info.st_mode) or getattr(info,'st_file_attributes',0) & 0x400:
                raise PermissionError('Unverifiable attempt store directory')
        except FileNotFoundError:
            if not self.synthetic:
                raise PermissionError('Missing canonical native attempt store')
        else:
            check(self.directory)
        roots=() if self.synthetic else (self.directory.parent, self.directory.parents[2]/'reports')
        if self.synthetic and self.directory.is_dir():
            roots=(self.directory,)
        for root in roots:
            for directory in root.iterdir():
                if not self.synthetic and not directory.name.lower().startswith(('p13-','p13_')):
                    continue
                info=directory.lstat()
                if getattr(info,'st_file_attributes',0) & 0x400 or stat.S_ISLNK(info.st_mode):
                    raise PermissionError('Unverifiable attempt directory')
                if stat.S_ISDIR(info.st_mode) and directory!=self.directory:
                    check(directory)

    def consume(self, attempt_uuid, evidence):
        self.require_unspent(attempt_uuid)
        canonical = str(uuid.UUID(attempt_uuid))
        if canonical != attempt_uuid:
            raise ValueError('Canonical UUID required')
        self.directory.mkdir(parents=True, exist_ok=True)
        path = self.directory / (canonical + '.consumed.json')
        with path.open('x', encoding='utf-8') as stream:
            json.dump(evidence, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())

    def finish(self, attempt_uuid, evidence):
        path = self.directory / (str(uuid.UUID(attempt_uuid)) + '.terminal.json')
        with path.open('x', encoding='utf-8') as stream:
            json.dump(evidence, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())


class StatusOnlyAdapter(AcquisitionDiagnostics):
    def __init__(self, *, write_once, read_response_once, attempt_store,
                 readonly_max_attempts=1, expected_probe=PROBE, **acquisition):
        if type(readonly_max_attempts) is not int or readonly_max_attempts != 1:
            raise ValueError('readonly_max_attempts must be 1')
        if acquisition.get('pipe_name') != PIPE:
            raise ValueError('Only the audited pipe name is allowed')
        super().__init__(**acquisition)
        self._write = write_once
        self._read = read_response_once
        self._store = attempt_store
        self.expected_probe = PureWindowsPath(expected_probe)
        self.counts.update(WriteFile=0, ReadResponse=0)
        self._one_run = threading.Lock()  # Python object guard, NOT a Worker/process lock.
        self._consumed = False
        self.write_attempted = False
        self.response = None
        self.raw_response = None
        self.receipt = None

    def acquire_once(self, **kwargs):
        raise PermissionError('Status adapter requires run() and explicit flow authorization')

    def WriteFile(self, *args, **kwargs):
        raise PermissionError('Direct writes forbidden; run() sends fixed nx_status once')

    def ReadFile(self, *args, **kwargs):
        raise PermissionError('Direct reads forbidden; run() owns bounded status response')

    def _safe_error(self, api, error):
        self._capture_error(api, error)

    def run(self, flow, command='nx_status', *, create_args=(), timeout_ms=60000):
        if command != 'nx_status':
            raise PermissionError('Only exact nx_status is allowed, no arguments or newline')
        if not isinstance(flow, RecoveryState) or flow.state != State.STATUS_ATTEMPT_AUTHORIZED:
            raise PermissionError('Separate status-only authorization required')
        if flow.provenance != self.provenance:
            raise ValueError('Flow/adapter provenance mismatch')
        authorization=flow.validate_authorization()
        history_evidence={'schema':'p13-history-v2','rows':copy.deepcopy(flow.history),
            'transition_chain':list(flow._chain),'observation_chain':list(flow._observation_chain),'history_chain':list(flow._history_chain)}
        if self.provenance=='authorized_native_acquisition':
            if type(self._store) is not FileAttemptStore:raise PermissionError('Custom native stores forbidden')
            self._store.validate_native()
            if self.expected_probe!=PureWindowsPath(PROBE):raise PermissionError('Fixed probe required')
        if isinstance(self._store, FileAttemptStore) and self._store.synthetic != (self.provenance == 'synthetic/mock'):
            raise ValueError('Synthetic/native attempt store mismatch')
        if type(timeout_ms) is not int or not 1 <= timeout_ms <= 60000:
            raise ValueError('Finite bounded timeout required')
        with self._one_run:
            if self._consumed:
                raise RetryForbidden('Adapter consumed; no replay')
            self._consumed = True
        primary = None
        primary_tb = None
        known_response = False
        consumed_on_disk = False
        current_api = 'attempt_store'
        try:
            self._store.consume(flow.attempt_uuid, {'time': self._clock(), 'attempt_uuid': flow.attempt_uuid,
                'command': command, 'authorization_reference': flow.authorization_reference,
                'authorization_ticket':authorization,'authorization_ticket_sha256':canonical_hash(authorization),
                'authorization_history':history_evidence,
                'provenance': self.provenance, 'readonly_max_attempts': 1, 'state': State.STATUS_ATTEMPTED.value})
            consumed_on_disk = True
            flow._set(State.STATUS_ATTEMPTED, {'attempt_uuid': flow.attempt_uuid})
            current_api = 'WaitNamedPipe'
            self.WaitNamedPipe(PIPE, 500)
            current_api = 'CreateFile'
            handle = self.CreateFile(PIPE, *create_args)
            if handle is None:
                raise ValueError('CreateFile returned unknown handle')
            current_api = 'WriteFile'
            self._record(current_api, 'before_call', command='nx_status', command_bytes=10)
            self.counts[current_api] = 1
            self.write_attempted = True  # Unknown even if WriteFile raises before reporting byte count.
            written = self._write(handle, b'nx_status\n', timeout_ms)
            if type(written) is not int or written != len(b'nx_status\n'):
                raise IOError('Incomplete/unknown write; never send remaining bytes or retry')
            self._record(current_api, 'success', bytes=written)
            current_api = 'ReadResponse'
            self.counts[current_api] = 1
            raw = self._read(handle, timeout_ms, 1024 * 1024)
            if not isinstance(raw, bytes) or len(raw) > 1024 * 1024 or not raw.endswith(b'\n'):
                raise ValueError('Incomplete/oversized status response')
            self.raw_response = raw.decode('utf-8-sig', errors='strict')
            value = json.loads(self.raw_response, parse_constant=lambda x: (_ for _ in ()).throw(ValueError('Non-finite JSON')))
            if not isinstance(value, dict):
                raise ValueError('Non-object status reply')
            self.response = value
            known_response = True
            self._record(current_api, 'response', response=value)
            if value.get('ok') is not True or value.get('ready') is not True or value.get('nx') != '12.0.2.9' or value.get('loader') != 'nx12_ai_lab':
                raise ValueError('Status rejected/version or loader mismatch')
            if not isinstance(value.get('part'), str) or PureWindowsPath(value['part']) != self.expected_probe:
                raise ValueError('Status work-part path differs from isolated probe; no automatic switch')
        except BaseException as error:
            primary, primary_tb = error, error.__traceback__
            # Acquisition wrapper already captured the original WinError; do not replace it.
            if self._original_error is not error:
                self._safe_error(current_api, error)
        finally:
            try:
                self._cleanup()
            except BaseException as cleanup_error:
                if primary is None:
                    primary, primary_tb = cleanup_error, cleanup_error.__traceback__
            if primary is None and self.cleanup_status != 'CLOSED':
                primary = RuntimeError('Cleanup not confirmed')
            if primary is None:
                flow._set(State.STATUS_SUCCESS, {'response': self.response, 'scope': 'one status only; no session, modified flags, PID handshake or geometry acceptance'})
            elif self.write_attempted and not known_response:
                flow._set(State.UNKNOWN_AFTER_WRITE, {'error_type': type(primary).__name__, 'automatic_replay': False})
            else:
                flow._set(State.STOPPED, {'error_type': type(primary).__name__, 'cleanup_status': self.cleanup_status})
            self.receipt = {'state': flow.state.value, 'attempt_uuid': flow.attempt_uuid,
                'authorization_ticket':authorization,'authorization_ticket_sha256':canonical_hash(authorization),
                'authorization_history':history_evidence,
                'provenance': self.provenance, 'counts': dict(self.counts), 'events': list(self.events),
                'write_attempted': self.write_attempted, 'known_response': known_response,
                'response': self.response, 'raw_response': self.raw_response, 'cleanup_status': self.cleanup_status,
                'transport_events': list(getattr(self, 'transport_events', [])),
                'global_preflight_ready': False, 'session_parts_called': False, 'Worker_lock_acquired': False,
                'smoke_test_run': False, 'replay_allowed': False}
            if consumed_on_disk:
                try:
                    self._store.finish(flow.attempt_uuid, self.receipt)
                except BaseException as persistence_error:
                    if primary is None:
                        primary, primary_tb = persistence_error, persistence_error.__traceback__
                    # No successful final receipt means no release to downstream work.
                    if flow.state == State.STATUS_SUCCESS:
                        flow._set(State.STOPPED, {'terminal_receipt_persistence': 'FAILED'})
                        self.receipt['state'] = flow.state.value
                    self.receipt['terminal_persistence_error'] = type(persistence_error).__name__
        if primary is not None:
            raise primary.with_traceback(primary_tb)
        return self.receipt
