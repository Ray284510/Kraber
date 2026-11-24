"""Offline only: actual Worker/state code with fake clients/locks, temporary SQLite."""
import json,sqlite3,tempfile,unittest,copy,hashlib
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock,patch
from . import nx_worker,stage_mcp
from .task_store import TaskStore,OutcomeUnknown
from .test_geometry_gate_p11 import MockServer,policy,solid
from .batch_harness_contract import prepared_paths,require_probe_session,require_temporal_evidence

MOCK_RECEIPTS=[]

def fake_client(side_effect=None):
    c=SimpleNamespace(call=AsyncMock(side_effect=side_effect),tool=AsyncMock(),__aexit__=AsyncMock(return_value=False))
    c.__aenter__=AsyncMock(return_value=c)
    return c


class RetryTests(unittest.IsolatedAsyncioTestCase):
    def make(self,attempts=1):
        temp=tempfile.TemporaryDirectory(dir=nx_worker.ROOT/'tmp');self.addCleanup(temp.cleanup)
        w=nx_worker.Worker.__new__(nx_worker.Worker);w.folder=Path(temp.name);w.store=TaskStore(w.folder)
        self.addCleanup(w.store.db.close)
        w.provider='codex';w.model_root=w.folder/'models';w.spec={'readonly_max_attempts':attempts}
        if attempts>1:w.spec['readonly_retry_authorization']='synthetic explicit authorization for offline test only'
        w.readonly_max_attempts=nx_worker.readonly_policy(w.spec);w.lock=None;w._lock_held=False
        w.client=fake_client();w._client_context=w.client
        return w

    def record(self,w):
        rows=[dict(r) for r in w.store.db.execute('SELECT * FROM events ORDER BY n')]
        MOCK_RECEIPTS.append(dict(test=self.id(),provenance='synthetic/mock; no actual Worker lock or MCP',events=rows))

    async def test_strict_one_attempt_stops(self):
        w=self.make();w.client.call.side_effect=OSError('synthetic transport failure')
        with patch.object(nx_worker,'Client',side_effect=AssertionError('Must not reconnect')) as factory:
            with self.assertRaises(OSError):await w.observe('nx_status')
        self.assertEqual(w.client.call.await_count,1);factory.assert_not_called();self.record(w)

    async def test_three_authorized_attempts_bounded(self):
        w=self.make(3);clients=[w.client,fake_client(),fake_client()]
        for c in clients:c.call.side_effect=OSError('synthetic transport failure')
        with patch.object(nx_worker,'Client',side_effect=clients[1:]),patch.object(nx_worker.asyncio,'sleep',new=AsyncMock()):
            with self.assertRaises(OSError):await w.observe('nx_status')
        self.assertEqual([c.call.await_count for c in clients],[1,1,1])
        self.assertEqual([c.__aexit__.await_count for c in clients],[1,1,0]);self.record(w)
        await w._close_client();self.assertEqual(clients[-1].__aexit__.await_count,1)

    async def test_transport_error_receipt_stops_strict(self):
        w=self.make();w.client.call.return_value={'ok':False,'transport_error':'synthetic','retry_safe':False}
        with self.assertRaises(OSError):await w.observe('nx_status')
        self.assertEqual(w.client.call.await_count,1);self.record(w)

    async def test_nontransport_failure_not_retried_with_three_allowed(self):
        w=self.make(3);w.client.call.side_effect=ValueError('synthetic native validation failure')
        with patch.object(nx_worker,'Client') as factory:
            with self.assertRaises(ValueError):await w.observe('nx_status')
        factory.assert_not_called();self.assertEqual(w.client.call.await_count,1);self.record(w)

    async def test_observation_log_failure_is_not_retried(self):
        w=self.make(3);w.client.call.return_value={'ok':True,'provenance':'synthetic'}
        original=w.store.event
        def event(kind,data):
            if kind=='nx_observation':raise OSError('synthetic evidence write failure')
            return original(kind,data)
        with patch.object(w.store,'event',side_effect=event),patch.object(nx_worker,'Client') as factory:
            with self.assertRaises(OSError):await w.observe('nx_status')
        factory.assert_not_called();self.assertEqual(w.client.call.await_count,1)

    async def test_mutation_unknown_never_replayed(self):
        w=self.make(3)
        async def call(command,*args):
            if command=='nx_status':return {'ok':True,'part':str(w.model_root/'test.prt'),'nx':'12.0.2.9'}
            return {'ok':False,'transport_error':'synthetic unknown mutation','retry_safe':False}
        w.client.call.side_effect=call
        w.client.tool.return_value=dict(ok=True,nx='12.0.2.9',process_id=123,all_modified_flags_known=True,
                                        parts=[{'path':str(w.model_root/'test.prt'),'tag':'10'}])
        with self.assertRaises(RuntimeError):await w.execute('one','nx_shape test')
        with self.assertRaises(OutcomeUnknown):await w.execute('one','nx_shape test')
        commands=[x.args[0] for x in w.client.call.await_args_list]
        self.assertEqual(commands.count('nx_shape test'),1)
        self.assertEqual(w.store.db.execute("SELECT state FROM operations WHERE id='one'").fetchone()[0],'unknown')
        self.record(w)

    async def test_entry_failure_closes_partial_client_and_database(self):
        w=self.make();c=fake_client();c.__aenter__.side_effect=OSError('synthetic startup failure')
        with patch.object(w,'_acquire_lock'),patch.object(w,'_unlock') as unlock,patch.object(nx_worker,'Client',return_value=c):
            with self.assertRaises(OSError):await w.__aenter__()
        c.__aexit__.assert_awaited_once();unlock.assert_called_once()
        with self.assertRaises(sqlite3.ProgrammingError):w.store.db.execute('SELECT 1')

    async def test_client_close_failure_still_releases_lock_and_db(self):
        w=self.make();w._client_context.__aexit__.side_effect=OSError('synthetic close failure')
        with patch.object(w,'_unlock') as unlock:
            with self.assertRaises(OSError):await w.__aexit__(None,None,None)
        unlock.assert_called_once();self.assertIsNone(w._client_context)
        with self.assertRaises(sqlite3.ProgrammingError):w.store.db.execute('SELECT 1')

    async def test_database_close_failure_still_releases_lock(self):
        w=self.make();w.store.db.close();w.store=SimpleNamespace(db=Mock())
        w.store.db.close.side_effect=OSError('synthetic db close failure')
        with patch.object(w,'_unlock') as unlock:
            with self.assertRaises(OSError):await w.__aexit__(None,None,None)
        unlock.assert_called_once()

    async def test_retry_cleanup_failure_does_not_reconnect_or_replay(self):
        w=self.make(3);w.client.call.side_effect=OSError('synthetic read failure')
        w._client_context.__aexit__.side_effect=RuntimeError('synthetic cleanup failure');c=w.client
        with patch.object(nx_worker,'Client') as factory:
            with self.assertRaises(RuntimeError):await w.observe('nx_status')
        factory.assert_not_called();self.assertEqual(c.call.await_count,1);self.record(w)

    async def test_lock_unavailable_never_opens_client(self):
        w=self.make()
        with patch.object(w,'_acquire_lock',side_effect=RuntimeError('synthetic lock unavailable')),patch.object(w,'_unlock') as unlock,patch.object(nx_worker,'Client') as factory:
            with self.assertRaises(RuntimeError):await w.__aenter__()
        factory.assert_not_called();unlock.assert_called_once()

    async def test_unlock_failure_still_closes_fake_handle(self):
        w=self.make();lock=Mock();lock.fileno.return_value=42;w.lock=lock;w._lock_held=True
        with patch.object(nx_worker.msvcrt,'locking',side_effect=OSError('synthetic unlock failure')):
            with self.assertRaises(OSError):w._unlock()
        lock.close.assert_called_once();self.assertIsNone(w.lock);self.assertFalse(w._lock_held)


