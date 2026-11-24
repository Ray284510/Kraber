"""Read-only offline phase audit. No launcher, NX, network or repair capability."""
import hashlib,json,uuid,datetime
from pathlib import Path
from .status_only import NX_PATH,NX_SHA256,PROBE,PROBE_SHA256

PHASES=('PREPARED_VALIDATED','START_INTENT_DURABLE','START_RETURNED_PID','START_OUTCOME_UNKNOWN','GUARD_RELEASED','GUARD_RELEASE_UNKNOWN')

def digest(text):return hashlib.sha256(text.encode('utf-8')).hexdigest()

def verify_phase_journal(envelopes, *, legacy_result=None):
    """Input order is evidence order; never sort/repair phases or trust result.json."""
    if not isinstance(envelopes,(list,tuple)):
        return {'status':'UNKNOWN','start_outcome':'UNKNOWN_START_OUTCOME','complete':False,
                'errors':[{'reason':'ordered phase list required'}],'replay_allowed':False,
                'legacy_result_used':False,'repair_performed':False,'native_readiness_proven':False}
    errors=[];payloads=[];previous=None;identity=None
    common=('attempt_uuid','nx_path','nx_sha256','nx_version','probe_path','probe_sha256','environment_schema_hash','directory_preflight_receipt_hash','authorization_reference','provenance')
    for index,envelope in enumerate(envelopes):
        try:
            if envelope['schema']!='p13-launch-envelope-v1':raise ValueError('envelope schema')
            raw=envelope['payload_json'];p=json.loads(raw)
            if envelope['payload_sha256']!=digest(raw) or envelope['previous_phase_sha256']!=previous or envelope['phase_sha256']!=digest((previous or '-')+'\n'+raw):raise ValueError('phase hash chain')
            if p['schema']!='p13-launch-phase-v1' or type(p['sequence']) is not int or p['sequence']!=index:raise ValueError('payload schema/sequence')
            if p['phase'] not in PHASES or p['state']!=p['phase']:raise ValueError('phase/state')
            if str(uuid.UUID(p['attempt_uuid']))!=p['attempt_uuid']:raise ValueError('canonical UUID')
            if p['provenance'] not in ('synthetic/mock','native_process_launch_after_explicit_authorization'):raise ValueError('provenance')
            if not isinstance(p['authorization_reference'],str) or not p['authorization_reference'].strip():raise ValueError('authorization reference')
            if (p['nx_path'],p['nx_sha256'],p['nx_version'],p['probe_path'],p['probe_sha256'])!=(NX_PATH,NX_SHA256,'12.0.2.9',PROBE,PROBE_SHA256):raise ValueError('fixed identity')
            for key in ('environment_schema_hash','directory_preflight_receipt_hash'):
                if not isinstance(p[key],str) or len(p[key])!=64 or any(c not in '0123456789abcdef' for c in p[key]):raise ValueError('contract hash')
            if datetime.datetime.fromisoformat(p['timestamp']).tzinfo is None:raise ValueError('timestamp timezone')
            current=tuple(p[k] for k in common)
            if identity is not None and current!=identity:raise ValueError('attempt identity changed')
            identity=current
            expected_calls=0 if p['phase']=='PREPARED_VALIDATED' else 1
            if type(p['launch_calls']) is not int or p['launch_calls']!=expected_calls:raise ValueError('intent call count')
            dispatch=0 if p['phase'] in ('PREPARED_VALIDATED','START_INTENT_DURABLE') else 1
            if type(p['start_dispatch_count']) is not int or p['start_dispatch_count']!=dispatch:raise ValueError('dispatch count')
            if p['phase']=='START_RETURNED_PID' and (type(p['details'].get('nx_pid')) is not int or p['details']['nx_pid']<=0):raise ValueError('positive PID')
            if p['phase']=='GUARD_RELEASED' and p['details'].get('cleanup_status')!='RELEASED':raise ValueError('guard receipt')
            if p['phase']=='GUARD_RELEASE_UNKNOWN' and p['details'].get('cleanup_status')!='UNKNOWN_GUARD_RELEASE_FAILED':raise ValueError('guard receipt')
            payloads.append(p);previous=envelope['phase_sha256']
        except (KeyError,ValueError,TypeError,AttributeError) as error:
            errors.append({'index':index,'reason':str(error)});break
    phases=[p['phase'] for p in payloads]
    if len(set(phases))!=len(phases):errors.append({'reason':'duplicate phase'})
    allowed_prefixes=[['PREPARED_VALIDATED','START_INTENT_DURABLE',result,guard]
        for result in ('START_RETURNED_PID','START_OUTCOME_UNKNOWN') for guard in ('GUARD_RELEASED','GUARD_RELEASE_UNKNOWN')]
    if not any(phases==order[:len(phases)] for order in allowed_prefixes):errors.append({'reason':'phase order'})
    complete=not errors and len(phases)==4 and len(payloads)==len(envelopes)
    start='UNKNOWN_START_OUTCOME'
    pid=None
    if not errors and 'START_RETURNED_PID' in phases:
        receipt=payloads[phases.index('START_RETURNED_PID')];pid=receipt['details']['nx_pid'];start='START_RETURNED_PID'
    guard=phases[-1] if phases and phases[-1] in ('GUARD_RELEASED','GUARD_RELEASE_UNKNOWN') else 'UNKNOWN'
    success=complete and start=='START_RETURNED_PID' and guard=='GUARD_RELEASED'
    return {'status':'RECORDED_START_RETURNED_PID' if success else 'UNKNOWN','phase_chain_valid':not errors,'complete':complete,
        'start_outcome':start,'pid_from_receipt':pid,'guard_outcome':guard,'errors':errors,'observed_phases':phases,
        'replay_allowed':False,'legacy_result_used':False,'native_readiness_proven':False,'repair_performed':False}

def read_phase_files(paths, *, legacy_result=None):
    """Only reads ordered paths; malformed/truncated files remain UNKNOWN."""
    envelopes=[]
    try:
        for path in paths:envelopes.append(json.loads(Path(path).read_text(encoding='utf-8-sig')))
    except (OSError,ValueError) as error:
        return {'status':'UNKNOWN','start_outcome':'UNKNOWN_START_OUTCOME','complete':False,'errors':[type(error).__name__],
                'replay_allowed':False,'legacy_result_used':False,'repair_performed':False}
    return verify_phase_journal(envelopes,legacy_result=legacy_result)
