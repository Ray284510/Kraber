"""DSH's only NX surface: stage-scoped commands with durable operation identities."""
import argparse
import asyncio
import copy
import hashlib
import json
import re
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

from mcp.server.fastmcp import FastMCP
from mcp.types import ToolAnnotations

from .nx_worker import Worker,ROOT,READ_ONLY
from .research_context import read_resource
from .transport_outcome import uncertain
from .stage_geometry import inspect_geometry, compatibility_expectation
from .batch_geometry import BatchGeometry, model_identity, operation_capability
from .batch_harness_contract import NEW_BLANK, require_blank_origin, require_smoke_inspection, smoke_part_tag

SAFE_ACTIONS={'nx_create_part','nx_open_part','nx_save_part','nx_close_part','nx_shape','nx_sphere',
              'nx_hole','nx_blend_strict','nx_fit_view','nx_add_component','nx_constraint',
              'nx_constraint_value','nx_constraint_suppress','nx_export_step','nx_import_step','nx_color'}
SAFE_ACTIONS.update({'nx_surface_edit_ellipse','nx_surface_edit_arc','nx_surface_loft','nx_surface_shell','nx_surface_sew','nx_surface_thicken',
                     'nx_surface_sweep','nx_surface_sweep_fixture',
                     'nx_surface_trim','nx_surface_extend','nx_surface_fixture'})
PATH_POSITIONS={'nx_create_part':(1,),'nx_open_part':(1,),'nx_add_component':(1,),
                'nx_export_step':(1,),'nx_import_step':(1,2)}
PATH_POSITIONS['nx_surface_loft']=(2,)


def checkpoint_receipt(path):
    """Keep complete durable evidence on disk, not in an oversized tool event."""
    raw = path.read_bytes()
    result = json.loads(raw)
    session = result['nx_session']
    return {'ok': True, 'geometry_pass': False, 'geometry_gate': False,
            'geometry_status': 'NOT_EVALUATED_CHECKPOINT_ONLY',
            'stage': result['stage'], 'saved': str(path),
            'sha256': hashlib.sha256(raw).hexdigest(),
            'operation_count': len(result['operations']),
            'unresolved_count': len(result['unresolved']),
            'nx_process_id': session.get('process_id'),
            'any_modified': session.get('any_modified'),
            'scope': 'Durable checkpoint receipt; full native state and operation history are in saved file. Not acceptance.'}


def inspect_geometry_valid(result, *, allow_sheets=False):
    """Compatibility bool: empty/unknown can never be a geometry pass."""
    return inspect_geometry(result, compatibility_expectation(allow_sheets),
                            stage_id='legacy_boolean_helper')['geometry_pass']


def valid_stage(folder,stage):
    folder=Path(folder).resolve()
    if not folder.is_relative_to(ROOT/'logs/phase5'):raise ValueError('Invalid task folder')
    if not re.fullmatch('[A-Za-z0-9_-]{1,80}',stage):raise ValueError('Invalid stage ID')
    spec=json.loads((folder/'stages'/(stage+'.json')).read_text(encoding='utf-8'))
    if set(spec['allowed_commands'])-SAFE_ACTIONS:raise ValueError('Unsupported commands')
    return folder,spec


