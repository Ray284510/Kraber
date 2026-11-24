"""Real NX acceptance for scoped serial batches and actual body-ID references."""
import argparse
import asyncio
import json
import math
import os
import hashlib
from pathlib import Path

from mcp import ClientSession,StdioServerParameters
from mcp.client.stdio import stdio_client
from .diagnostics.status_only import FileAttemptStore
from .stage_mcp import ROOT
from . import p13_smoke_command as smoke
from .batch_geometry import require_current_geometry
from .batch_harness_contract import (NEW_BLANK, fresh_target, require_empty_session, smoke_part_tag,require_probe_session,require_temporal_evidence,
    smoke_operations,operation_by_step,smoke_inspection_checks,require_smoke_inspection,
    require_smoke_execution_policy, require_duplicate_observation, SMOKE_LABELS)


async def verify(folder,*,confirmed_new_blank=False,preflight_receipt=None, approval=None):
    # No native connection until a later, explicitly authorized continuation has
    # supplied an actual one-shot host/MCP preflight, not a prepared template.
    if not confirmed_new_blank or preflight_receipt is None:
        raise ValueError('Explicit new blank Smoke scope and a fresh native preflight receipt required')
    spec=json.loads((folder/'task_spec.json').read_text())
    require_smoke_execution_policy(spec)
    part=fresh_target(folder,spec,ROOT)
    models=part.parent
    if models != smoke.SMOKE_ROOT.resolve():
        raise ValueError('Fixed Smoke requires the prepared disposable model root')
    receipt_path=Path(preflight_receipt).resolve()
    if not receipt_path.is_relative_to(folder.resolve()):raise ValueError('Preflight receipt must be in this task')
    preflight_raw=receipt_path.read_bytes()
    preflight=json.loads(preflight_raw)
    if (preflight.get('task_uuid')!=spec['task_uuid'] or preflight.get('provenance')!='native_host_MCP_preflight'
            or preflight.get('all_preconditions_passed') is not True):
        raise ValueError('No successful native preflight for this task')
    initial_identity=require_empty_session(preflight['status'],preflight['session'])
    if initial_identity['process_id']!=spec['expected_server_pid']:raise ValueError('Preflight PID does not match strict peer binding')
    if (folder/'task.sqlite').exists():raise ValueError('One-shot harness requires a never-started task database')
    name='batch-validation'
    stage={'id':name,'allowed_commands':['nx_create_part','nx_shape','nx_hole','nx_save_part','nx_close_part','nx_open_part','nx_fit_view'],
           'geometry_expectation':{'min_bodies':1,'max_bodies':1,'expected_body_kinds':['solid'],
                'required_receipt_fields':{'result':['ok','bodies','features','failed_feature_count'],
                    'body':['solid','volume_mm3','consistency_fault_count','faces'],'feature':['errors','warnings']}},
           'smoke_inspection_checks':smoke_inspection_checks(),'initial_part_contract':NEW_BLANK,
           'output_tokens':2048,'max_generations':4,'goal':'Native batch broker acceptance; Codex, no paid API.'}
    if json.loads((folder/'stages'/(name+'.json')).read_text())!=stage:
        raise ValueError('Prepared stage spec mismatch; do not overwrite it')
    store = FileAttemptStore()
    run_value = smoke.admit_harness(folder, spec, preflight_raw, store, approval)
    try:
        await _run_admitted(folder, spec, stage, name, part, initial_identity, run_value, store.synthetic)
    except BaseException as primary:
        import traceback
        failure = {'scope':smoke.APPROVAL,'attempt_uuid':spec['task_uuid'],'outcome':'STOPPED',
                   'replay_allowed':False,'error_type':type(primary).__name__,'error':str(primary),
                   'traceback':''.join(traceback.format_exception(primary))}
        try: store.finish(spec['task_uuid'],failure)
        except Exception as cleanup: primary.add_note('Terminal evidence write failed: '+repr(cleanup))
        raise
    # If this exclusive write fails, retain the consumed marker and stop. A
    # saved acceptance measurement alone cannot certify a complete audit.
    store.finish(spec['task_uuid'], {'scope':smoke.APPROVAL,'attempt_uuid':spec['task_uuid'],'outcome':'HARNESS_COMPLETED',
                                     'acceptance_sha256':hashlib.sha256((folder/'batch-native-acceptance.json').read_bytes()).hexdigest()})
    print(json.dumps({'passed':True,'evidence':str(folder/'batch-native-acceptance.json')}))


