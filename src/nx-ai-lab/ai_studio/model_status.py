"""Local routing evidence. Run with python -m ai_studio.model_status."""
import hashlib
import json
import sqlite3
import time
from contextlib import closing
from decimal import Decimal
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOG = ROOT / 'logs/phase5'
TASK = LOG / 'staged_20261108_212227'
EVIDENCE = LOG / 'model-router-20261109'
MODELS = {'gpt-6.1-sol': 'SOL', 'gpt-6-astra': 'ASTRA'}

def confined(path):
    path = path.resolve()
    if not path.is_relative_to(ROOT):
        raise ValueError('Evidence path escapes workspace')
    return path

def obj(raw):
    try:
        value = json.loads(raw or '{}')
    except (ValueError, TypeError):
        return {}
    return value if isinstance(value, dict) else {}

def read(path):
    path = confined(path)
    return obj(path.read_text(encoding='utf-8-sig')) if path.is_file() else {}

def db(path):
    return closing(sqlite3.connect(confined(path).as_uri() + '?mode=ro', uri=True))

def fields(value, keys):
    return {key: value[key] for key in keys.split() if key in value}

def tokens(target, usage):
    if isinstance(usage, dict):
        for key in ('input_tokens', 'output_tokens'):
            value = usage.get(key)
            if type(value) is int and value >= 0:
                target[key] = (target.get(key) or 0) + value

def api():
    buckets = {role: {} for role in ('SOL', 'ASTRA', 'unattributed')}
    with db(LOG / 'api-budget.sqlite') as ledger:
        ledger.execute('BEGIN')
        joined = ledger.execute("SELECT 1 FROM sqlite_master WHERE name='api_requests'").fetchone()
        tail = 'r.payload FROM spend s LEFT JOIN api_requests r ON r.id=s.id' if joined else 'NULL FROM spend s'
        for state, amount, usage, payload in ledger.execute('SELECT s.state,s.amount,s.usage,' + tail):
            row = buckets[MODELS.get(obj(payload).get('model'), 'unattributed')]
            key = {'estimated': 'estimated', 'reserved': 'reserved'}.get(state, 'other')
            row[key] = row.get(key, 0) + amount
            tokens(row, obj(usage))
    for row in buckets.values():
        for key in ('estimated', 'reserved', 'other'):
            row[key + '_usd'] = str(Decimal(row.pop(key, 0)) / 1_000_000)
    return buckets

def codex():
    buckets = {role: {'receipts': []} for role in ('SOL', 'ASTRA')}
    seen = set()
    paths = set(confined(EVIDENCE).rglob('events.jsonl'))
    paths.update(EVIDENCE.glob('codex-*.jsonl'))
    paths.update(LOG.glob('staged_*/subscription-route-*.jsonl'))
    for path in sorted(paths):
        path = confined(path)
        if not path.is_file():
            continue
        launch = read(path.with_name('launch.json'))
        route = launch.get('selected_route', launch)
        if launch.get('requested_model'):
            route={'model':launch['requested_model'],'role':MODELS.get(launch['requested_model']),'effort':launch.get('reasoning')}
        thread = None
        with path.open(encoding='utf-8-sig') as log:
            for line in log:
                event = obj(line)
                if event.get('type'): event={'kind':'stdout','value':event}
                value = event.get('value')
                if event.get('kind') == 'selected_route' and isinstance(value, dict):
                    route = value
                if event.get('kind') == 'stdout':
                    value = value if isinstance(value, dict) else obj(value)
                    if value.get('type') == 'thread.started':
                        thread = value.get('thread_id')
                    identity = fields(route, 'role model effort') if isinstance(route, dict) else {}
                    role = identity.get('role')
                    if role not in buckets or not identity.get('model') or value.get('type') != 'turn.completed':
                        continue
                    digest = hashlib.sha256(json.dumps([thread, identity, value], sort_keys=True).encode()).hexdigest()
                    if digest not in seen:
                        seen.add(digest)
                        buckets[role]['receipts'].append(dict(identity, sha256=digest))
                        tokens(buckets[role], value.get('usage'))
    return buckets

def snapshot():
    pointer = read(LOG / 'current-task.json')
    task_folder = confined(Path(pointer.get('folder', TASK)))
    if not task_folder.is_relative_to(LOG):
        raise ValueError('Current task outside phase5 logs')
    route_keys = 'provider role model effort reason manual'
    with db(task_folder / 'task.sqlite') as task:
        task.execute('BEGIN')
        selected = task.execute("SELECT value FROM task_meta WHERE name='model_route'").fetchone()
        route = fields(obj(selected[0]), route_keys) if selected else None
        history = []
        for n, event_time, kind, raw in task.execute(
                "SELECT n,time,kind,data FROM events WHERE kind IN ('model_route','api_route_checkpoint','codex_route_checkpoint') ORDER BY n"):
            data = obj(raw)
            history.append(dict(n=n, time=event_time, kind=kind, blocked=data.get('blocked'),
                                route=fields(data.get('route') or data, route_keys)))
    if history: route=history[-1]['route']
    config = read(ROOT / 'config/model-router.json')
    override = fields(config, route_keys) if config.get('mode') == 'manual' else {}
    discovery = read(EVIDENCE / 'codex-discovery-current.json')
    limits = (discovery.get('official_global_usage') or discovery.get('result') or discovery).get('rateLimits') or {}
    percent = (limits.get('primary') or {}).get('usedPercent')
    api_usage = api()
    capabilities = read(ROOT / 'logs/phase5/model-capabilities.json')
    capabilities['fresh'] = 0 <= time.time() - capabilities.get('at', 0) <= 300
    return dict(task=task_folder.name, selected_route=route, liveness='NOT ASSERTED', history=history,
                mode='manual' if override else 'auto', per_task_override=override,
                capabilities=capabilities, auto_switch_paused=config.get('auto_switch_paused', False),
                automatic_fallback='Controlled stage-budget fallback verified with official Codex; actual account quota depletion NOT VERIFIED', manual_controls='CLI/desktop controls apply at next safe stage',
                usage={'API SOL': api_usage.pop('SOL'), 'API ASTRA': api_usage.pop('ASTRA'),
                       **{'Codex ' + role: row for role, row in codex().items()}},
                api_unattributed=api_usage['unattributed'], codex_account_global_used_percent=percent, codex_usage_observed_at=discovery.get('at'),
                scope='Local recorded usage; subscription percentage is account-global, not per model')

if __name__ == '__main__':
    print(json.dumps(snapshot(), indent=2, ensure_ascii=False))
