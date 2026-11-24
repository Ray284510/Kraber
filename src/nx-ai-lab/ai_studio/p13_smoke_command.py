"""The fixed P13 fixture rides the existing Worker, MCP adapter and named pipe."""
import hashlib
import secrets
import sys
import json
import os
import re
import uuid
from pathlib import Path

from .task_store import OutcomeUnknown

SMOKE_ROOT = Path(__file__).resolve().parents[1] / 'models/phase4_p13_smoke_e80a1f33'
READS = frozenset({'nx_status', 'nx_session_parts', 'nx_inspect'})


RUN_ENV = 'NX_AI_P13_SINGLE_RUN'
APPROVAL = 'AUTHORIZE P13_FIXED_SMOKE ONCE'


def session_binding(spec):
    start = spec.get('expected_nx_start_utc')
    mvid = spec.get('expected_loader_mvid')
    if not isinstance(start, str) or not re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z', start):
        raise ValueError('Full-precision OS process start time required')
    if not isinstance(mvid, str) or str(uuid.UUID(mvid)) != mvid:
        raise ValueError('Expected candidate Loader MVID required')
    return start, mvid


def join_harness(folder, spec, store):
    """Only the Stage child from this harness run receives the transient value."""
    value = os.environ.pop(RUN_ENV, '')
    if not re.fullmatch('[0-9a-f]{64}', value):
        raise PermissionError('The fixed Smoke Worker requires its one-shot Harness')
    attempt = spec['task_uuid']
    if str(uuid.UUID(attempt)) != attempt:
        raise ValueError('Canonical Smoke task UUID required')
    evidence = json.loads((store.directory/(attempt+'.consumed.json')).read_bytes())
    marker = json.loads((folder/'native-run-started.json').read_bytes())
    if (marker != evidence or evidence.get('scope') != APPROVAL or evidence.get('attempt_uuid') != attempt
            or evidence.get('folder') != str(folder.resolve())
            or evidence.get('run_sha256') != hashlib.sha256(value.encode('ascii')).hexdigest()):
        raise PermissionError('Smoke dispatch is not bound to the consumed Harness invocation')


def admit_harness(folder, spec, preflight_bytes, store, approval):
    """Use the existing canonical store. Approval pins come from the user, outside the task."""
    from .batch_harness_contract import NEW_BLANK, smoke_operations, IDENTITY_READ_LIMITS
    attempt = spec['task_uuid']
    store.require_unspent(attempt)
    if not approval or len(approval) != 3:
        raise PermissionError('Independent Smoke ticket, user message hash and reference required')
    ticket_pin, message_pin, reference = approval
    raw = (folder/'ticket.canonical.json').read_bytes()
    ticket = json.loads(raw)
    message = (folder/'authorization-user-message.utf8.txt').read_bytes()
    auth = json.loads((folder/'authorization-reference.json').read_bytes())
    if not store.synthetic:
        store.validate_native()
        if ticket.get('provenance') != 'user_approved_native_smoke' or auth.get('provenance') != 'original_user_message':
            raise PermissionError('Independent native Smoke authorization record required')
        root = Path(__file__).resolve().parents[1]
        bindings = ticket.get('source_bindings')
        paths = ('ai_studio/nx_worker.py', 'ai_studio/p13_smoke_command.py', 'ai_studio/stage_mcp.py',
                 'ai_studio/task_store.py', 'ai_studio/batch_geometry.py','ai_studio/stage_geometry.py',
                 'ai_studio/batch_harness_contract.py','ai_studio/verify_stage_batch.py','ai_studio/transport_outcome.py',
                 'bridge/mcp_server.py','bridge/phase4_client.py','bridge/pipe_client.py','bridge/native_step.py',
                 'bridge/NX12_Loader.dll','nx-user/startup/NX12_Loader.dll','bridge/p13_strict_launcher_core.psm1')
        if not isinstance(bindings,dict) or set(bindings) != set(paths):
            raise PermissionError('Incomplete source/deployment ticket pins')
        for relative in paths:
            if hashlib.sha256((root/relative).read_bytes()).hexdigest() != bindings[relative]:
                raise PermissionError('Smoke binding changed: '+relative)
    calculated = hashlib.sha256(json.dumps(ticket,sort_keys=True,ensure_ascii=False,separators=(',',':')).encode()).hexdigest()
    if (not reference or auth.get('reference') != reference or auth.get('attempt_uuid') != attempt
            or calculated != ticket_pin or auth.get('ticket_sha256') != ticket_pin
            or hashlib.sha256(message).hexdigest() != message_pin or auth.get('message_sha256') != message_pin
            or message.decode('utf-8').splitlines() != [APPROVAL,'ATTEMPT_UUID='+attempt,'TICKET_SHA256='+ticket_pin]):
        raise PermissionError('Smoke original authorization and ticket do not match')
    start, mvid = session_binding(spec)
    preflight = json.loads(preflight_bytes)
    for receipt in (preflight['status'], preflight['session']):
        if (receipt.get('p13_start_utc') != start or receipt.get('p13_loader_mvid') != mvid
                or receipt.get('p13_server_pid') != spec['expected_server_pid']):
            raise PermissionError('Fresh preflight process incarnation or assembly mismatch')
    fresh = hashlib.sha256(preflight_bytes).hexdigest()
    if (ticket.get('scope') != APPROVAL or ticket.get('attempt_uuid') != attempt or ticket.get('task_uuid') != attempt
            or ticket.get('task_root') != str(folder.resolve()) or ticket.get('models_root') != str(SMOKE_ROOT)
            or ticket.get('expected_server_pid') != spec['expected_server_pid']
            or ticket.get('expected_nx_start_utc') != start or ticket.get('expected_loader_mvid') != mvid
            or ticket.get('preflight_sha256') != fresh or ticket.get('initial_part_contract') != NEW_BLANK
            or spec.get('initial_part_contract') != NEW_BLANK
            or ticket.get('worker_identity_read_max') != IDENTITY_READ_LIMITS
            or ticket.get('operations') != smoke_operations(SMOKE_ROOT/'batch_validation.prt')):
        raise PermissionError('Smoke scope or fresh preflight binding mismatch')
    value = secrets.token_hex(32)
    evidence = dict(scope=APPROVAL,attempt_uuid=attempt,folder=str(folder.resolve()),preflight_sha256=fresh,
                    ticket_sha256=ticket_pin,message_sha256=message_pin,authorization_reference=reference,
                    run_sha256=hashlib.sha256(value.encode('ascii')).hexdigest(),replay_allowed=False)
    if any((folder/name).exists() for name in ('native-run-started.json','batch-native-acceptance.json','batch-native-calls.json','batch-freshness-evidence.json')):
        raise FileExistsError('Prior local Smoke attempt evidence')
    store.consume(attempt,evidence)
    with (folder/'native-run-started.json').open('x',encoding='utf-8') as stream:
        json.dump(evidence,stream)
        stream.flush()
        os.fsync(stream.fileno())
    return value


