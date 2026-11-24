"""Pure preconditions/evidence checks for the one-shot P13 harness. No transport."""
from decimal import Decimal
import hashlib
import math
from .batch_geometry import part_path, digest
from pathlib import Path
from .batch_geometry import require_current_geometry


def prepared_paths(folder,spec,root):
    folder=Path(folder).resolve();root=Path(root).resolve()
    models=Path(spec['models']).resolve();probe=Path(spec['probe']['copy_path']).resolve()
    if not folder.is_relative_to(root/'logs/phase5') or not models.is_relative_to(root/'models'):
        raise ValueError('Task/model roots outside authorized project scopes')
    if probe!=models/'broker_probe.prt' or not probe.is_file():raise ValueError('Prepared probe copy missing/mismatched')
    if spec.get('readonly_max_attempts')!=1 or type(spec.get('readonly_max_attempts')) is not int:
        raise ValueError('P13 requires explicit readonly_max_attempts=1')
    if hashlib.sha256(probe.read_bytes()).hexdigest()!=spec['probe']['sha256']:raise ValueError('Probe copy hash changed')
    target=models/'batch_validation.prt'
    if target.exists():raise ValueError('Fresh target required; no replay/overwrite')
    return models,probe,target


def require_probe_session(status,session,probe):
    if status.get('ok') is not True or session.get('ok') is not True:raise ValueError('Native status/session failed')
    if status.get('nx')!='12.0.2.9' or session.get('nx')!='12.0.2.9':raise ValueError('NX version mismatch')
    if session.get('all_modified_flags_known') is not True or session.get('any_modified') is not False:
        raise ValueError('Unsaved/unknown parts; stop')
    parts=session.get('parts')
    if not isinstance(parts,list) or len(parts)!=1:raise ValueError('Exactly the prepared probe must be loaded')
    if Path(status.get('part','')).resolve()!=probe or Path(parts[0].get('path','')).resolve()!=probe:
        raise ValueError('Work/loaded part is not the prepared probe')
    if parts[0].get('is_modified') is not False or not parts[0].get('tag'):raise ValueError('Probe identity/modified unknown')
    if type(session.get('process_id')) is not int or session['process_id']<=0:raise ValueError('NX PID missing')
    return dict(process_id=session['process_id'],part=str(probe),tag=parts[0]['tag'])


def require_temporal_evidence(result, *, labels=None, probe=None, final_part=None, expected_identity=None):
    if labels is not None:
        return _four_inspection_evidence(result,labels,final_part,expected_identity)
    require_current_geometry(result)
    rows=result['results'];names=[r['command'].split()[0] for r in rows]
    inspect_indices=[i for i,n in enumerate(names) if n=='nx_inspect']
    if len(inspect_indices)!=2:raise ValueError('Expected two native inspections')
    first,last=inspect_indices
    inspected=rows[first]['freshness_after_operation']
    if inspected['geometry_pass'] is not True:raise ValueError('First inspection did not pass')
    for i in range(first+1,last):
        row=rows[i];before=row['freshness_before_dispatch'];after=row['freshness_after_operation']
        if row['capability']=='presentation_only':
            if before['geometry_pass'] is not True or after['geometry_pass'] is not True:
                raise ValueError('View did not preserve fresh inspection')
            if after['inspection_receipt_id']!=inspected['inspection_receipt_id']:raise ValueError('View rebound receipt')
        else:
            if before['geometry_pass'] is not False or after['geometry_pass'] is not False or not after['inspection_stale']:
                raise ValueError('Save/close/open did not invalidate immediately')
    final=rows[last]['freshness_after_operation']
    if final['inspection_receipt_id']==inspected['inspection_receipt_id']:raise ValueError('Final inspection not rebound')
    if final['inspection_binding']['last_geometry_mutation_index']!=result['last_geometry_mutation_index']:
        raise ValueError('Final inspection missing latest mutation')
    return {'first_inspection':inspected,'final_inspection':final,'per_operation':[r['freshness_after_operation'] for r in rows]}


SMOKE_LABELS=('I0','I1','I2','I3')
SMOKE_VOLUME=1280-16*math.pi
IDENTITY_READ_LIMITS={'nx_status':20,'nx_session_parts':21}
NEW_BLANK = 'new_blank_prt_i0_v1'


def fresh_target(folder, spec, root):
    models = Path(spec['models']).resolve()
    if (not folder.resolve().is_relative_to(root.resolve()/'logs/phase5')
            or models != (root.resolve()/'models/phase4_p13_smoke_e80a1f33')
            or not models.is_dir() or spec.get('initial_part_contract') != NEW_BLANK):
        raise ValueError('Expected fixed disposable directory and new blank part contract')
    target = models/'batch_validation.prt'
    try:
        target.lstat()
    except FileNotFoundError:
        return target
    raise FileExistsError('Fresh target required; no replay/overwrite')


