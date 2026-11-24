"""Actual nx_batch control flow with synthetic Worker/receipts and private SQLite."""
import copy
import sqlite3
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock,patch

from . import stage_mcp
from .test_geometry_gate_p11 import MockServer,policy,solid
from .batch_geometry import operation_capability,require_current_geometry

MOCK_RECEIPTS=[]
PART=str(Path(__file__).resolve().parents[1] / 'models/p12_synthetic/part.prt')

def inspection(**extra):
    return dict(solid(),part=PART,provenance='synthetic/mock P12',**extra)

def inspect_op():return {'command':'nx_inspect'}
def mutate_op(name='make'):return {'step':name,'command':'nx_sphere synthetic 0 0 0 5'}
def view_op():return {'step':'view','command':'nx_fit_view'}


class BatchTimelineTests(unittest.IsolatedAsyncioTestCase):
    async def execute_batch(self,ops,receipts=None,*,expectation=None,mutation_failure=False,
                            mutation_raises=False,identity_drift=False,session_missing=False,
                            view_changes_identity=False,followup_ops=None):
        tmp=tempfile.TemporaryDirectory(dir=stage_mcp.ROOT/'tmp');self.addCleanup(tmp.cleanup)
        folder=Path(tmp.name);db=sqlite3.connect(':memory:');self.addCleanup(db.close)
        db.executescript('CREATE TABLE operations(id,state,payload);CREATE TABLE task_meta(name,value);')
        items=iter(copy.deepcopy(receipts if receipts is not None else [inspection()]))
        calls=[];inspected=0;model=dict(pid=12,tag='1200',part=PART)
        def session():
            return dict(ok=True,nx='12.0.2.9',process_id=model['pid'],all_modified_flags_known=True,any_modified=False,
                        parts=[dict(path=model['part'],tag=model['tag'],is_modified=False)],provenance='synthetic/mock')
        async def observe(command):
            nonlocal inspected
            calls.append(dict(kind='observation',command=command))
            name=command.split()[0]
            if name=='nx_inspect':
                inspected+=1;return next(items)
            if name=='nx_session_parts':
                if session_missing:return {'ok':True,'parts':[]}
                value=session()
                if identity_drift and inspected:value['process_id']=99
                return value
            if name=='nx_status':return dict(ok=True,part=model['part'],nx='12.0.2.9',provenance='synthetic/mock')
            return dict(ok=True,provenance='synthetic/mock read')
        async def action(identity,command,**kw):
            calls.append(dict(kind='mutation_dispatch',command=command,identity=identity))
            if mutation_raises:raise RuntimeError('synthetic unknown dispatch result')
            if view_changes_identity and command=='nx_fit_view':model['tag']='other-part-tag'
            return dict(ok=not mutation_failure,provenance='synthetic/mock action')
        worker=SimpleNamespace(recover_receipts=Mock(),observe=AsyncMock(side_effect=observe),
                               execute=AsyncMock(side_effect=action),model_root=Path(PART).parent,
                               store=SimpleNamespace(db=db,switch_provider=Mock(),checkpoint=Mock(return_value={'unresolved':[]})))
        class Context:
            async def __aenter__(self):return worker
            async def __aexit__(self,*args):return False
        spec=dict(allowed_commands=['nx_fit_view','nx_sphere','nx_future_unknown'],
                  geometry_expectation=expectation if expectation is not None else policy())
        with patch.object(stage_mcp,'valid_stage',return_value=(folder,spec)),patch.object(stage_mcp,'FastMCP',MockServer),patch.object(stage_mcp,'Worker',return_value=Context()):
            server=stage_mcp.build_server(folder,'P12_mock')
            async with server.lifespan(server):
                with patch.object(stage_mcp,'inspect_geometry_valid',side_effect=RuntimeError('legacy bool must not decide batch acceptance')):
                    result=await server.tools['nx_batch'](ops)
                    if followup_ops is not None:
                        first=result;result=await server.tools['nx_batch'](followup_ops)
                        result['previous_batch_for_test_only']=first
        MOCK_RECEIPTS.append(dict(test=self.id(),provenance='synthetic/mock only',operations=ops,receipts=receipts,
                                 result=result,calls=calls))
        return result,worker

    async def test_inspect_mutation_is_stale(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()])
        self.assertTrue(r['ok']);self.assertFalse(r['geometry_pass'])
        self.assertTrue(r['inspection_stale']);self.assertEqual(r['geometry_status'],'STALE_INSPECTION')

    async def test_presentation_retains_inspection(self):
        r,w=await self.execute_batch([inspect_op(),view_op()])
        self.assertTrue(r['ok']);self.assertTrue(r['geometry_pass'])
        require_current_geometry(r)

    async def test_mutation_then_new_inspection(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op(),inspect_op()],[inspection(),inspection()])
        self.assertTrue(r['geometry_pass']);self.assertFalse(r['inspection_stale'])
        self.assertEqual(r['last_geometry_mutation_index'],2)
        self.assertEqual(r['last_successful_inspection_index'],3)
        self.assertEqual(r['inspections'][-1]['binding']['last_geometry_mutation_index'],2)
        self.assertFalse(r['inspections'][0]['valid_for_current_state'])

    async def test_second_inspection_failure_stops_next_mutation(self):
        bad=inspection();bad['bodies']=[]
        r,w=await self.execute_batch([inspect_op(),mutate_op(),inspect_op(),mutate_op('after')],[inspection(),bad])
        self.assertFalse(r['ok']);self.assertFalse(r['geometry_pass']);w.execute.assert_awaited_once()

    async def test_readonly_after_mutation_cannot_restore(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op(),{'command':'nx_list_bodies'}])
        self.assertTrue(r['operation_ok']);self.assertFalse(r['geometry_pass']);self.assertTrue(r['geometry_dirty'])

    async def test_two_mutations_latest_sequence_covered(self):
        r,w=await self.execute_batch([mutate_op('one'),mutate_op('two'),inspect_op()])
        self.assertTrue(r['geometry_pass']);self.assertEqual(r['last_geometry_mutation_index'],2)
        self.assertEqual(r['inspections'][-1]['binding']['operation_sequence'],3)
        self.assertEqual(len(r['inspection_receipt_ids']),1)

    async def test_unknown_command_never_reuses_pass(self):
        r,w=await self.execute_batch([inspect_op(),{'step':'future','command':'nx_future_unknown'}])
        self.assertFalse(r['geometry_pass']);self.assertTrue(r['geometry_dirty'])

    async def test_empty_then_mutation_not_geometry_pass(self):
        p=policy();p.update(min_bodies=0,max_bodies=1,allow_empty_geometry=True,empty_geometry_reason='synthetic planning')
        empty=inspection();empty['bodies']=[]
        r,w=await self.execute_batch([inspect_op(),mutate_op()],[empty],expectation=p)
        self.assertTrue(r['ok']);self.assertFalse(r['geometry_pass']);self.assertEqual(r['geometry_status'],'STALE_INSPECTION')

    async def test_wrong_part_receipt_rejected(self):
        bad=inspection();bad['part']=str(Path(__file__).resolve().parents[1] / 'models/other.prt')
        r,w=await self.execute_batch([inspect_op(),mutate_op()],[bad])
        self.assertFalse(r['geometry_pass']);self.assertTrue(r['inspection_stale']);w.execute.assert_not_awaited()

    async def test_wrong_process_receipt_rejected(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()],[inspection(process_id=99)])
        self.assertFalse(r['geometry_pass']);w.execute.assert_not_awaited()

    async def test_wrong_part_tag_receipt_rejected(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()],[inspection(part_tag='wrong')])
        self.assertFalse(r['geometry_pass']);w.execute.assert_not_awaited()

    async def test_session_drift_rejected(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()],identity_drift=True)
        self.assertFalse(r['geometry_pass']);self.assertTrue(r['inspection_stale']);w.execute.assert_not_awaited()

    async def test_missing_session_identity_not_pass(self):
        r,w=await self.execute_batch([inspect_op()],session_missing=True)
        self.assertFalse(r['geometry_pass']);self.assertEqual(r['geometry_status'],'STALE_INSPECTION')

    async def test_failed_mutation_invalidates_before_dispatch(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()],mutation_failure=True)
        self.assertFalse(r['ok']);self.assertFalse(r['operation_ok']);self.assertFalse(r['geometry_pass'])
        self.assertTrue(r['inspection_stale'])

    async def test_unknown_mutation_exception_invalidates(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()],mutation_raises=True)
        self.assertFalse(r['operation_ok']);self.assertFalse(r['geometry_pass']);self.assertTrue(r['inspection_stale'])

    async def test_mutation_only_no_inspection(self):
        r,w=await self.execute_batch([mutate_op()])
        self.assertTrue(r['ok']);self.assertTrue(r['operation_ok']);self.assertFalse(r['geometry_pass'])
        self.assertIsNone(r['last_successful_inspection_index']);self.assertFalse(r['inspection_stale'])

    async def test_stale_operation_receipt_rejected(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()],[inspection(operation_sequence=400)])
        self.assertEqual(r['geometry_status'],'STALE_INSPECTION');w.execute.assert_not_awaited()

    async def test_wrong_server_session_receipt_rejected(self):
        r,w=await self.execute_batch([inspect_op(),mutate_op()],[inspection(stage_session_id='previous-server')])
        self.assertFalse(r['geometry_pass']);w.execute.assert_not_awaited()

    async def test_identity_changed_during_presentation_is_stale(self):
        r,w=await self.execute_batch([inspect_op(),view_op()],view_changes_identity=True)
        self.assertTrue(r['operation_ok']);self.assertFalse(r['geometry_pass']);self.assertTrue(r['inspection_stale'])

    async def test_unknown_read_transport_command_invalidates(self):
        # Worker READ_ONLY is transport permission, not a proven no-side-effect contract.
        r,w=await self.execute_batch([inspect_op(),{'command':'nx_surface_state'}])
        self.assertTrue(r['ok']);self.assertFalse(r['geometry_pass']);self.assertTrue(r['inspection_stale'])
        w.execute.assert_not_awaited()

    async def test_unknown_rejected_command_invalidates(self):
        r,w=await self.execute_batch([inspect_op(),{'step':'unknown','command':'not_registered'}])
        self.assertFalse(r['ok']);self.assertFalse(r['geometry_pass']);self.assertTrue(r['inspection_stale'])
        w.execute.assert_not_awaited()

    async def test_no_inspection_reuse_across_batches(self):
        r,w=await self.execute_batch([inspect_op()],followup_ops=[view_op()])
        self.assertTrue(r['previous_batch_for_test_only']['geometry_pass'])
        self.assertTrue(r['operation_ok']);self.assertFalse(r['geometry_pass'])
        self.assertNotEqual(r['batch_id'],r['previous_batch_for_test_only']['batch_id'])
        self.assertEqual(r['operation_sequence'],2)

    async def test_stage_mutation_sequence_retained_across_batches(self):
        r,w=await self.execute_batch([mutate_op()],followup_ops=[inspect_op()])
        self.assertEqual(r['last_geometry_mutation_index'],1)
        self.assertEqual(r['last_successful_inspection_index'],2)
        require_current_geometry(r)

    async def test_latest_empty_does_not_inherit_previous_pass(self):
        p=policy();p.update(min_bodies=0,max_bodies=1,allow_empty_geometry=True,empty_geometry_reason='synthetic')
        empty=inspection();empty['bodies']=[]
        r,w=await self.execute_batch([inspect_op(),inspect_op()],[inspection(),empty],expectation=p)
        self.assertTrue(r['ok']);self.assertFalse(r['geometry_pass'])
        self.assertEqual(r['geometry_status'],'EMPTY_STAGE_ALLOWED')
        self.assertFalse(r['inspections'][0]['valid_for_current_state'])

    async def test_consumer_rejects_ok_only_stale_and_tampered_binding(self):
        for r in ({'ok':True},{'ok':True,'geometry_pass':True}):
            with self.assertRaises(ValueError):require_current_geometry(r)
        stale,w=await self.execute_batch([inspect_op(),mutate_op()])
        with self.assertRaises(ValueError):require_current_geometry(stale)
        fresh,w=await self.execute_batch([mutate_op(),inspect_op()])
        require_current_geometry(fresh)
        for field,value in [('last_geometry_mutation_index',2),('current_model_identity',{}),('operation_sequence',1)]:
            bad=copy.deepcopy(fresh);bad[field]=value
            with self.assertRaises(ValueError):require_current_geometry(bad)


class CapabilityTests(unittest.TestCase):
    def test_explicit_capabilities_not_name_heuristics(self):
        self.assertEqual(operation_capability('nx_fit_view'),'presentation_only')
        self.assertEqual(operation_capability('nx_color'),'presentation_only')
        self.assertEqual(operation_capability('nx_inspect'),'non_geometry_observation')
        self.assertEqual(operation_capability('nx_open_part'),'geometry_mutation')
        self.assertEqual(operation_capability('nx_create_imaginary'),'unknown')
        self.assertEqual(operation_capability('nx_surface_state'),'unknown')
        self.assertEqual(operation_capability('nx_save_part'),'unknown')
        self.assertEqual(operation_capability('nx_interference'),'unknown')
        self.assertEqual(operation_capability('nx_assembly_state'),'unknown')

if __name__=='__main__':unittest.main()