class PolicyTests(unittest.TestCase):
    def test_default_conservative_and_explicit_recovery_authority(self):
        self.assertEqual(nx_worker.readonly_policy({}),1)
        for v in (None,True,False,0,4,'3',1.0):
            with self.assertRaises(ValueError):nx_worker.readonly_policy({'readonly_max_attempts':v})
        with self.assertRaises(ValueError):nx_worker.readonly_policy({'readonly_max_attempts':3})
        self.assertEqual(nx_worker.readonly_policy({'readonly_max_attempts':3,'readonly_retry_authorization':'synthetic authorization'}),3)


class FreshnessTests(unittest.IsolatedAsyncioTestCase):
    async def run_scenario(self,post_inspect=True):
        temp=tempfile.TemporaryDirectory(dir=stage_mcp.ROOT/'tmp');self.addCleanup(temp.cleanup)
        folder=Path(temp.name);(folder/'stages').mkdir();db=sqlite3.connect(':memory:');self.addCleanup(db.close)
        db.executescript('CREATE TABLE operations(id,state,payload);CREATE TABLE task_meta(name,value);')
        models=stage_mcp.ROOT/'models/p13_mock_only';probe=models/'broker_probe.prt';target=models/'batch_validation.prt'
        active={'part':str(probe),'tag':'10'}
        def session():return dict(ok=True,nx='12.0.2.9',process_id=123,all_modified_flags_known=True,any_modified=False,
            parts=[dict(path=active['part'],tag=active['tag'],is_modified=False)] if active['part'] else [],provenance='synthetic/mock')
        async def observe(command):
            if command=='nx_status':return dict(ok=True,nx='12.0.2.9',part=active['part'],provenance='synthetic/mock')
            if command=='nx_session_parts':return session()
            if command=='nx_inspect':return dict(solid(),part=active['part'],provenance='synthetic/mock')
            raise ValueError('Unexpected synthetic observation '+command)
        async def action(identity,command,**kw):
            if command.startswith('nx_close_part'):active['part']=''
            if command.startswith(('nx_create_part ','nx_open_part ')):
                active['part']=command.split()[1];active['tag']=str(int(active['tag'])+1)
            result=dict(ok=True,body_id='actual_mock_body',saved=active['part'],provenance='synthetic/mock')
            if command.startswith('nx_shape '):
                ctx=kw['handle_context']
                result.update(receipt_kind='fresh_dispatch',handle_binding={
                    'batch_id':ctx['batch_id'],'generation':ctx['next_generation'],
                    'model_identity':stage_mcp.model_identity(await observe('nx_status'),ctx['session'],ctx['stage_session_id'])})
            return result
        store=SimpleNamespace(db=db,switch_provider=Mock(),checkpoint=Mock(return_value={'operations':[],'unresolved':[]}))
        worker=SimpleNamespace(observe=AsyncMock(side_effect=observe),execute=AsyncMock(side_effect=action),store=store,model_root=models,recover_receipts=Mock())
        class Context:
            async def __aenter__(self):return worker
            async def __aexit__(self,*a):return False
        ops=[{'step':'close_initial','command':'nx_close_part 0'},{'step':'create','command':'nx_create_part '+str(target)},
             {'step':'solid','command':'nx_shape fixture'},{'step':'hole','command':'nx_hole $solid.body_id 0 0 4 4 0'},
             {'command':'nx_inspect'},{'step':'view','command':'nx_fit_view'},
             {'step':'save','command':'nx_save_part'},{'step':'close','command':'nx_close_part 0'},
             {'step':'reopen','command':'nx_open_part '+str(target)}]
        if post_inspect:ops.append({'command':'nx_inspect'})
        spec={'allowed_commands':['nx_create_part','nx_close_part','nx_open_part','nx_shape','nx_hole','nx_fit_view','nx_save_part'],'geometry_expectation':policy()}
        with patch.object(stage_mcp,'valid_stage',return_value=(folder,spec)),patch.object(stage_mcp,'FastMCP',MockServer),patch.object(stage_mcp,'Worker',return_value=Context()):
            server=stage_mcp.build_server(folder,'p13-mock','codex')
            async with server.lifespan(server):
                result=await server.tools['nx_batch'](ops)
                checkpoint=await server.tools['stage_checkpoint']('synthetic checkpoint only')
        MOCK_RECEIPTS.append(dict(test=self.id(),provenance='synthetic/mock actual nx_batch; no NX/MCP',operations=ops,result=result,checkpoint=checkpoint))
        return result,checkpoint

    async def test_full_state_snapshots_immutable_and_bound(self):
        r,c=await self.run_scenario();e=require_temporal_evidence(r)
        self.assertTrue(e['first_inspection']['geometry_pass']);self.assertFalse(r['inspections'][0]['geometry_pass'])
        self.assertTrue(r['results'][5]['freshness_after_operation']['geometry_pass'])
        for i in (6,7,8):
            self.assertTrue(r['results'][i]['freshness_before_dispatch']['geometry_dirty'])
            self.assertFalse(r['results'][i]['freshness_after_operation']['geometry_pass'])
        self.assertEqual(e['final_inspection']['inspection_binding']['last_geometry_mutation_index'],9)
        self.assertEqual(e['final_inspection']['inspection_binding']['model_identity']['part_tag'],'12')
        self.assertFalse(c['geometry_pass']);self.assertEqual(c['geometry_status'],'NOT_EVALUATED_CHECKPOINT_ONLY')

    async def test_no_post_inspection_checkpoint_cannot_restore(self):
        r,c=await self.run_scenario(False)
        self.assertTrue(r['operation_ok']);self.assertFalse(r['geometry_pass']);self.assertTrue(r['inspection_stale'])
        self.assertFalse(c['geometry_pass'])

    async def test_harness_rejects_missing_or_forged_snapshot(self):
        r,c=await self.run_scenario();bad=copy.deepcopy(r);bad['results'][6]['freshness_after_operation']['geometry_pass']=True
        with self.assertRaises(ValueError):require_temporal_evidence(bad)

    async def test_harness_cannot_run_without_later_confirmation(self):
        from .verify_stage_batch import verify
        with patch('ai_studio.verify_stage_batch.stdio_client',side_effect=AssertionError('No transport')) as client:
            with self.assertRaises(ValueError):await verify(Path('nonexistent'))
        client.assert_not_called()