def require_empty_session(status, session):
    if (status.get('ok') is not True or session.get('ok') is not True
            or status.get('nx') != '12.0.2.9' or session.get('nx') != status['nx']
            or any(status.get(field) != '' for field in ('part','p13_work_path','p13_display_path'))
            or any(field not in status or status[field] is not None for field in ('p13_work_tag','p13_display_tag'))
            or session.get('parts') != [] or type(session.get('process_id')) is not int or session['process_id'] <= 0):
        raise ValueError('Explicitly empty Work/Display/Loaded session required before create')
    return dict(process_id=session['process_id'], part=None, tag=None)


def smoke_part_tag(receipt, path):
    tag = receipt.get('p13_work_tag')
    target = part_path(str(path))
    if (not target or not isinstance(tag,str) or not tag.isascii() or not tag.isdecimal() or int(tag) <= 0
            or receipt.get('p13_display_tag') != tag
            or any(part_path(receipt.get(k)) != target for k in ('p13_work_path','p13_display_path'))):
        raise ValueError('Work/Display path or Tag mismatch')
    return tag


def require_blank_origin(raw, create_row, target):
    created = create_row.get('actual', {})
    request = created.get('p13_request')
    if (create_row.get('step') != 'create' or create_row.get('command') != 'nx_create_part '+str(target)
            or created.get('receipt_kind') != 'fresh_dispatch' or created.get('ok') is not True
            or not isinstance(request,str) or not request or request != created.get('p13_create_request')
            or raw.get('p13_create_request') != request or raw.get('p13_blank_observed') is not True
            or smoke_part_tag(raw,target) != smoke_part_tag(created,target)
            or raw.get('bodies') != [] or not isinstance(raw.get('sketches'),list)
            or any(not isinstance(sketch,dict) or type(sketch.get('degrees_of_freedom')) is not int
                   or sketch['degrees_of_freedom'] != 0 for sketch in raw['sketches'])):
        raise ValueError('I0 requires this batch fresh create receipt and zero bodies')


def smoke_operations(part):
    return [dict(step='create',command='nx_create_part '+str(part)),
            dict(step='I0',command='nx_inspect'),
            dict(step='solid',command='nx_shape batch_outline box 0 0 0 0 0 1 20 16 4 create -'),
            dict(step='I1',command='nx_inspect'),
            dict(step='hole',command='nx_hole $solid.body_id 0 0 4 4 0'),
            dict(step='I2',command='nx_inspect'),dict(step='view',command='nx_fit_view'),
            dict(step='save',command='nx_save_part'),dict(step='close',command='nx_close_part 0'),
            dict(step='reopen',command='nx_open_part '+str(part)),dict(step='I3',command='nx_inspect')]


def operation_by_step(rows,step):
    matches=[r for r in rows if r.get('step')==step]
    if len(matches)!=1:raise ValueError('Unique stable step required: '+step)
    return matches[0]


def smoke_inspection_checks():
    return {k:{'volume_mm3':None if k=='I0' else 1280.0 if k=='I1' else SMOKE_VOLUME} for k in SMOKE_LABELS}


def require_smoke_inspection(raw,expected_volume=None):
    # Explicit checks survive -O; the production Gate still validates full receipt policy.
    if not isinstance(raw,dict) or raw.get('ok') is not True:raise ValueError('Inspection failed')
    bodies=raw.get('bodies')
    if not isinstance(bodies,list) or len(bodies)!=1 or bodies[0].get('solid') is not True:
        raise ValueError('One solid required')
    volume=bodies[0].get('volume_mm3')
    if type(volume) not in (int,float) or not math.isfinite(volume) or volume<=0:raise ValueError('Invalid volume')
    if expected_volume is not None:
        # Compare the JSON measurement as stated; binary subtraction can round
        # 1280.00001 - 1280 below the excluded boundary.
        error = abs(Decimal(str(volume)) - Decimal(str(expected_volume)))
        if error >= Decimal('0.00001'):
            raise ValueError('Smoke volume mismatch')
    sketches=raw.get('sketches')
    if not isinstance(sketches,list):raise ValueError('Sketch receipt missing')
    for sketch in sketches:
        if not isinstance(sketch,dict) or type(sketch.get('degrees_of_freedom')) is not int or sketch['degrees_of_freedom']!=0:
            raise ValueError('Sketch not fully constrained')
    return raw


def require_duplicate_observation(before, after, expectation, expected_part):
    """Validate the independent observation before any restoration or PASS record."""
    from .stage_geometry import inspect_geometry
    for receipt in (before, after):
        gate = inspect_geometry(receipt, expectation, stage_id='batch-validation')
        if gate['geometry_pass'] is not True:
            raise ValueError('Duplicate observation geometry gate: ' + gate['reason'])
        require_smoke_inspection(receipt, SMOKE_VOLUME)
        if part_path(receipt.get('part')) != part_path(str(expected_part)):
            raise ValueError('Duplicate observation part mismatch')
    if before['bodies'][0]['volume_mm3'] != after['bodies'][0]['volume_mm3']:
        raise ValueError('Duplicate operation changed volume')
    if len(before['features']) != len(after['features']):
        raise ValueError('Duplicate operation changed feature count')