async def fresh_probe_pair(client, spec, record):
    """The two reads inside an independently authorized preflight. No lock or modeling."""
    from .batch_harness_contract import require_empty_session, require_smoke_execution_policy, NEW_BLANK
    require_smoke_execution_policy(spec)
    if spec.get('initial_part_contract') != NEW_BLANK:
        raise PermissionError('Fresh blank start contract required')
    if Path(spec['models']).resolve() != SMOKE_ROOT.resolve():
        raise PermissionError('Wrong Smoke root')
    status = await correlated_call(client, 'nx_status', spec)
    record('nx_status', status)
    if (status.get('ok') is not True or status.get('nx') != '12.0.2.9'
            or status.get('part') != '' or status.get('p13_display_path') != ''):
        raise OutcomeUnknown('Fresh Smoke work part or response identity mismatch')
    session = await correlated_call(client, 'nx_session_parts', spec)
    record('nx_session_parts', session)
    return require_empty_session(status,session)


def expected_part(operation_id, command, models):
    stage, step, attempt = operation_id.split('.')
    if stage != 'batch-validation' or attempt != '0':
        raise PermissionError('Outside the authorized Smoke stage')
    final = models / 'batch_validation.prt'
    commands = {
        'create': ('nx_create_part ' + str(final), None),
        'solid': ('nx_shape batch_outline box 0 0 0 0 0 1 20 16 4 create -', final),
        'view': ('nx_fit_view', final), 'save': ('nx_save_part', final),
        'close': ('nx_close_part 0', final), 'reopen': ('nx_open_part ' + str(final), None),
    }
    if step == 'hole' and re.fullmatch(r'nx_hole BODY_[1-9][0-9]* 0 0 4 4 0', command):
        return final
    if step not in commands or command != commands[step][0]:
        raise PermissionError('Command or target outside fixed Smoke scope')
    return commands[step][1]


