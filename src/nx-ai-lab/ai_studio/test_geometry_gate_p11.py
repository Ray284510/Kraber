"""Synthetic receipt/worker fixtures; NO native NX or MCP transport execution."""
import copy
import sqlite3
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

from .stage_geometry import inspect_geometry
from . import stage_mcp

MOCK_RECEIPTS=[]

def policy():
    return dict(min_bodies=1,max_bodies=1,expected_body_kinds=['solid'],
                required_receipt_fields={'result':['ok','bodies','features','failed_feature_count'],
                                         'body':['solid','volume_mm3','consistency_fault_count','faces'],
                                         'feature':['errors','warnings']})

def solid():
    return dict(ok=True,failed_feature_count=0,features=[dict(errors=[],warnings=[])],
                bodies=[dict(solid=True,faces=6,consistency_fault_count=0,volume_mm3=10.0)])

def cases():
    out=[]
    def add(name,fn):
        v=solid();fn(v);out.append((name,v))
    for name,val in [('none',None),('list',[]),('string','receipt')]:out.append(('result_'+name,val))
    add('missing_bodies',lambda v:v.pop('bodies'))
    for name,val in [('empty',[]),('none',None),('dict',{}),('string','body'),('notdict',[1]),('too_many',[solid()['bodies'][0]]*2)]:
        add('bodies_'+name,lambda v,x=val:v.update(bodies=x))
    for name,val in [('bool_true',True),('bool_false',False),('nan',float('nan')),('inf',float('inf')),
                     ('negative',-1),('zero',0),('string','1'),('none',None)]:
        add('volume_'+name,lambda v,x=val:v['bodies'][0].update(volume_mm3=x))
    for name,val in [('bool',True),('float',0.0),('string','0'),('nonzero',1),('none',None)]:
        add('fault_'+name,lambda v,x=val:v['bodies'][0].update(consistency_fault_count=x))
        add('feature_count_'+name,lambda v,x=val:v.update(failed_feature_count=x))
    add('missing_fault',lambda v:v['bodies'][0].pop('consistency_fault_count'))
    add('missing_feature_count',lambda v:v.pop('failed_feature_count'))
    add('missing_features',lambda v:v.pop('features'))
    add('missing_faces',lambda v:v['bodies'][0].pop('faces'))
    add('faces_bool',lambda v:v['bodies'][0].update(faces=True))
    add('faces_zero',lambda v:v['bodies'][0].update(faces=0))
    add('unknown_kind',lambda v:v['bodies'][0].update(kind='mesh'))
    add('solid_not_bool',lambda v:v['bodies'][0].update(solid=1))
    add('ok_not_bool',lambda v:v.update(ok=1))
    for name,val in [('dict',{}),('none',None),('unknown_item',[None]),('fields_missing',[{}]),
                     ('errors',[dict(errors=['E'],warnings=[])]),('warnings',[dict(errors=[],warnings=['W'])]),
                     ('errors_bool',[dict(errors=False,warnings=[])])]:
        add('features_'+name,lambda v,x=val:v.update(features=x))
    return out

class GeometryGateP11(unittest.TestCase):
    def test_valid_nonempty_solid(self):
        g=inspect_geometry(solid(),policy(),stage_id='mock')
        self.assertTrue(g['geometry_pass']);self.assertEqual(g['status'],'GEOMETRY_RECEIPT_PASS')
    def test_missing_stage_policy(self):
        self.assertFalse(inspect_geometry(solid(),None,stage_id='mock')['geometry_pass'])
    def test_additional_required_fields(self):
        p=policy();p['required_receipt_fields']['result'].append('part')
        self.assertFalse(inspect_geometry(solid(),p,stage_id='mock')['geometry_pass'])
    def test_sheet_explicit_permission(self):
        r=solid();r['bodies'][0].update(solid=False,volume_mm3=None,volume_applicable=False,faces=1)
        p=policy();p['expected_body_kinds']=['sheet']
        self.assertFalse(inspect_geometry(r,p,stage_id='mock')['geometry_pass'])
        p['allow_sheet_geometry']=True
        self.assertTrue(inspect_geometry(r,p,stage_id='mock')['geometry_pass'])
        for key,val in [('faces',True),('volume_mm3',0),('volume_applicable',0)]:
            bad=copy.deepcopy(r);bad['bodies'][0][key]=val
            self.assertFalse(inspect_geometry(bad,p,stage_id='mock')['geometry_pass'])
        bad=copy.deepcopy(r);bad['bodies'][0].pop('volume_mm3')
        self.assertFalse(inspect_geometry(bad,p,stage_id='mock')['geometry_pass'])
    def test_explicit_empty_distinct_from_pass(self):
        p=policy();p.update(min_bodies=0,max_bodies=0,allow_empty_geometry=True,empty_geometry_reason='design-only mock stage')
        r=solid();r['bodies']=[]
        g=inspect_geometry(r,p,stage_id='empty-stage')
        self.assertEqual(g['status'],'EMPTY_STAGE_ALLOWED');self.assertFalse(g['geometry_pass'])
        self.assertEqual(g['stage_id'],'empty-stage');self.assertEqual(g['reason'],p['empty_geometry_reason'])
        for field in ('allow_empty_geometry','empty_geometry_reason'):
            bad=p.copy();bad.pop(field)
            self.assertEqual(inspect_geometry(r,bad,stage_id='empty-stage')['status'],'GEOMETRY_REJECTED')
        self.assertEqual(inspect_geometry(r,p,stage_id='')['status'],'GEOMETRY_REJECTED')
    def test_body_count_too_few(self):
        p=policy();p.update(min_bodies=2,max_bodies=3)
        self.assertFalse(inspect_geometry(solid(),p,stage_id='mock')['geometry_pass'])

