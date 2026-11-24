"""Capture actual state before pausing/taking over; never save an unsaved user part."""
import asyncio
import hashlib
import json
import subprocess
import uuid
from pathlib import Path
from .nx_worker import Worker, ROOT
from .task_store import now


async def capture(folder, stage, reason):
    folder = Path(folder).resolve()
    async with Worker(folder, 'codex') as worker:
        session = await worker.observe('nx_session_parts')
        inspect = await worker.observe('nx_inspect') if session.get('parts') else None
        operations = [dict(r) for r in worker.store.db.execute('SELECT * FROM operations ORDER BY created')]
        unresolved = [r['id'] for r in operations if r['state'] not in ('succeeded', 'failed')]
        safe = (session.get('ok') is True and session.get('all_modified_flags_known') is True
                and session.get('any_modified') is False and not unresolved)
        documents = {}
        for name in ('requirement.txt', 'handoff.md', 'task_spec.json'):
            raw = (folder / name).read_bytes()
            documents[name] = {'sha256': hashlib.sha256(raw).hexdigest(), 'content': raw.decode('utf-8-sig')}
        artifacts = [{'path': str(p), 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
                     for p in sorted((folder / 'stages').glob('*.json'))]
        from .model_status import snapshot
        record = {'at': now(), 'stage': stage, 'reason': reason, 'safe_to_switch': safe,
                  'native_session': session, 'native_inspect': inspect, 'operations': operations,
                  'unresolved': unresolved, 'documents': documents, 'stage_artifacts': artifacts,
                  'next_action': json.loads((folder / 'stages' / (stage + '.json')).read_text(encoding='utf-8-sig'))['goal'],
                  'git_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                  'git_status': subprocess.check_output(['git', 'status', '--short'], cwd=ROOT, text=True),
                  'recorded_usage_and_route': snapshot(), 'provider_terminal_billing_not_inferred': True}
        path = folder / ('recovery-' + uuid.uuid4().hex + '.json')
        path.write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8')
        worker.store.event('recovery_checkpoint', {'path': str(path), 'reason': reason, 'safe': safe})
        return path, record


def checkpoint(folder, stage, reason):
    return asyncio.run(capture(folder, stage, reason))
