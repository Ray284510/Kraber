"""Finite batch receipt freshness. No NX calls, I/O, billing or global CAD proof."""
import hashlib
import json
import ntpath

# Exact command capabilities, grounded in the current Loader dispatch. This is
# intentionally narrower than Worker's transport READ_ONLY list: diagnostic
# commands without a reviewed geometry side-effect contract remain unknown.
PRESENTATION = frozenset({'nx_fit_view', 'nx_color'})
OBSERVATIONS = frozenset({'nx_status', 'nx_session_parts', 'nx_inspect',
                         'nx_list_bodies', 'nx_list_features', 'nx_list_edges'})
MUTATIONS = frozenset({'nx_create_part', 'nx_open_part', 'nx_close_part', 'nx_shape', 'nx_sphere',
                      'nx_hole', 'nx_blend_strict', 'nx_add_component', 'nx_constraint',
                      'nx_constraint_value', 'nx_constraint_suppress', 'nx_import_step',
                      'nx_surface_edit_ellipse', 'nx_surface_edit_arc', 'nx_surface_loft',
                      'nx_surface_shell', 'nx_surface_sew', 'nx_surface_thicken',
                      'nx_surface_sweep', 'nx_surface_sweep_fixture', 'nx_surface_trim',
                      'nx_surface_extend', 'nx_surface_fixture'})


def operation_capability(name):
    if name in PRESENTATION:return 'presentation_only'
    if name in OBSERVATIONS:return 'non_geometry_observation'
    if name in MUTATIONS:return 'geometry_mutation'
    return 'unknown'  # includes save/export until a narrower state contract is reviewed


def digest(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,ensure_ascii=False,separators=(',',':')).encode()).hexdigest()


def part_path(value):
    if not isinstance(value,str) or not value or not ntpath.isabs(value):return None
    return ntpath.normcase(ntpath.normpath(value))


def model_identity(status, session, stage_session_id):
    """Bind work-part path to a loaded part Tag and a process, not a geometry hash."""
    if not isinstance(status,dict) or status.get('ok') is not True:return None
    if not isinstance(session,dict) or session.get('ok') is not True:return None
    path=part_path(status.get('part'));pid=session.get('process_id')
    if not path or type(pid) is not int or pid<=0 or session.get('all_modified_flags_known') is not True:return None
    rows=session.get('parts')
    if not isinstance(rows,list):return None
    owners=[p for p in rows if isinstance(p,dict) and part_path(p.get('path'))==path]
    if len(owners)!=1 or not isinstance(owners[0].get('tag'),str) or not owners[0]['tag']:return None
    version=session.get('nx')
    if not isinstance(version,str) or not version or status.get('nx')!=version:return None
    return dict(stage_session_id=stage_session_id,process_id=pid,part=path,part_tag=owners[0]['tag'],nx=version)


