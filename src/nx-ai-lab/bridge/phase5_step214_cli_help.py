"""Review-only proposal: one help invocation; never supply CAD inputs/outputs."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

TASK = Path(__import__('os').environ['NX_AI_LAB_ROOT']) / 'logs/phase5/staged_20261108_212227'
NX = Path(__import__('os').environ['NX_INSTALL_DIR'])
EXE = NX / 'NXBIN/step214ug.exe'
RUN = TASK / 'cli_help_attempt1'
STEP = 'a0_step214_cli_help'
# Help-only candidate; installed NX12 acceptance of -h is NOT yet verified.
ARGV = [str(EXE), '-h']


def guard(path):
    for p in (path, *path.parents):
        if not p.exists() or p.is_symlink() or p.is_junction():
            raise RuntimeError('Missing/reparse path refused: ' + str(p))


def digest(path):
    guard(path)
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1048576), b''):
            h.update(block)
    return h.hexdigest()


def record(name, value):
    with (RUN / name).open('x', encoding='utf-8') as f:
        json.dump(value, f, ensure_ascii=False, indent=2)
        f.flush()
        os.fsync(f.fileno())


def main():
    if os.name != 'nt':
        raise RuntimeError('Windows only')
    guard(TASK)
    watched = [EXE, NX / 'STEP214UG/ugstep214.def',
               NX / 'STEP214UG/step214ug.def']
    before = {str(p): digest(p) for p in watched}
    RUN.mkdir()  # Exclusive durable identity: never rerun even after interruption.
    env = dict(os.environ)  # Do not alter licensing variables or configuration.
    env.update(TEMP=str(RUN), TMP=str(RUN), UGII_TMP_DIR=str(RUN))
    env.update(UGII_BASE_DIR=str(NX), UGII_ROOT_DIR=str(NX/'UGII'),
               STEP214UG_DIR=str(NX/'STEP214UG')+'\\',
               ROSE=str(NX/'STEP214UG')+'\\', ROSE_DB=str(NX/'STEP214UG')+'\\')
    env['PATH'] = str(NX/'NXBIN')+';'+env.get('PATH', '')
    record('started.json', dict(step=STEP, attempt=1, argv=ARGV,
           cwd=str(RUN), hashes=before, started=time.time(),
           scope='help only; no NX acceptance; review required before execution'))
    result = dict(step=STEP, attempt=1, status='not_started', retry_safe=False)
    try:
        with (RUN / 'stdout.bin').open('xb') as out, (RUN / 'stderr.bin').open('xb') as err:
            with subprocess.Popen(ARGV, cwd=RUN, env=env, stdin=subprocess.DEVNULL,
                                  stdout=out, stderr=err, shell=False,
                                  creationflags=subprocess.CREATE_NO_WINDOW) as proc:
                record('launched.json', {'pid': proc.pid, 'argv': ARGV})
                try:
                    result['returncode'] = proc.wait(timeout=10)
                    result['status'] = 'exited_help_unverified'
                except subprocess.TimeoutExpired:
                    proc.kill()  # Only our child; never terminate the NX session.
                    result['status'] = 'timeout_stop_for_review'
                    result['returncode'] = proc.wait(timeout=3)
    except Exception as exc:
        result.update(status='error_stop_for_review', error=repr(exc))
    finally:
        result['finished'] = time.time()
        result['hashes_after'] = {str(p): digest(p) for p in watched}
        result['watched_files_unchanged'] = result['hashes_after'] == before
        result['scope'] = 'Raw help evidence only; exit zero is not CLI/STEP acceptance'
        record('finished.json', result)
    # No flag fallbacks, no export/import, no model access, no automatic retry.


if __name__ == '__main__':
    main()