def prepare_intent(operation_id, command, models, identity, current, expected_pid):
    path = expected_part(operation_id, command, models)
    adapter = current.get('pipe_client_pid')
    if (type(expected_pid) is not int or expected_pid <= 0 or type(adapter) is not int or adapter <= 0
            or type(current.get('p13_server_pid')) is not int or current['p13_server_pid'] != expected_pid):
        raise OutcomeUnknown('Missing correlated adapter identity')
    if path is None:
        if current.get('part') != '' or identity is not None:
            raise OutcomeUnknown('Empty session required for create/open')
    elif (not identity or Path(identity['part']) != path
          or identity['process_id'] != expected_pid or not identity.get('part_tag')):
        raise OutcomeUnknown('Operation target differs from the current part')
    return dict(operation=operation_id, command=command, expected_path=str(path) if path else '',
                expected_tag=identity['part_tag'] if identity else '', peer_pid=adapter, nx_pid=expected_pid)


def write_intent(lock, intent):
    # Byte zero stays locked by the original Worker. This transient handoff is
    # not replay authority. TaskStore/MutationDomain remain the durable owners.
    lock.seek(1)
    lock.truncate()
    if intent is not None:
        data = json.dumps(intent, ensure_ascii=True, separators=(',', ':')).encode('ascii')
        if lock.write(data) != len(data):
            raise OSError('Incomplete transient dispatch permit')
    lock.flush()
    os.fsync(lock.fileno())


async def correlated_call(client, command, spec, *, lock=None, intent=None):
    expected_pid = spec['expected_server_pid']
    start, mvid = session_binding(spec)
    request = str(uuid.uuid4())
    if intent is None and command not in READS:
        raise PermissionError('A model operation requires a current Worker reservation')
    if intent is not None:
        if lock is None:
            raise OutcomeUnknown('Original execution lock required')
        write_intent(lock, dict(intent, request=request, start_utc=start, loader_mvid=mvid))
    try:
        reply = await client.call('nx_p13_once ' + request + ' ' + command, True)
        if not isinstance(reply, dict):
            raise OutcomeUnknown('Malformed scoped response')
        transport = reply.get('transport_receipt')
        # Preserve a proven pre-write error. Everything else without a matching
        # native envelope is uncertain, including an incompatible running DLL.
        from .transport_outcome import unsent
        if reply.get('ok') is False and reply.get('execution_started') is False and unsent(transport):
            return reply
        counts = transport.get('counts', {}) if isinstance(transport, dict) else {}
        strict_counts = ('WaitNamedPipe','CreateFile','GetNamedPipeServerProcessId','WriteFile','CloseHandle')
        valid = (isinstance(transport, dict) and isinstance(counts, dict)
                 and all(type(counts.get(k)) is int and counts[k] == 1 for k in strict_counts)
                 and type(counts.get('ReadFile')) is int and counts['ReadFile'] >= 1
                 and type(counts.get('event_CloseHandle')) is int and counts['event_CloseHandle'] == counts['ReadFile'] + 1
                 and transport.get('strict_single_connect') is True and transport.get('retry') is False
                 and type(transport.get('peer_pid')) is int and transport['peer_pid'] == expected_pid
                 and transport.get('cleanup_errors') == [] and not transport.get('error')
                 and transport.get('outcome') == 'RESPONSE_RECEIVED')
        if command == 'nx_status' and reply.get('ok') is True:
            work, display = reply.get('part'), reply.get('p13_display_path')
            valid = valid and isinstance(work,str) and isinstance(display,str) and (
                work == display == '' or (bool(work) and bool(display) and Path(work) == Path(display)))
        if (not valid or reply.get('p13_start_utc') != start or reply.get('p13_loader_mvid') != mvid
                or reply.get('p13_request') != request or reply.get('p13_command') != command
                or type(reply.get('p13_server_pid')) is not int or reply['p13_server_pid'] != expected_pid
                or type(reply.get('pipe_client_pid')) is not int or reply['pipe_client_pid'] <= 0
                or (intent is not None and reply['pipe_client_pid'] != intent['peer_pid'])):
            return dict(ok=False, outcome='UNKNOWN_AFTER_WRITE', retry_safe=False,
                        error='Smoke response identity or physical receipt incomplete',
                        transport_receipt=transport, received_result=reply, expected_request=request)
        return reply
    finally:
        primary = sys.exc_info()[1]
        if intent is not None:
            try:
                write_intent(lock, None)
            except OSError as error:
                # Domain remains reserved; never turn cleanup failure into NOT_SENT.
                if primary is not None:
                    primary.smoke_cleanup_failed = True
                    primary.smoke_cleanup_error = repr(error)
                    primary.add_note('Worker permit cleanup failed: ' + repr(error))
                    raise primary from error
                error.smoke_cleanup_failed = True
                if 'reply' in locals():
                    error.received_result = reply
                    if isinstance(reply, dict):
                        error.transport_receipt = reply.get('transport_receipt')
                raise