class BatchGeometry:
    """No cross-batch PASS reuse; indices are server-stage command sequences (1-based)."""
    def __init__(self,batch_id,last_geometry_mutation_index=None):
        self.batch_id=batch_id;self.operation_sequence=None
        self.geometry_dirty=True;self.last_geometry_mutation_index=last_geometry_mutation_index
        self.last_successful_inspection_index=None;self.current_identity=None
        self.inspections=[];self.identity_checks=[];self.stale_reason=None

    def invalidate(self,reason):
        self.geometry_dirty=True
        if self.inspections:
            self.stale_reason=reason
            for receipt in self.inspections:
                receipt['valid_for_current_state']=False
                receipt['geometry_pass']=False
                receipt['invalidated_reason']=reason
            self.inspections[-1]['status']='STALE_INSPECTION'

    def begin(self,index,capability):
        self.operation_sequence=index
        if capability in ('geometry_mutation','unknown'):
            self.last_geometry_mutation_index=index
            self.invalidate('geometry_or_unknown_operation_after_inspection')

    def inspect(self,gate,raw,before,after,index):
        for prior in self.inspections:
            prior['valid_for_current_state']=False
            prior['geometry_pass']=False
        identity=before.get('identity') if before else None
        later=after.get('identity') if after else None
        self.current_identity=later;self.stale_reason=None
        binding=dict(batch_id=self.batch_id,operation_sequence=index,
                     last_geometry_mutation_index=self.last_geometry_mutation_index,model_identity=later,
                     raw_receipt_sha256=digest(raw),before_context_sha256=digest(before),after_context_sha256=digest(after))
        receipt=dict(gate,receipt_gate=dict(gate),binding=binding,
                     inspection_receipt_id=digest(binding),valid_for_current_state=False)
        self.inspections.append(receipt)
        if gate['status'] not in ('GEOMETRY_RECEIPT_PASS','EMPTY_STAGE_ALLOWED'):
            self.geometry_dirty=True;return receipt
        consistent=bool(identity and identity==later and part_path(raw.get('part'))==identity['part'])
        if consistent:
            for key in ('process_id','part_tag','stage_session_id'):
                if key in raw and raw[key]!=identity[key]:consistent=False
            if 'model_identity' in raw and raw['model_identity']!=identity:consistent=False
            if 'operation_sequence' in raw and (type(raw['operation_sequence']) is not int or raw['operation_sequence']!=index):consistent=False
            if 'batch_id' in raw and raw['batch_id']!=self.batch_id:consistent=False
        if not consistent:
            self.invalidate('inspection_context_missing_changed_or_mismatched')
            return receipt
        receipt['valid_for_current_state']=True;self.geometry_dirty=False
        if gate['geometry_pass']:self.last_successful_inspection_index=index
        return receipt

    def check_current(self,context):
        self.identity_checks.append(context)
        identity=context.get('identity');self.current_identity=identity
        if self.inspections and self.inspections[-1]['valid_for_current_state']:
            if not identity or identity!=self.inspections[-1]['binding']['model_identity']:
                self.invalidate('current_model_session_changed_after_inspection')

    def result(self,operation_ok):
        latest=self.inspections[-1] if self.inspections else None
        fresh=bool(latest and latest['valid_for_current_state'] and not self.geometry_dirty)
        passed=bool(operation_ok and fresh and latest['geometry_pass'])
        return dict(ok=operation_ok,operation_ok=operation_ok,geometry_gate=passed,geometry_pass=passed,
                    geometry_status=latest['status'] if latest else 'INSPECTION_REQUIRED',
                    geometry_dirty=self.geometry_dirty,inspection_stale=bool(self.stale_reason),
                    stale_reason=self.stale_reason,last_geometry_mutation_index=self.last_geometry_mutation_index,
                    last_successful_inspection_index=self.last_successful_inspection_index,
                    operation_sequence=self.operation_sequence,batch_id=self.batch_id,
                    inspection_receipt_ids=[r['inspection_receipt_id'] for r in self.inspections],
                    current_model_identity=self.current_identity,inspections=self.inspections,
                    identity_checks=self.identity_checks,
                    scope='Finite serialized batch receipt freshness; not NX native geometry quality or external-edit proof')


def require_current_geometry(result):
    """Consumer guard: operation ok or historical inspections alone are insufficient."""
    if (not isinstance(result,dict) or result.get('operation_ok') is not True
            or result.get('geometry_pass') is not True or result.get('geometry_gate') is not True
            or result.get('inspection_stale') is not False or result.get('geometry_dirty') is not False):
        raise ValueError('Current batch geometry inspection required')
    inspections=result.get('inspections')
    if not isinstance(inspections,list) or not inspections:raise ValueError('Inspection receipt missing')
    latest=inspections[-1]
    if not isinstance(latest,dict) or not isinstance(latest.get('binding'),dict):raise ValueError('Inspection binding missing')
    binding=latest['binding'];seq=binding.get('operation_sequence');mutation=result.get('last_geometry_mutation_index')
    final=result.get('operation_sequence')
    if (not isinstance(binding.get('model_identity'),dict) or not binding['model_identity']
            or not isinstance(result.get('batch_id'),str) or not result['batch_id']
            or type(seq) is not int or type(final) is not int or not 1<=seq<=final
            or (mutation is not None and (type(mutation) is not int or not 1<=mutation<seq))):
        raise ValueError('Invalid inspection sequence or identity')
    if (latest.get('valid_for_current_state') is not True or latest.get('geometry_pass') is not True
            or binding.get('batch_id')!=result.get('batch_id')
            or binding.get('model_identity')!=result.get('current_model_identity')
            or binding.get('last_geometry_mutation_index')!=result.get('last_geometry_mutation_index')
            or binding.get('operation_sequence')!=result.get('last_successful_inspection_index')):
        raise ValueError('Stale or mismatched batch inspection binding')
    return latest
