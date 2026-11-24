"""Serialized real MCP execution with durable receipts and a separate process lock."""
import asyncio
import hashlib
import json
import msvcrt
import os
import sys
import uuid
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'bridge'))
from lab_paths import ROOT as VERIFIED_ROOT
from phase4_client import Client
from .task_store import TaskStore, MutationDomain, OutcomeUnknown, canonical
from .transport_outcome import READ_ONLY, uncertain, unsent
from .batch_geometry import model_identity, operation_capability
from .batch_harness_contract import require_smoke_execution_policy
from . import p13_smoke_command as smoke


def readonly_policy(spec):
    attempts=spec.get('readonly_max_attempts',1)
    if type(attempts) is not int or not 1<=attempts<=3:
        raise ValueError('readonly_max_attempts must be an integer from 1 to 3')
    authorization=spec.get('readonly_retry_authorization')
    if attempts>1 and (not isinstance(authorization,str) or not authorization.strip()):
        raise ValueError('Read-only recovery requires an explicit authorization reference')
    return attempts


def transport_options(spec):
    strict=spec.get('strict_single_connect',False)
    if type(strict) is not bool:raise ValueError('strict_single_connect must be bool')
    pid=spec.get('expected_server_pid')
    if strict and (type(pid) is not int or pid<=0 or readonly_policy(spec)!=1):
        raise ValueError('Strict transport requires positive peer PID and one outer attempt')
    return dict(strict_single_connect=strict,expected_server_pid=pid if strict else None)


def readonly_transport_failure(exc):
    if isinstance(exc,(OSError,TimeoutError)):return True
    # Client.tool exposes a bridge failure as RuntimeError(JSON). Native
    # validation/geometry failures are not a reason to reconnect/retry.
    try:payload=json.loads(str(exc))
    except (ValueError,TypeError):return False
    return isinstance(payload,dict) and bool(payload.get('transport_error'))


def historical_result(cached,allow_failure):
    if not cached.get('ok') and not allow_failure:
        raise RuntimeError('Previously failed operation; new repair ID required')
    return {**{k:v for k,v in cached.items() if k not in ('body_id','sketch_id','handle_binding')},
            'receipt_kind':'cached_historical','historical_result':cached}