class ProbeContractTests(unittest.TestCase):
    def test_offline_copy_path_hash_and_fresh_target_contract(self):
        with tempfile.TemporaryDirectory(dir=stage_mcp.ROOT/'tmp') as t:
            root=Path(t);folder=root/'logs/phase5/task';folder.mkdir(parents=True)
            models=root/'models/task';models.mkdir(parents=True);probe=models/'broker_probe.prt'
            probe.write_bytes(b'synthetic fixture only, not NX model')
            spec={'models':str(models),'readonly_max_attempts':1,'probe':{'copy_path':str(probe),'sha256':hashlib.sha256(probe.read_bytes()).hexdigest()}}
            self.assertEqual(prepared_paths(folder,spec,root)[1],probe)
            (models/'batch_validation.prt').write_bytes(b'synthetic collision')
            with self.assertRaises(ValueError):prepared_paths(folder,spec,root)
    def test_empty_or_wrong_probe_session_rejected(self):
        probe=Path(str(Path(__file__).resolve().parents[1] / 'models/p13_mock/probe.prt')).resolve()
        status=dict(ok=True,nx='12.0.2.9',part=str(probe))
        session=dict(ok=True,nx='12.0.2.9',process_id=123,all_modified_flags_known=True,any_modified=False,parts=[])
        with self.assertRaises(ValueError):require_probe_session(status,session,probe)
        session['parts']=[dict(path=str(probe),tag='1',is_modified=False)]
        self.assertEqual(require_probe_session(status,session,probe)['tag'],'1')
        session['any_modified']=True
        with self.assertRaises(ValueError):require_probe_session(status,session,probe)

if __name__=='__main__':unittest.main()