def require_smoke_execution_policy(spec):
    # This reference must be supplied only after separate native authorization.
    consent=spec.get('internal_identity_reads_authorization')
    limits = IDENTITY_READ_LIMITS if spec.get('initial_part_contract') == NEW_BLANK else {'nx_status':23,'nx_session_parts':24}
    if (not isinstance(consent,dict) or consent.get('task_uuid')!=spec.get('task_uuid')
            or not isinstance(consent.get('user_message_reference'),str) or not consent['user_message_reference'].strip()
            or consent.get('limits')!=limits):
        raise ValueError('Separate native internal identity-read authorization required')
    if spec.get('strict_single_connect') is not True or type(spec.get('expected_server_pid')) is not int or spec['expected_server_pid']<=0:
        raise ValueError('Strict connection and expected peer PID required')
    if type(spec.get('readonly_max_attempts')) is not int or spec['readonly_max_attempts']!=1:
        raise ValueError('One outer attempt required')


def _four_inspection_evidence(result,labels,final_part,expected_identity):
    require_current_geometry(result)  # Sole final PASS consumer; no bool shortcut.
    if tuple(labels)!=SMOKE_LABELS or final_part is None or not expected_identity:
        raise ValueError('Explicit four-label/path/initial identity contract required')
    rows=result['results'];inspection_rows=[r for r in rows if r['command'].split()[0]=='nx_inspect']
    if [r.get('step') for r in inspection_rows]!=list(SMOKE_LABELS):raise ValueError('Four inspections missing/reordered')
    expected_steps=[r['step'] for r in smoke_operations(final_part)]
    if [r.get('step') for r in rows]!=expected_steps:raise ValueError('Unexpected smoke sequence')
    session_id=None;previous=None;seen=[]
    for row in rows:
        before=row['freshness_before_dispatch'];after=row['freshness_after_operation']
        if row['capability'] in ('geometry_mutation','unknown'):
            if before['geometry_pass'] or after['geometry_pass'] or not before['geometry_dirty']:
                raise ValueError('Mutation did not invalidate before dispatch')
            if previous and not before['inspection_stale']:raise ValueError('Missing stale receipt')
        elif row['capability']=='presentation_only':
            if not previous or not before['geometry_pass'] or not after['geometry_pass'] or after['inspection_receipt_id']!=previous:
                raise ValueError('Presentation did not preserve latest inspection')
        if row.get('step') not in SMOKE_LABELS:continue
        label=row['step']
        if label == 'I0':
            require_blank_origin(row['actual'],operation_by_step(rows,'create'),final_part)
            if (row['inspection']['receipt_gate']['status'] != 'EMPTY_STAGE_ALLOWED'
                    or after['geometry_pass'] is not False or after['geometry_gate'] is not False
                    or after['last_successful_inspection_index'] is not None):
                raise ValueError('I0 cannot certify geometry')
        else:
            require_smoke_inspection(row['actual'],smoke_inspection_checks()[label]['volume_mm3'])
        binding=after['inspection_binding'];identity=binding['model_identity']
        origin = operation_by_step(rows,'reopen' if label == 'I3' else 'create')['actual']
        tag = smoke_part_tag(origin,final_part)
        if tag != smoke_part_tag(row['actual'],final_part) or tag != identity['part_tag']:
            raise ValueError('Inspection ownership does not match create/reopen receipt')
        if (after['geometry_pass'] is not (label != 'I0') or after['geometry_dirty'] or binding['batch_id']!=result['batch_id']
                or binding['operation_sequence']!=row['operation_sequence']
                or binding['raw_receipt_sha256']!=digest(row['actual'])
                or binding['before_context_sha256']!=digest(row['inspection_context_before'])
                or binding['after_context_sha256']!=digest(row['inspection_context_after'])
                or after['inspection_receipt_id']!=digest(binding)
                or binding['last_geometry_mutation_index']!=after['last_geometry_mutation_index']
                or identity['part']!=part_path(str(final_part))
                or identity['process_id']!=expected_identity['process_id'] or identity['nx']!='12.0.2.9'
                or not identity['part_tag'] or not identity['stage_session_id']):
            raise ValueError('Inspection binding mismatch')
        if session_id is None:session_id=identity['stage_session_id']
        if identity['stage_session_id']!=session_id:raise ValueError('Stage session changed')
        if after['inspection_receipt_id'] in seen:raise ValueError('Inspection not rebound')
        previous=after['inspection_receipt_id'];seen.append(previous)
    final=operation_by_step(rows,'I3')['freshness_after_operation']
    if final['inspection_binding']!=result['inspections'][-1]['binding']:raise ValueError('Final receipt mismatch')
    return dict(first_inspection=operation_by_step(rows,'I0')['freshness_after_operation'],final_inspection=final,
                inspections={k:operation_by_step(rows,k)['freshness_after_operation'] for k in SMOKE_LABELS},
                per_operation=[r['freshness_after_operation'] for r in rows])
