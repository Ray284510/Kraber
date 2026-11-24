"""Review only. Codex must supply fresh native nx_stage_state()['nx_session']."""
import hashlib, json, os, re, subprocess, time
from pathlib import Path
ROOT = Path(__import__('os').environ['NX_AI_LAB_ROOT'])
NX = Path(__import__('os').environ['NX_INSTALL_DIR'])
TASK = ROOT / 'logs/phase5/staged_20261108_212227'
SRC = ROOT / 'models/phase4_phase5_staged_20261108_212227/planar_reference.prt'
RUN = TASK / 'planar_export_diagnostic_attempt0'
EXE = NX / 'NXBIN/step214ug.exe'
OFFICIAL = [NX / 'STEP214UG/ugstep214.def', NX / 'STEP214UG/step214ug.def']
STEP = 'a1_planar_export_diagnostic'
def guard(p):
    for q in (p, *p.parents):
        if not q.exists() or q.is_symlink() or q.is_junction():
            raise RuntimeError('Missing/reparse path: ' + str(q))
def sha(p):
    guard(p)
    with p.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()
def record(name, obj):
    with (RUN / name).open('x', encoding='utf-8') as f:
        json.dump(obj, f, ensure_ascii=False, indent=2); f.flush(); os.fsync(f.fileno())
def main(state):
    if os.name != 'nt': raise RuntimeError('Windows only')
    if not (state.get('ok') and state.get('all_modified_flags_known') and
            state.get('any_modified') is False): raise RuntimeError('Unsaved/unknown NX state')
    if not any(Path(p['path']) == SRC and p.get('is_modified') is False and
               p.get('is_fully_loaded') for p in state.get('parts', [])):
        raise RuntimeError('Require saved, fully loaded task copy; never close/save here')
    guard(TASK)
    watched = [SRC, *OFFICIAL, EXE]
    before = {str(p): sha(p) for p in watched}
    if before[str(OFFICIAL[0])] != '6beaa7e676013f173189e7908fa9afe70a19db6dba31113583f1d25115ddc412':
        raise RuntimeError('Official export template changed; review required')
    RUN.mkdir()  # Exclusive stable identity, including crashes before Popen: NEVER retry.
    result = dict(step=STEP, attempt=0, retry_safe=False, status='not_launched', precision_passed=False)
    try:
        record('before.json', dict(hashes=before, session=state, started=time.time()))
        raw = OFFICIAL[0].read_bytes()
        with (RUN / 'template.def').open('xb') as f: f.write(raw)
        text = raw.decode('ascii')
        edits = {'CHOOSE_DIRECTION':'UG to STEP', 'CNV_MODE':'Off-Line',
                 'MODULES_MASK':'Solids', 'PA_EDGE_FACE_BSPLINE_APPROX_TOL':'0.00005000000',
                 'Author':'DSH_A1_PLANAR_ATTEMPT0_TOL_5E5_DEF_TRACE',
                 'UGI_LAYER_MASK':'1-256', 'UGI_LAYERE_MASK':'', 'UGI_LAYERD_MASK':'',
                 'SAVE_DEF_FILE':str(RUN / 'saved.def'), 'RECALL_DEF_FILE':str(RUN / 'export.def'),
                 'LOG_FILE':str(RUN / 'translator.log')}
        for key, value in edits.items():
            text, n = re.subn(r'(?m)^'+re.escape(key)+r'[^\S\r\n]*=[^\r\n]*', lambda m: key+' = '+value, text)
            if n != 1: raise RuntimeError('Ambiguous/missing setting: '+key)
        defs, out = RUN / 'export.def', RUN / 'output.step'
        with defs.open('x', encoding='ascii', newline='') as f: f.write(text)
        env = dict(os.environ)
        env.update(UGII_BASE_DIR=str(NX), UGII_ROOT_DIR=str(NX/'UGII'),
                   UGII_TMP_DIR=str(RUN), TEMP=str(RUN), TMP=str(RUN),
                   STEP214UG_DIR=str(NX/'STEP214UG')+'\\', ROSE=str(NX/'STEP214UG')+'\\', ROSE_DB=str(NX/'STEP214UG')+'\\')
        env['PATH'] = str(NX/'NXBIN')+';'+env['PATH']
        argv = [str(EXE), str(SRC), 'o='+str(out), 'd='+str(defs), 'l='+str(RUN/'translator.log')]
        record('launch_intent.json', dict(argv=argv, cwd=str(RUN), settings_sha256=sha(defs), edits=edits))
        if out.exists(): raise RuntimeError('Refuse overwrite')
        with (RUN/'stdout.bin').open('xb') as stdout, (RUN/'stderr.bin').open('xb') as stderr:
            proc = subprocess.Popen(argv, cwd=RUN, env=env, stdin=subprocess.DEVNULL,
                                    stdout=stdout, stderr=stderr, shell=False, creationflags=subprocess.CREATE_NO_WINDOW)
            result.update(pid=proc.pid, status='launched_unknown')
            try:
                result.update(returncode=proc.wait(timeout=45), status='exited_unverified')
            except subprocess.TimeoutExpired:
                result['status'] = 'timeout_unknown_stop_for_review'
                proc.kill(); result['returncode'] = proc.wait(timeout=3)
        result['output_exists'] = out.is_file()
    except BaseException as exc:
        result.update(status='error_or_unknown_stop_for_review', error=repr(exc))
    finally:
        after = {}
        for p in watched:
            try: after[str(p)] = sha(p)
            except Exception as exc: after[str(p)] = {'hash_error':repr(exc)}
        result.update(hashes_after=after, watched_unchanged=(after == before), finished=time.time())
        record('finished.json', result)
# No automatic entry point, help call, fallback, builder Commit, import, save or close.