def build_server(folder,stage,provider='openai_dsh'):
    folder,spec=valid_stage(folder,stage)
    final_path=folder/'stages'/(stage+'.complete.json')
    blank_smoke = stage == 'batch-validation' and spec.get('initial_part_contract') == NEW_BLANK
    lock=asyncio.Lock();state={'edge_snapshots':{},'operation_sequence':0,
                             'last_geometry_mutation_index':None,'stage_session_id':uuid.uuid4().hex}

    def next_sequence(capability):
        state['operation_sequence']+=1
        if capability in ('geometry_mutation','unknown'):
            state['last_geometry_mutation_index']=state['operation_sequence']
        return state['operation_sequence']

    async def inspection_context():
        # Adapter binding for the existing bridge protocol; no native revision
        # counter is available. It does not prove absence of external edits.
        w=state['worker']
        status=await w.observe('nx_status')
        session=await w.observe('nx_session_parts')
        identity = model_identity(status,session,state['stage_session_id'])
        if blank_smoke:
            target = w.model_root/'batch_validation.prt'
            if (not identity or len(session['parts']) != 1 or type(session['parts'][0].get('is_modified')) is not bool
                    or smoke_part_tag(status,target) != identity['part_tag']):
                raise ValueError('Single loaded Work/Display identity required during inspection')
        return {'status':status,'session':session,'identity':identity}

    @asynccontextmanager
    async def lifespan(server):
        async with Worker(folder,provider) as worker:
            worker.recover_receipts();state['worker']=worker
            yield state

    mcp=FastMCP('NX AI Studio checkpoint worker',lifespan=lifespan)
    read=ToolAnnotations(readOnlyHint=True,destructiveHint=False,openWorldHint=False)
    write=ToolAnnotations(readOnlyHint=False,destructiveHint=False,openWorldHint=False)

    @mcp.tool(annotations=read)
    async def research_read(resource:str,start_line:int=1,limit:int=120,needle:str='')->dict:
        """Read or search one exact manifest-listed project/NX12 documentation file.

        resource is a key from the supplied handoff manifest. needle returns line
        locations; use start_line to read surrounding documentation. No arbitrary paths.
        """
        return read_resource(folder,resource,start_line,limit,needle)

    @mcp.tool(annotations=read)
    async def research_read_batch(requests:list[dict])->dict:
        """Read/search 1..8 independent manifest resources in one call.

        Each item uses research_read arguments: resource, start_line, limit,
        needle. needle is a literal substring, NOT a regex or an OR expression.
        Same hash/path protections as research_read; total output capped at 60 KB.
        """
        if not 1 <= len(requests) <= 8:
            raise ValueError('1..8 bounded research reads required')
        results=[]
        for request in requests:
            if set(request)-{'resource','start_line','limit','needle'} or 'resource' not in request:
                raise ValueError('Only explicit research_read arguments are supported')
            results.append(read_resource(folder,**request))
            if len(json.dumps(results,ensure_ascii=False).encode()) > 60000:
                raise ValueError('Request fewer or shorter reads: 60 KB batch output limit')
        return {'results':results,'scope':'Actual hash-verified local research; not runtime acceptance'}

    @mcp.tool(annotations=read)
    async def nx_stage_state()->dict:
        """Read current stage, command limits, paths and actual NX state before action."""
        async with lock:
            w=state['worker']
            return {'stage':stage,'specification':spec,'models':str(w.model_root),
                    'completed':final_path.exists(),'nx_session':await w.observe('nx_session_parts'),
                    'operations':[dict(r) for r in w.store.db.execute('SELECT id,state,result FROM operations WHERE id LIKE ?',(stage+'.%',))]}

    @mcp.tool(annotations=read)
    async def nx_observe(command:str)->dict:
        """Read actual NX status, geometry, feature/assembly state or interference."""
        async with lock:
            next_sequence(operation_capability(command.split()[0] if command.split() else ''))
            result=await state['worker'].observe(command)
            if command.startswith('nx_list_edges ') and result.get('ok'):
                state['edge_snapshots'][command.split()[1]]=result
            return result

    @mcp.tool(annotations=write)
    async def nx_action(step:str,attempt:int,command:str,source_receipt:dict|None=None)->dict:
        """Execute one scoped NX command. attempt=0 first; 1..3 repairs only after failure.

        Reuse identical step/attempt to retrieve a completed receipt. Never change
        a successful step's arguments. For a registered body/sketch operand,
        supply its fresh source_receipt from this stage session.
        """
        async with lock:
            next_sequence(operation_capability(command.split()[0] if command.split() else ''))
            return await do_action(step,attempt,command,source_receipt=source_receipt)

    async def do_action(step,attempt,command,*,handle_context=None,required_handles=(),source_receipt=None):
        if final_path.exists():raise ValueError('Stage already checkpointed; no more mutations')
        if not re.fullmatch('[A-Za-z0-9_-]{1,80}',step) or not 0<=attempt<=3:
            raise ValueError('Invalid step or repair count')
        if len(command)>2048 or any(c in command for c in '\r\n\x00'):raise ValueError('One bounded command required')
        words=command.split()
        if not words or words[0] not in spec['allowed_commands']:raise ValueError('Command not permitted in this stage')
        w=state['worker']
        if words[0] in {'nx_surface_spline_fixture','nx_surface_edit_pole'}:
            if len(words)!=(2 if words[0]=='nx_surface_spline_fixture' else 6) or not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{0,49}',words[1]):
                raise ValueError('Invalid bounded spline command')
        if words[0] in {'nx_surface_edit_ellipse','nx_surface_edit_arc'}:
            if len(words)!=(7 if words[0]=='nx_surface_edit_ellipse' else 8) or not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{0,63}',words[1]):
                raise ValueError('Invalid native curve edit arguments')
        if words[0] in {'nx_surface_trim','nx_surface_extend','nx_surface_fixture'}:
            expected={'nx_surface_trim':12,'nx_surface_extend':9,'nx_surface_fixture':3}[words[0]]
            if len(words)!=expected or not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{0,63}',words[1]):
                raise ValueError('Invalid bounded surface command')
            if any('/' in item or '\\' in item or ':' in item for item in words[1:]):
                raise ValueError('Surface selectors cannot contain filesystem paths')
        if words[0] in {'nx_surface_sweep','nx_surface_sweep_fixture'}:
            fixture = words[0] == 'nx_surface_sweep_fixture'
            if len(words) != (4 if fixture else 6):
                raise ValueError('Invalid sweep argument count')
            names = words[1:2] if fixture else words[1:4]
            if any(not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{0,63}', item) for item in names):
                raise ValueError('Sweep arguments must be identifiers, not paths')
            if words[-2] not in {'solid','sheet'} or words[-1] not in {'forward','reverse'}:
                raise ValueError('Invalid sweep body type/direction')
        for pos in PATH_POSITIONS.get(words[0],()):
            path=Path(words[pos])
            if not path.is_absolute() or not path.resolve().is_relative_to(w.model_root):
                raise ValueError('Absolute path inside this task models required')
        if words[0]=='nx_close_part' and words!=['nx_close_part','0']:
            raise ValueError('Explicit saved-part close only')
        identity=f'{stage}.{step}.{attempt}'
        limit=spec.get('command_limits',{}).get(words[0])
        if limit is not None:
            if type(limit) is not int or not 0<=limit<=48:raise ValueError('Invalid per-command stage limit')
            existing=w.store.db.execute('SELECT 1 FROM operations WHERE id=?',(identity,)).fetchone()
            used=sum(1 for row in w.store.db.execute('SELECT payload FROM operations WHERE id LIKE ?',(stage+'.%',))
                     if json.loads(row[0]).get('command','').split()[0]==words[0])
            if not existing and used>=limit:raise ValueError('Per-command stage limit reached; no additional native attempt')
        cached=w.store.db.execute('SELECT state FROM operations WHERE id=?',(identity,)).fetchone()
        if words[0]=='nx_blend_strict' and not cached:
            edges=state['edge_snapshots'].get(words[1])
            if not edges:raise ValueError('Read current nx_list_edges for this body before selecting blend edges')
            actual=await w.observe('nx_list_edges '+words[1])
            def topology_fields(receipt):
                return {k:v for k,v in receipt.items() if k!='transport_receipt'}
            if not actual.get('ok') or topology_fields(actual)!=topology_fields(edges):
                raise ValueError('Edge topology changed; query and select current edges again')
            available={str(e['index']) for e in actual['edges']}
            if not set(words[3].split(','))<=available:raise ValueError('Selected edge index is not in current native query')
        if attempt:
            old=w.store.db.execute('SELECT state,result FROM operations WHERE id=?',(f'{stage}.{step}.{attempt-1}',)).fetchone()
            if not old or old['state']!='failed' or uncertain(json.loads(old['result'])):
                raise ValueError('Repair requires definitive prior failure evidence')
        session=await w.observe('nx_session_parts')
        if not session.get('all_modified_flags_known'):raise ValueError('NX modified state unknown')
        pid_row=w.store.db.execute("SELECT value FROM task_meta WHERE name='nx_process_id'").fetchone()
        if pid_row and pid_row[0]!=str(session['process_id']):
            raise ValueError('NX process changed; cold recovery and fresh bridge IDs must be verified before mutations')
        if not pid_row:
            with w.store.db:w.store.db.execute('INSERT INTO task_meta VALUES(?,?)',('nx_process_id',str(session['process_id'])))
        if words[0] in ('nx_close_part','nx_create_part','nx_open_part') and session.get('any_modified'):
            raise ValueError('Save before closing or switching parts; unsaved models are protected')
        w.store.switch_provider(provider,session)
        handle_context=dict(handle_context or {},stage_session_id=state['stage_session_id'],session=session)
        result=await w.execute(identity,command,allow_failure=True,handle_context=handle_context,
                               required_handles=required_handles,source_receipt=source_receipt)
        if not cached:state['edge_snapshots'].clear()
        return result

    @mcp.tool(annotations=write)
    async def nx_batch(operations:list[dict])->dict:
        """Run up to 48 planned steps sequentially; stop at the first actual failure.

        Each entry: {step: stable_name, attempt: 0..3, command: NX command}.
        For blends, query current nx_list_edges using the registered body_id from
        a mutation receipt. Listed display names may not be registered handles.
        Read-only commands may omit step/attempt. Use $step.body_id in a later
        command to reference an actual successful prior step in THIS stage.
        Every mutation uses the same durable identity/guards as nx_action.
        ok/operation_ok describes execution only. geometry_pass requires a fresh
        nx_inspect after the latest geometry/unknown operation, bound to current
        process, work-part and operation sequence. Display-only commands retain it.
        """
        async with lock:
            if not 1<=len(operations)<=48:raise ValueError('1..48 steps per finite batch')
            if final_path.exists():raise ValueError('Stage already complete')
            results=[];w=state['worker'];symbols={};generation=0
            timeline=BatchGeometry(uuid.uuid4().hex,state['last_geometry_mutation_index'])
            def freshness(operation_ok):
                # Observation only: copy the actual P12 state, never reconstruct
                # it in the harness. Later invalidation cannot rewrite snapshots.
                current=timeline.result(operation_ok)
                keys=('batch_id','operation_sequence','geometry_dirty','geometry_pass','geometry_gate',
                      'geometry_status','inspection_stale','last_geometry_mutation_index',
                      'last_successful_inspection_index','current_model_identity','inspection_receipt_ids')
                value={key:current[key] for key in keys}
                latest=timeline.inspections[-1] if timeline.inspections else None
                value.update(stage_session_id=state['stage_session_id'],
                             inspection_receipt_id=latest['inspection_receipt_id'] if latest else None,
                             inspection_binding=latest['binding'] if latest else None,
                             identity_scope='last_inspection_not_currently_verified' if timeline.geometry_dirty else 'verified_at_inspection',
                             provenance='P12 BatchGeometry state snapshot; native provenance belongs to actual Worker receipts')
                return copy.deepcopy(value)
            def finish(ok,**extra):
                return dict(timeline.result(ok),results=results,
                            provenance='Worker receipt validation only; not independent NX geometry acceptance',**extra)
            for entry in operations:
                command=entry.get('command','') if isinstance(entry,dict) else ''
                capability=operation_capability(command.split()[0] if isinstance(command,str) and command.split() else '')
                index=next_sequence(capability)
                # Invalidate BEFORE dispatch, including rejected/failed mutations.
                timeline.begin(index,capability)
                record={'step':entry.get('step') if isinstance(entry,dict) else None,'command':command,
                        'operation_sequence':index,'capability':capability,'freshness_before_dispatch':freshness(True)}
                required_handles=[]
                def substitute(match):
                    step,field=match.groups()
                    if step not in symbols:raise ValueError('Reference must follow a successful step in this batch and current part')
                    value=symbols[step].get(field)
                    if not isinstance(value,str) or not re.fullmatch('[A-Za-z0-9_-]+',value):
                        raise ValueError('Reference must be a bridge ID from a real receipt')
                    required_handles.append((value,symbols[step]['handle_binding']))
                    return value
                try:
                    command=re.sub(r'\$([A-Za-z0-9_-]+)\.(body_id|sketch_id)',substitute,command)
                    name=command.split()[0]
                    record['command']=command
                    if name in READ_ONLY:
                        before=await inspection_context() if name=='nx_inspect' else None
                        result=await w.observe(command)
                        if name=='nx_inspect':
                            expectation=spec.get('geometry_expectation')
                            blank_i0 = blank_smoke and entry.get('step') == 'I0'
                            if blank_i0 and isinstance(expectation,dict):
                                expectation=dict(expectation,min_bodies=0,max_bodies=0,allow_empty_geometry=True,
                                                 empty_geometry_reason='Fresh create: zero bodies at I0')
                            gate=inspect_geometry(result,expectation,stage_id=stage)
                            if blank_smoke:
                                record.update(actual=result,inspection_context_before=before)
                                try:
                                    target = w.model_root/'batch_validation.prt'
                                    if smoke_part_tag(result,target) != before['identity']['part_tag']:
                                        raise ValueError('Inspected part Tag changed')
                                    origin_step = 'reopen' if entry.get('step') == 'I3' else 'create'
                                    origins = [row['actual'] for row in results if row.get('step') == origin_step and 'actual' in row]
                                    if (len(origins) != 1 or origins[0].get('receipt_kind') != 'fresh_dispatch'
                                            or smoke_part_tag(origins[0],target) != before['identity']['part_tag']):
                                        raise ValueError('No matching fresh create/reopen receipt for this inspection')
                                    if blank_i0:
                                        if len(results) != 1:
                                            raise ValueError('I0 must immediately follow create in the same batch')
                                        require_blank_origin(result,results[0],target)
                                except (ValueError,KeyError,TypeError) as exc:
                                    gate=dict(gate,geometry_pass=False,status='GEOMETRY_REJECTED',reason=str(exc))
                            checks=spec.get('smoke_inspection_checks')
                            if checks is not None and gate['geometry_pass']:
                                try:
                                    check=checks[entry.get('step')]
                                    require_smoke_inspection(result,check['volume_mm3'])
                                except (ValueError,KeyError,TypeError) as exc:
                                    gate=dict(gate,geometry_pass=False,status='GEOMETRY_REJECTED',reason=str(exc))
                            after=await inspection_context() if gate['status'] in ('GEOMETRY_RECEIPT_PASS','EMPTY_STAGE_ALLOWED') else None
                            receipt=timeline.inspect(gate,result,before,after,index)
                            record.update(inspection=receipt,inspection_context_before=before,inspection_context_after=after)
                            if receipt['status'] not in ('GEOMETRY_RECEIPT_PASS','EMPTY_STAGE_ALLOWED'):
                                results.append(dict(record,actual=result,freshness_after_operation=freshness(False)))
                                return finish(False,stopped_on_failure=True)
                        if name=='nx_list_edges' and isinstance(result,dict) and result.get('ok'):
                            state['edge_snapshots'][command.split()[1]]=result
                    else:
                        next_generation=index if capability in ('geometry_mutation','unknown') else generation
                        result=await do_action(entry['step'],entry.get('attempt',0),command,
                                    handle_context={'batch_id':timeline.batch_id,'generation':generation,
                                                    'next_generation':next_generation},required_handles=required_handles)
                        if next_generation!=generation:
                            symbols.clear()
                            generation=next_generation
                    record['actual']=result
                    if not isinstance(result,dict) or result.get('ok') is not True:
                        timeline.invalidate('operation_failed_or_malformed')
                        results.append(dict(record,freshness_after_operation=freshness(False)))
                        return finish(False,stopped_on_failure=True)
                    if entry.get('step'):
                        symbols.pop(entry['step'],None)
                        if result.get('receipt_kind')=='fresh_dispatch' and result.get('handle_binding'):
                            symbols[entry['step']]=result
                    if command=='nx_save_part':
                        w.store.checkpoint('Actual part save completed inside finite batch',[result['saved']])
                    results.append(dict(record,freshness_after_operation=freshness(True)))
                except Exception as exc:
                    timeline.invalidate('operation_exception_terminal_state_unverified')
                    results.append(dict(record,error=type(exc).__name__,message=str(exc),
                                        freshness_after_operation=freshness(False)))
                    return finish(False,stopped_on_failure=True)
            if timeline.inspections and timeline.inspections[-1]['valid_for_current_state']:
                try:timeline.check_current(await inspection_context())
                except Exception as exc:
                    timeline.invalidate('final_identity_query_failed')
                    return finish(False,identity_error=type(exc).__name__)
            return finish(True)

    @mcp.tool(annotations=write)
    async def design_artifact(name:str,design:dict)->dict:
        """Save agent-created design/parameters before modeling. This is NOT NX verification."""
        async with lock:
            if not re.fullmatch('[A-Za-z0-9_-]{1,60}',name):raise ValueError('Simple artifact name required')
            if final_path.exists():raise ValueError('Stage already complete')
            encoded=json.dumps(design,ensure_ascii=False,indent=2)
            if len(encoded.encode())>65536:raise ValueError('Split large design records')
            target=folder/'stages'/(stage+'.'+name+'.json')
            if target.exists():
                if target.read_text(encoding='utf-8')!=encoded:raise ValueError('Use a new artifact revision name')
            else:target.write_text(encoded,encoding='utf-8')
            return {'ok':True,'saved':str(target),'provenance':'Agent design, not native CAD evidence'}
    @mcp.tool(annotations=write)
    async def stage_checkpoint(summary:str)->dict:
        """Finish this stage only at a saved boundary; persist actual NX state and operations."""
        async with lock:
            if final_path.exists():return checkpoint_receipt(final_path)
            if len(summary)>4000:raise ValueError('Use a short stage summary')
            w=state['worker'];session=await w.observe('nx_session_parts')
            if not session.get('all_modified_flags_known') or session.get('any_modified'):
                raise ValueError('Cannot checkpoint unsaved/unknown NX state')
            result=w.store.checkpoint(summary,[p['path'] for p in session['parts']])
            if result['unresolved']:raise ValueError('Unresolved operation prevents checkpoint')
            result.update(stage=stage,nx_session=session,summary_provenance='Agent supplied; actual NX evidence separate')
            temp=final_path.with_suffix('.tmp');temp.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');temp.replace(final_path)
            return checkpoint_receipt(final_path)

    return mcp


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--task',required=True);parser.add_argument('--stage',required=True)
    parser.add_argument('--provider',choices=['codex','openai_dsh'],default='openai_dsh')
    args=parser.parse_args();build_server(args.task,args.stage,args.provider).run()