async def _run_admitted(folder, spec, stage, name, part, initial_identity, run_value, synthetic):
    params=StdioServerParameters(command=str(ROOT/'.venv/Scripts/python.exe'),cwd=str(ROOT),
        args=['-m','ai_studio.stage_mcp','--task',str(folder),'--stage',name,'--provider','codex'],
        env=dict(os.environ,**{smoke.RUN_ENV:run_value},NX_AI_LAB_ROOT=str(ROOT),TEMP=str(ROOT/'tmp'),TMP=str(ROOT/'tmp')))
    events=[]
    async with stdio_client(params) as (r,w):
        async with ClientSession(r,w) as s:
            await s.initialize()
            async def call(tool,args,expect_error=False):
                raw=await s.call_tool(tool,args);text=next(c.text for c in raw.content if c.type=='text')
                events.append({'tool':tool,'arguments':args,'is_error':bool(raw.isError),'text':text})
                (folder/'batch-native-calls.json').write_text(json.dumps(events,ensure_ascii=False,indent=2),encoding='utf-8')
                if raw.isError is not expect_error:
                    raise ValueError(f'Unexpected MCP error status for {tool}')
                return text if expect_error else json.loads(text)
            initial=await call('nx_stage_state',{})
            status=await call('nx_observe',{'command':'nx_status'})
            locked_identity=require_empty_session(status,initial['nx_session'])
            if locked_identity!=initial_identity:raise ValueError('Empty session changed since preflight')
            if any(o['state'] in ('running','unknown') for o in initial['operations']):raise ValueError('Unknown mutation; stop')
            ops=smoke_operations(part)
            result=await call('nx_batch',{'operations':ops})
            if result.get('operation_ok') is not True:
                rows=result.get('results') or [{}]
                last=rows[-1]
                reason=last.get('message') or last.get('inspection',{}).get('receipt_gate',{}).get('reason')
                raise ValueError(f"Smoke stopped at {last.get('step')}: {reason or 'see batch-native-calls.json'}")
            require_current_geometry(result)
            freshness=require_temporal_evidence(result,labels=SMOKE_LABELS,final_part=part,expected_identity=locked_identity)
            (folder/'batch-freshness-evidence.json').write_text(json.dumps(freshness,ensure_ascii=False,indent=2),encoding='utf-8')
            solid = operation_by_step(result['results'], 'solid')['actual']
            hole = operation_by_step(result['results'], 'hole')['actual']
            if (solid.get('receipt_kind') != 'fresh_dispatch' or hole.get('receipt_kind') != 'fresh_dispatch'
                    or not solid.get('body_id') or hole.get('body_id') != solid['body_id']
                    or not solid.get('body_tag') or hole.get('body_tag') != solid['body_tag']
                    or solid.get('owning_part_tag') != hole.get('owning_part_tag') or hole.get('owning_part_tag') != hole['handle_binding']['model_identity']['part_tag']):
                raise ValueError('Missing native hole target/body ownership evidence')
            actual=operation_by_step(result['results'],'I3')['actual'];require_smoke_inspection(actual,1280-16*math.pi)
            # A reference from a previous batch is rejected before native dispatch.
            rejected=await call('nx_batch',{'operations':[{'step':'bad_reference','command':'nx_hole $solid.body_id 0 0 2 4 0'}]})
            if rejected.get('operation_ok') is not False or rejected.get('geometry_pass') is not False:
                raise ValueError('Out-of-batch reference must fail closed')
            rejected_row = operation_by_step(rejected['results'], 'bad_reference')
            if (len(rejected['results']) != 1 or 'actual' in rejected_row
                    or rejected_row.get('error') != 'ValueError'
                    or rejected_row.get('message') != 'Reference must follow a successful step in this batch and current part'):
                raise ValueError('Missing pre-dispatch cross-batch rejection evidence')
            duplicate=await call('nx_batch',{'operations':[operation_by_step(ops,'create'),operation_by_step(ops,'solid')]})
            if duplicate.get('operation_ok') is not True or duplicate.get('geometry_pass') is not False:
                raise ValueError('Replay execution without inspection is not geometry acceptance')
            for step in ('create', 'solid'):
                cached = operation_by_step(duplicate['results'], step)['actual']
                if (cached.get('receipt_kind') != 'cached_historical'
                        or 'body_id' in cached or 'handle_binding' in cached):
                    raise ValueError('Duplicate operation lacks a historical-only receipt')
            after=await call('nx_observe',{'command':'nx_inspect'})
            require_duplicate_observation(actual, after, stage['geometry_expectation'], part)
            final_status=await call('nx_observe',{'command':'nx_status'})
            final_session=await call('nx_observe',{'command':'nx_session_parts'})
            saved_identity=require_probe_session(final_status,final_session,part)
            if (saved_identity['process_id'] != initial_identity['process_id']
                    or saved_identity['tag'] != smoke_part_tag(final_status,part)
                    or saved_identity['tag'] != smoke_part_tag(actual,part)
                    or saved_identity['tag'] != smoke_part_tag(after,part)):
                raise ValueError('Saved model identity changed after I3')
            checkpoint=await call('stage_checkpoint',{'summary':'Fresh blank I0 verified; saved model reopened, I3 and duplicate observation checked.'})
            if (checkpoint.get('ok') is not True or checkpoint.get('geometry_pass') is not False
                    or checkpoint.get('geometry_gate') is not False or checkpoint.get('any_modified') is not False
                    or checkpoint.get('unresolved_count') != 0 or checkpoint.get('nx_process_id') != locked_identity['process_id']):
                raise ValueError('Invalid saved model checkpoint')
            checkpoint_file = folder/'stages'/(name+'.complete.json')
            if (Path(checkpoint.get('saved','')).resolve() != checkpoint_file.resolve()
                    or hashlib.sha256(checkpoint_file.read_bytes()).hexdigest() != checkpoint.get('sha256')):
                raise ValueError('Checkpoint file does not match the returned receipt')
            if not part.is_file() or part.stat().st_size == 0:
                raise ValueError('Saved final model missing or empty')
    evidence={'passed':True,'provenance':'synthetic/mock' if synthetic else 'native MCP / NX Worker',
              'provider':'codex','api_requests':0,'actual_prt':str(part),'reopened':actual,
              'model_bytes':part.stat().st_size,'model_sha256':hashlib.sha256(part.read_bytes()).hexdigest(),
              'initial_part_contract':NEW_BLANK,'initial_part':str(part),'i0_geometry_pass':freshness['first_inspection']['geometry_pass'],
              'i0_tag':freshness['first_inspection']['inspection_binding']['model_identity']['part_tag'],
              'i3_tag':saved_identity['tag'],'solid_body_tag':solid['body_tag'],'hole_body_tag':hole['body_tag'],'checkpoint_sha256':checkpoint['sha256'],
              'repeat_geometry_unchanged':True,'out_of_batch_reference_rejected':True,
              'scope':'Native batch broker, not paid DSH modeling or complex surface acceptance'}
    with (folder/'batch-native-acceptance.json').open('x',encoding='utf-8') as stream:
        json.dump(evidence,stream,ensure_ascii=False,indent=2)
        stream.flush()
        os.fsync(stream.fileno())



if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('folder',type=Path)
    p.add_argument('--confirmed-new-blank',action='store_true');p.add_argument('--preflight-receipt',type=Path)
    p.add_argument('--ticket-sha256',required=True);p.add_argument('--message-sha256',required=True)
    p.add_argument('--authorization-reference',required=True)
    a=p.parse_args();asyncio.run(verify(a.folder,confirmed_new_blank=a.confirmed_new_blank,preflight_receipt=a.preflight_receipt,approval=(a.ticket_sha256,a.message_sha256,a.authorization_reference)))