for _name,_value in cases():
    def case(self,value=_value):
        g=inspect_geometry(value,policy(),stage_id='mock')
        self.assertFalse(g['geometry_pass']);self.assertEqual(g['status'],'GEOMETRY_REJECTED')
        self.assertTrue(g['reason'])
        # The compatibility helper has no stage-specific maximum. Batch uses
        # the explicit policy above, which rejects two bodies for this stage.
        if not (isinstance(value,dict) and isinstance(value.get('bodies'),list) and len(value['bodies'])==2):
            self.assertFalse(stage_mcp.inspect_geometry_valid(value))
        MOCK_RECEIPTS.append(dict(test=self.id(),provenance='synthetic/mock',input=value,gate=g))
    setattr(GeometryGateP11,'test_reject_'+_name,case)


class MockServer:
    """Registration-only fake. No FastMCP server/lifecycle/transport is started."""
    def __init__(self,*args,lifespan,**kwargs):self.lifespan=lifespan;self.tools={}
    def tool(self,**kwargs):
        def register(fn):self.tools[fn.__name__]=fn;return fn
        return register


class BatchGateP11(unittest.IsolatedAsyncioTestCase):
    async def run_batch(self,receipts,expectation):
        tmp=tempfile.TemporaryDirectory(dir=stage_mcp.ROOT/'tmp');self.addCleanup(tmp.cleanup)
        folder=Path(tmp.name);db=sqlite3.connect(':memory:');self.addCleanup(db.close)
        db.executescript('CREATE TABLE operations(id,state,payload); CREATE TABLE task_meta(name,value);')
        observed=[];iterator=iter(receipts)
        async def observe(command):
            observed.append(command)
            part=str(Path(__file__).resolve().parents[1] / 'models/p11_synthetic/part.prt')
            if command.split()[0]=='nx_inspect':
                value=next(iterator)
                return dict(value,part=part) if isinstance(value,dict) else value
            if command=='nx_status':return dict(ok=True,part=part,nx='12.0.2.9')
            return dict(ok=True,nx='12.0.2.9',all_modified_flags_known=True,any_modified=False,process_id=11,
                        parts=[dict(path=part,tag='1100',is_modified=False)])
        worker=SimpleNamespace(recover_receipts=Mock(),observe=AsyncMock(side_effect=observe),
                               execute=AsyncMock(return_value={'ok':True,'provenance':'synthetic worker mutation receipt'}),
                               store=SimpleNamespace(db=db,switch_provider=Mock()))
        context=SimpleNamespace()
        class WorkerContext:
            async def __aenter__(self):return worker
            async def __aexit__(self,*args):return False
        spec=dict(allowed_commands=['nx_fit_view'],geometry_expectation=expectation)
        with patch.object(stage_mcp,'valid_stage',return_value=(folder,spec)),patch.object(stage_mcp,'FastMCP',MockServer),patch.object(stage_mcp,'Worker',return_value=WorkerContext()):
            server=stage_mcp.build_server(folder,'mock-stage')
            async with server.lifespan(server):
                ops=[{'command':'nx_inspect ' } for _ in receipts]+[{'step':'after','command':'nx_fit_view'}]
                result=await server.tools['nx_batch'](ops)
        MOCK_RECEIPTS.append(dict(test=self.id(),provenance='synthetic/mock, actual nx_batch function',
                                 input=receipts,result=result,mutation_calls=worker.execute.await_count))
        return result,worker,observed
    async def test_missing_bodies_stops_next_mutation(self):
        r=solid();r.pop('bodies');result,w,obs=await self.run_batch([r],policy())
        self.assertFalse(result['ok']);self.assertFalse(result['geometry_pass']);w.execute.assert_not_awaited()
        self.assertEqual(obs,['nx_status','nx_session_parts','nx_inspect '])
    async def test_empty_bodies_stops_next_mutation(self):
        r=solid();r['bodies']=[];result,w,obs=await self.run_batch([r],policy())
        self.assertFalse(result['ok']);w.execute.assert_not_awaited()
    async def test_non_dict_receipt_stops_next_mutation(self):
        result,w,obs=await self.run_batch([None],policy())
        self.assertFalse(result['ok']);w.execute.assert_not_awaited()
    async def test_every_inspect_checked(self):
        bad=solid();bad['bodies']=[]
        result,w,obs=await self.run_batch([solid(),bad],policy())
        self.assertFalse(result['ok']);self.assertEqual(len(result['inspections']),2);w.execute.assert_not_awaited()
    async def test_valid_nonempty_allows_later_flow(self):
        result,w,obs=await self.run_batch([solid()],policy())
        self.assertTrue(result['ok']);self.assertTrue(result['geometry_pass']);w.execute.assert_awaited_once()
    async def test_allowed_empty_is_not_geometry_pass(self):
        p=policy();p.update(min_bodies=0,max_bodies=0,allow_empty_geometry=True,empty_geometry_reason='mock design stage')
        r=solid();r['bodies']=[];result,w,obs=await self.run_batch([r],p)
        self.assertTrue(result['ok']);self.assertFalse(result['geometry_pass']);self.assertFalse(result['geometry_gate'])
        self.assertEqual(result['inspections'][0]['status'],'EMPTY_STAGE_ALLOWED')
        w.execute.assert_awaited_once()
    async def test_missing_explicit_policy_stops(self):
        result,w,obs=await self.run_batch([solid()],None)
        self.assertFalse(result['ok']);w.execute.assert_not_awaited()

if __name__=='__main__':unittest.main()