class Worker:
    def __init__(self,folder,provider='codex'):
        self.folder=Path(folder).resolve()
        if not self.folder.is_relative_to(ROOT/'logs/phase5'):raise ValueError('Task data must stay in phase5 logs')
        self.provider=provider
        self.spec=json.loads((self.folder/'task_spec.json').read_text(encoding='utf-8'))
        self.readonly_max_attempts=readonly_policy(self.spec)
        transport_options(self.spec)  # Validate strict config before creating task storage.
        if self.spec.get('strict_single_connect'):require_smoke_execution_policy(self.spec)
        self.model_root=Path(self.spec['models']).resolve()
        if not self.model_root.is_relative_to(ROOT/'models'):raise ValueError('Invalid model root')
        if self.model_root == smoke.SMOKE_ROOT:
            require_smoke_execution_policy(self.spec)
            from .diagnostics.status_only import FileAttemptStore
            smoke.join_harness(self.folder,self.spec,FileAttemptStore())
        self.store=TaskStore(self.folder, execution_root=ROOT/'logs/phase5')
        self.lock=None;self._lock_held=False;self._client_context=None;self.client=None

    def _acquire_lock(self):
        lock_path = ROOT / 'logs/phase5/nx-execution.lock'
        fd = os.open(lock_path, os.O_CREAT | os.O_RDWR | os.O_BINARY)
        self.lock = os.fdopen(fd, 'r+b', buffering=0)
        try:
            self.lock.seek(0)
            msvcrt.locking(self.lock.fileno(),msvcrt.LK_NBLCK,1)
            self._lock_held=True
            self.lock.write(b'0')
            self.lock.truncate(1)
            self.lock.flush()
        except BaseException:
            self.lock.close();self.lock=None;self._lock_held=False
            raise RuntimeError('Phase5 NX worker lock unavailable')

    async def _open_client(self):
        self._client_context=Client(log_stream=sys.stderr,**transport_options(self.spec))
        try:self.client=await self._client_context.__aenter__()
        except BaseException:
            await self._close_client()
            raise

    async def _close_client(self):
        context=self._client_context;self._client_context=None;self.client=None
        if context is not None:await context.__aexit__(None,None,None)

    async def __aenter__(self):
        try:
            self._acquire_lock()
            await self._open_client()
        except BaseException:
            try:self.store.db.close()
            finally:self._unlock()
            raise
        return self

    def _unlock(self):
        lock=self.lock;held=self._lock_held;self.lock=None;self._lock_held=False
        if lock is not None:
            try:
                if held:
                    lock.seek(0);msvcrt.locking(lock.fileno(),msvcrt.LK_UNLCK,1)
            finally:lock.close()

    async def __aexit__(self,*args):
        # Close transport cleanly; forwarding a body SystemExit into AnyIO wraps it
        # in BaseExceptionGroup and obscures the supervisor's controlled exit code.
        try:await self._close_client()
        finally:
            try:self.store.db.close()
            finally:self._unlock()

    async def observe(self,command):
        protected = getattr(self, 'model_root', None) == smoke.SMOKE_ROOT
        if protected and command not in smoke.READS:
            raise PermissionError('Outside the fixed Smoke observation scope')
        if command.split()[0] not in READ_ONLY:raise ValueError('Not an approved read-only operation')
        if self.spec.get('strict_single_connect'):
            require_smoke_execution_policy(self.spec)
            if command in ('nx_status','nx_session_parts'):
                counts=getattr(self,'_identity_read_counts',{});self._identity_read_counts=counts
                limit=self.spec['internal_identity_reads_authorization']['limits'][command]
                if counts.get(command,0)>=limit:raise PermissionError('Internal identity read budget exhausted')
                counts[command]=counts.get(command,0)+1
        for attempt in range(1,self.readonly_max_attempts+1):
            self.store.event('readonly_attempt',{'command':command,'attempt':attempt,'max_attempts':self.readonly_max_attempts})
            try:
                if protected:
                    result=await smoke.correlated_call(self.client, command, self.spec)
                    if result.get('ok') is not True:
                        self.store.event('smoke_observation_failed', {'command':command,'response':result})
                        raise OutcomeUnknown('Correlated Smoke observation failed')
                elif command=='nx_session_parts':result=await self.client.tool(command,{})
                else:result=await self.client.call(command,True)
                if isinstance(result,dict) and result.get('transport_error'):
                    raise OSError('Read-only transport error returned by MCP')
            except Exception as exc:
                transport=readonly_transport_failure(exc)
                retry=transport and attempt<self.readonly_max_attempts
                self.store.event('readonly_transport_error',{'command':command,'attempt':attempt,'type':type(exc).__name__,
                    'max_attempts':self.readonly_max_attempts,'retry_planned':retry,
                    'reason':'classified_transport_failure' if transport else 'nontransport_or_unclassified_no_retry',
                    'authorization':self.spec.get('readonly_retry_authorization')})
                if not retry:raise
                # Read-only connection recovery. No ambiguous mutation is resent.
                # Cleanup/reconnection failure terminates immediately; no hidden retry.
                await self._close_client()
                await asyncio.sleep(.2*attempt)
                await self._open_client()
            else:
                if self.spec.get('strict_single_connect') and command=='nx_session_parts':
                    parts=result.get('parts')
                    if not isinstance(parts,list) or len(parts)>1 or any(
                            not isinstance(p,dict) or not p.get('path') or
                            not Path(p['path']).resolve().is_relative_to(self.model_root) for p in parts):
                        raise PermissionError('Unexpected loaded part; stop without operating it')
                # An evidence-store failure is terminal, never a transport retry.
                self.store.event('nx_observation',{'command':command,'attempt':attempt,'response':result})
                return result

    async def execute(self,operation_id,command,*,allow_failure=False,interrupt_after_receipt=False,
                      handle_context=None, required_handles=(), source_receipt=None):
        words=command.split();name=words[0]
        protected = self.model_root == smoke.SMOKE_ROOT
        if protected:
            if not getattr(self, '_lock_held', False):
                raise OutcomeUnknown('Fixed Smoke requires the held execution lock')
            if name in READ_ONLY:
                return await self.observe(command)
            smoke.expected_part(operation_id, command, self.model_root)
        if name in {'nx_surface_spline_fixture','nx_surface_edit_pole'}:
            raise PermissionError('Spline repair budget exhausted; no further mutation dispatched')
        if name not in READ_ONLY:
            current=await self.observe('nx_status')
            if current.get('ok') is not True:
                raise RuntimeError('NX status unavailable; mutation was not dispatched')
            if name in {'nx_surface_sweep_general_fixture','nx_surface_sweep_general','nx_surface_sweep_general_edit','nx_surface_loft','nx_surface_shell','nx_surface_sew','nx_surface_thicken',
                        'nx_surface_spline_fixture','nx_surface_edit_pole','nx_surface_edit_ellipse','nx_surface_edit_arc','nx_surface_trim','nx_surface_extend','nx_surface_fixture','nx_surface_sweep','nx_surface_sweep_fixture'}:
                if not current.get('ok') or not current.get('part'):
                    raise PermissionError('Actual active surface experiment part required')
                active = Path(current['part']).resolve()
                authorized = {Path(p).resolve() for p in self.spec.get('surface_writable_parts', [])}
                if active not in authorized or not active.is_relative_to(self.model_root):
                    raise PermissionError('Only explicitly authorized new surface parts may be changed')
                if name in {'nx_surface_sweep','nx_surface_sweep_fixture'} and not active.stem.startswith('phase4_sweep_'):
                    raise PermissionError('Dedicated sweep experiment part required')
            if current.get('part') and not Path(current['part']).resolve().is_relative_to(self.model_root):
                raise PermissionError('Refusing to change a part outside this task')
            for word in words[1:]:
                if ':\\' in word or ':/' in word:
                    if not Path(word).resolve().is_relative_to(self.model_root):
                        raise PermissionError('Command path is outside this task')
        payload={'command':command}
        cached=self.store.cached_result(operation_id,payload)
        if cached is not None:
            return historical_result(cached,allow_failure)

        identity = None
        mutation = name not in READ_ONLY
        if mutation:
            if handle_context is None or 'batch_id' not in handle_context:
                if not hasattr(self,'_direct_scope'):
                    self._direct_scope=uuid.uuid4().hex
                    self._direct_generation=0
                context=handle_context or {}
                session=context.get('session')
                if session is None:
                    session=await self.observe('nx_session_parts')
                generation=self._direct_generation
                if operation_capability(name) in ('geometry_mutation','unknown'):
                    self._direct_generation+=1
                handle_context=dict(session=session,stage_session_id=context.get('stage_session_id',self._direct_scope),
                                    batch_id=self._direct_scope,generation=generation,next_generation=self._direct_generation)
            context=handle_context
            session=context['session']
            identity=model_identity(current,session,context['stage_session_id'])
            expected_pid=self.spec.get('expected_server_pid')
            if expected_pid is not None and (not isinstance(session,dict) or session.get('process_id')!=expected_pid):
                raise OutcomeUnknown('Session PID differs from the task execution identity')
            empty_session=(name in ('nx_create_part','nx_open_part','nx_import_step') and current.get('part')==''
                           and isinstance(session,dict) and session.get('ok') is True and session.get('parts')==[]
                           and session.get('all_modified_flags_known') is True and type(session.get('process_id')) is int
                           and session['process_id']>0 and isinstance(session.get('nx'),str)
                           and bool(session['nx']) and session['nx']==current.get('nx'))
            if not identity and not empty_session:
                raise OutcomeUnknown('Target part/session identity unknown; mutation not dispatched')
            if source_receipt is not None:
                if source_receipt.get('receipt_kind')!='fresh_dispatch' or not source_receipt.get('handle_binding'):
                    raise OutcomeUnknown('Fresh bound source receipt required')
                required_handles=tuple((source_receipt[k],source_receipt['handle_binding'])
                                       for k in ('body_id','sketch_id') if source_receipt.get(k))
            operands = {token for word in words[1:] for token in word.split(',')
                        if token.upper().startswith(('BODY_', 'SKETCH_'))}
            if operands or required_handles:
                authorized = set()
                for value,binding in required_handles:
                    if (getattr(self,'_current_handles',{}).get(value) != canonical(binding)
                            or identity is None or binding.get('model_identity') != identity
                            or binding.get('batch_id') != context.get('batch_id')
                            or binding.get('generation') != context.get('generation')):
                        raise OutcomeUnknown('Current object identity or generation is unknown')
                    authorized.add(value)
                if not operands <= authorized:
                    raise OutcomeUnknown('Unbound object handle; current batch receipt required')

        if mutation and (handle_context is None or context['generation']!=context['next_generation']):
            self._current_handles={}
        intent = (smoke.prepare_intent(operation_id, command, self.model_root, identity, current,
                                       self.spec['expected_server_pid']) if protected else None)
        key=(str(self.folder.resolve()),operation_id,hashlib.sha256(canonical(payload).encode()).hexdigest())
        domain=MutationDomain(self.store.execution_root) if mutation else None
        if domain is not None:
            prior=domain.begin(key,self.store,payload)
            if prior is not None:
                return historical_result(prior,allow_failure)
            with self.store.db:
                self.store.db.execute("INSERT OR IGNORE INTO task_meta VALUES ('mutation_domain_enrolled','1')")
        duplicate=self.store.claim(operation_id,payload,'nx_command' if mutation else 'nx_observation',self.provider)
        if duplicate is not None:
            raise OutcomeUnknown('Concurrent claim changed; no native dispatch')
        try:
            try:
                if protected:
                    result = await smoke.correlated_call(self.client, command, self.spec,
                                                         lock=self.lock, intent=intent)
                else:
                    result=await self.client.call(command,True)
            except Exception as exc:
                transport=getattr(exc,'transport_receipt',None)
                safe=unsent(transport) and not getattr(exc, 'smoke_cleanup_failed', False)
                result={'ok':False,'error_type':type(exc).__name__,'error':str(exc),
                        'execution_started':False if safe else None,'retry_safe':safe,
                        'outcome':'NOT_SENT' if safe else 'UNKNOWN_AFTER_WRITE','transport_receipt':transport}
                if getattr(exc, 'smoke_cleanup_failed', False):
                    result['permit_cleanup_error'] = getattr(exc, 'smoke_cleanup_error', str(exc))
                    result['received_result'] = getattr(exc, 'received_result', None)
            if mutation and uncertain(result):
                result={'ok':False,'outcome':'UNKNOWN_AFTER_WRITE','retry_safe':False,
                        'transport_receipt':result.get('transport_receipt') if isinstance(result,dict) else None,
                        'received_result':result}
            receipt={'task':key[0],'operation_id':operation_id,'payload_digest':key[2],
                     'response':result,'provenance':'Worker command response'}
            receipts=self.folder/'receipts';receipts.mkdir(exist_ok=True)
            p=receipts/(hashlib.sha256(operation_id.encode()).hexdigest()+'.json')
            raw=json.dumps(receipt,ensure_ascii=False,indent=2).encode('utf-8')
            with p.open('xb') as f:
                f.write(raw);f.flush();os.fsync(f.fileno())
            if domain is not None:
                domain.seal(key,raw)
                if uncertain(result):
                    raise OutcomeUnknown('UNKNOWN_AFTER_WRITE; original transport evidence preserved')
            if interrupt_after_receipt:
                self.store.event('controlled_interruption',{'id':operation_id,'point':'after response, before state commit'})
                raise SystemExit(75)
            self.store.complete(operation_id,result)
            if domain is not None:
                domain.settle(key,p.read_bytes(),self.store)
        except Exception as exc:
            self.store.unknown(operation_id,str(exc));raise
        if (mutation and handle_context and identity and result.get('ok') is True
                and name not in ('nx_open_part','nx_create_part','nx_close_part','nx_save_part')):
            result=dict(result,receipt_kind='fresh_dispatch',handle_binding={
                'model_identity':identity,'batch_id':handle_context['batch_id'],
                'generation':handle_context['next_generation']})
            if not hasattr(self,'_current_handles'):
                self._current_handles={}
            for field in ('body_id','sketch_id'):
                if isinstance(result.get(field),str):
                    self._current_handles[result[field]]=canonical(result['handle_binding'])
        if not result.get('ok') and not allow_failure:raise RuntimeError(json.dumps(result,ensure_ascii=False))
        return result

    def recover_receipts(self):
        domain=MutationDomain(self.store.execution_root)
        # Read-only receipts have no mutation reservation; preserve independent
        # recovery for interrupted observations without releasing the domain.
        for row in self.store.db.execute("SELECT id,payload FROM operations WHERE state IN ('running','unknown')"):
            if json.loads(row['payload'])['command'].split()[0] in READ_ONLY:
                path=self.folder/'receipts'/(hashlib.sha256(row['id'].encode()).hexdigest()+'.json')
                try:receipt=json.loads(path.read_bytes())
                except FileNotFoundError:continue
                if isinstance(receipt.get('response'),dict) and type(receipt['response'].get('ok')) is bool:
                    self.store.reconcile_receipt(row['id'],receipt)
        key=domain.pending()
        if key is None or key[0]!=str(self.folder.resolve()):
            return
        path=self.folder/'receipts'/(hashlib.sha256(key[1].encode()).hexdigest()+'.json')
        raw=path.read_bytes()
        domain.checked_response(key,raw)
        row=self.store.db.execute('SELECT state FROM operations WHERE id=?',(key[1],)).fetchone()
        if not row:
            raise OutcomeUnknown('Task operation missing; shared reservation retained')
        if row[0] in ('unknown','running'):
            self.store.reconcile_receipt(key[1],json.loads(raw))
        domain.settle(key,raw,self.store)
