"""Durable command identities and explicit unknown outcomes; no automatic mutation retry."""
import datetime
import os
from contextlib import closing
from .transport_outcome import READ_ONLY, uncertain
import hashlib
import json
import sqlite3
from pathlib import Path


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def canonical(value):
    return json.dumps(value,ensure_ascii=False,sort_keys=True,separators=(',',':'))


class OutcomeUnknown(RuntimeError): pass


class TaskStore:
    def __init__(self, folder, *, execution_root=None):
        self.folder=Path(folder);self.folder.mkdir(parents=True,exist_ok=True)
        self.execution_root = Path(execution_root) if execution_root is not None else self.folder
        db_path=self.folder/'task.sqlite'
        if not db_path.exists() and ((self.folder/'receipts').is_dir() or (self.folder/'checkpoint.json').exists()):
            raise OutcomeUnknown('Task history exists but its database is missing')
        self.db=sqlite3.connect(db_path,timeout=10)
        self.db.row_factory=sqlite3.Row
        self.db.executescript('''
        PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
        CREATE TABLE IF NOT EXISTS operations (
            id TEXT PRIMARY KEY, payload TEXT NOT NULL, digest TEXT NOT NULL,
            kind TEXT NOT NULL, state TEXT NOT NULL, provider TEXT NOT NULL,
            created TEXT NOT NULL, updated TEXT NOT NULL, result TEXT);
        CREATE TABLE IF NOT EXISTS events (
            n INTEGER PRIMARY KEY, time TEXT NOT NULL, kind TEXT NOT NULL, data TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS task_meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
        ''')

    def event(self,kind,data):
        with self.db:self.db.execute('INSERT INTO events(time,kind,data) VALUES(?,?,?)',(now(),kind,canonical(data)))

    def cached_result(self, operation_id, payload):
        row = self.db.execute('SELECT digest,state,result FROM operations WHERE id=?', (operation_id,)).fetchone()
        if row is None:
            return None
        if row['digest'] != hashlib.sha256(canonical(payload).encode()).hexdigest():
            raise ValueError('Operation ID reused with different arguments')
        if row['state'] not in ('succeeded','failed'):
            raise OutcomeUnknown('Prior operation outcome is unresolved')
        self.event('duplicate_not_executed', {'id':operation_id,'state':row['state']})
        return json.loads(row['result'])

    def claim(self,operation_id,payload,kind,provider):
        encoded=canonical(payload);digest=hashlib.sha256(encoded.encode()).hexdigest()
        self.db.execute('BEGIN IMMEDIATE')
        try:
            row=self.db.execute('SELECT * FROM operations WHERE id=?',(operation_id,)).fetchone()
            if row:
                if row['digest']!=digest:raise ValueError('Operation ID reused with different arguments')
                if row['state'] in ('succeeded','failed'):
                    self.db.commit();self.event('duplicate_not_executed',{'id':operation_id,'state':row['state']})
                    return json.loads(row['result'])
                raise OutcomeUnknown('Operation is pending or outcome unknown; reconcile before any resend: '+operation_id)
            active=self.db.execute("SELECT id FROM operations WHERE state IN ('running','unknown')").fetchall()
            if active:raise OutcomeUnknown('Unresolved operation blocks new commands: '+str([r['id'] for r in active]))
            self.db.execute('INSERT INTO operations VALUES(?,?,?,?,?,?,?,?,?)',
                            (operation_id,encoded,digest,kind,'running',provider,now(),now(),None))
            self.db.commit();return None
        except BaseException:
            self.db.rollback();raise

    def complete(self,operation_id,result):
        row=self.db.execute('SELECT kind,payload FROM operations WHERE id=?',(operation_id,)).fetchone()
        if row and row['kind']=='nx_command' and json.loads(row['payload'])['command'].split()[0] not in READ_ONLY and uncertain(result):
            self.unknown(operation_id,'UNKNOWN_AFTER_WRITE')
            raise OutcomeUnknown('Ambiguous mutation cannot be recorded as terminal')
        state='succeeded' if result.get('ok') else 'failed'
        with self.db:
            cur=self.db.execute("UPDATE operations SET state=?,updated=?,result=? WHERE id=? AND state='running'",
                                (state,now(),canonical(result),operation_id))
            if cur.rowcount!=1:raise RuntimeError('Operation is not running')

    def unknown(self,operation_id,error_type):
        with self.db:self.db.execute("UPDATE operations SET state='unknown',updated=? WHERE id=? AND state='running'",(now(),operation_id))
        self.event('unknown_outcome',{'id':operation_id,'error_type':error_type,'automatic_retry':False})

    def reconcile_receipt(self,operation_id,receipt):
        row=self.db.execute('SELECT * FROM operations WHERE id=?',(operation_id,)).fetchone()
        if not row or row['state'] not in ('running','unknown'):raise ValueError('No uncertain operation')
        if receipt['operation_id']!=operation_id or receipt['payload_digest']!=row['digest']:
            raise ValueError('Receipt does not match operation')
        if row['kind']=='nx_command' and json.loads(row['payload'])['command'].split()[0] not in READ_ONLY:
            path=self.folder/'receipts'/(hashlib.sha256(operation_id.encode()).hexdigest()+'.json')
            raw=path.read_bytes()
            key=(str(self.folder.resolve()),operation_id,row['digest'])
            MutationDomain(self.execution_root).checked_response(key,raw)
            if json.loads(raw)!=receipt:
                raise OutcomeUnknown('Reconciliation differs from sealed original receipt')
        # Only local durable MCP receipt files produced before an interrupted DB commit.
        # No receipt means human/agent inspection is required; never infer success from a filename.
        result=receipt['response']
        with self.db:self.db.execute('UPDATE operations SET state=?,result=?,updated=? WHERE id=?',
                                    ('succeeded' if result.get('ok') else 'failed',canonical(result),now(),operation_id))
        self.event('receipt_reconciliation',{'id':operation_id,'source':'Actual persisted MCP response','resent':False})

    def switch_provider(self,provider,nx_session):
        if provider not in ('codex','openai_dsh'):raise ValueError('Unsupported provider')
        if self.db.execute("SELECT 1 FROM operations WHERE state IN ('running','unknown')").fetchone():
            raise OutcomeUnknown('Cannot switch provider while NX outcome is unresolved')
        if not nx_session.get('ok') or not nx_session.get('all_modified_flags_known'):
            raise ValueError('Fresh actual NX session state required')
        with self.db:self.db.execute('INSERT OR REPLACE INTO task_meta VALUES(?,?)',('provider',provider))
        self.event('provider_selected',{'provider':provider,'nx_session':nx_session,
                                      'codex_billing':'Subscription usage unknown; not API tokens' if provider=='codex' else None})

    def checkpoint(self,summary,model_paths):
        rows=[dict(r) for r in self.db.execute('SELECT id,state,provider,kind,updated FROM operations ORDER BY created')]
        value={'time':now(),'summary':summary,'model_paths':model_paths,'operations':rows,
               'unresolved':[r['id'] for r in rows if r['state'] in ('running','unknown')]}
        self.event('checkpoint',value)
        temp=self.folder/'checkpoint.tmp';temp.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8');temp.replace(self.folder/'checkpoint.json')
        return value


class MutationDomain:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.directory = self.root / 'nx-mutation-domain-v1'
        self.file = self.directory / 'state.sqlite'

    def _open(self):
        # mkdir is exclusive. An existing empty/partial directory is evidence
        # of interrupted initialization. Never create a replacement database.
        try:
            self.directory.mkdir()
        except FileExistsError:
            return sqlite3.connect(self.file.as_uri() + '?mode=rw', uri=True)
        with closing(sqlite3.connect(self.file)) as db:
            with db:
                db.execute('CREATE TABLE migration (completed INTEGER NOT NULL CHECK(completed IN (0,1)))')
                db.execute('INSERT INTO migration VALUES (0)')
                db.execute('CREATE TABLE reservation (slot INTEGER PRIMARY KEY CHECK(slot=1), owner TEXT NOT NULL, op TEXT NOT NULL, digest TEXT NOT NULL, receipt_sha TEXT)')
        return sqlite3.connect(self.file.as_uri() + '?mode=rw', uri=True)

    def _check_legacy_tasks(self):
        # Inspect only the exact task database name; missing specs cannot hide an unresolved operation.
        def inaccessible(exc):
            raise exc
        for directory, folders, files in os.walk(self.root, onerror=inaccessible):
            if 'task.sqlite' not in files:
                if 'task_spec.json' in files and ('receipts' in folders or 'checkpoint.json' in files):
                    raise OutcomeUnknown('Task history exists but its database is missing: '+directory)
                continue
            path = Path(directory) / 'task.sqlite'
            with closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True)) as db:
                if db.execute("SELECT 1 FROM task_meta WHERE name='mutation_domain_enrolled'").fetchone():
                    raise OutcomeUnknown('Task enrollment predates shared domain; missing execution history')
                if db.execute("SELECT 1 FROM operations WHERE state NOT IN ('failed','succeeded') OR state IS NULL LIMIT 1").fetchone():
                    raise OutcomeUnknown('Unresolved historical task in shared execution domain: ' + str(path))
                # Earlier adapters labeled any ValueError as a validation failure.
                # A historical 'failed' row alone cannot authorize a repair.
                rows=db.execute("SELECT id,payload,result FROM operations WHERE state='failed' AND kind='nx_command'")
                for op,payload,result in rows:
                    if json.loads(payload)['command'].split()[0] not in READ_ONLY and uncertain(json.loads(result)):
                        raise OutcomeUnknown('Historical mutation lacks definitive failure evidence: '+str(path)+' '+op)

    def begin(self, key, store=None, payload=None):
        with closing(self._open()) as db, db:
            db.execute('BEGIN IMMEDIATE')
            migrated=db.execute('SELECT completed FROM migration').fetchall()
            if migrated==[(0,)]:
                self._check_legacy_tasks()
                db.execute('UPDATE migration SET completed=1')
            elif migrated!=[(1,)]:
                raise OutcomeUnknown('Unverifiable shared execution domain')
            if db.execute('SELECT 1 FROM reservation').fetchone():
                raise OutcomeUnknown('Shared execution domain has an unresolved mutation')
            if store is not None:
                prior=store.cached_result(key[1],payload)
                if prior is not None:
                    return prior
                if store.db.execute("SELECT 1 FROM operations WHERE state NOT IN ('succeeded','failed') LIMIT 1").fetchone():
                    raise OutcomeUnknown('Task already has an unresolved operation')
            db.execute('INSERT INTO reservation VALUES (1,?,?,?,NULL)', key)

    def seal(self, key, raw):
        sha = hashlib.sha256(raw).hexdigest()
        with closing(self._open()) as db, db:
            row = db.execute('UPDATE reservation SET receipt_sha=? WHERE owner=? AND op=? AND digest=? AND receipt_sha IS NULL', (sha, *key))
            if row.rowcount != 1:
                raise OutcomeUnknown('Shared reservation no longer matches receipt owner')

    def checked_response(self, key, raw):
        with closing(self._open()) as db:
            row = db.execute('SELECT receipt_sha FROM reservation WHERE owner=? AND op=? AND digest=?', key).fetchone()
        if not row or row[0] != hashlib.sha256(raw).hexdigest():
            raise OutcomeUnknown('Original receipt missing, unsealed, or hash mismatched')
        receipt = json.loads(raw)
        if ((receipt['task'], receipt['operation_id'], receipt['payload_digest']) != key
                or uncertain(receipt['response'])):
            raise OutcomeUnknown('Receipt does not establish a definitive operation outcome')
        return receipt['response']

    def settle(self, key, raw, store):
        response = self.checked_response(key, raw)
        row = store.db.execute('SELECT digest,state,result FROM operations WHERE id=?', (key[1],)).fetchone()
        terminal = 'succeeded' if response['ok'] else 'failed'
        if (str(store.folder.resolve()) != key[0] or row is None or tuple(row) !=
                (key[2], terminal, canonical(response))):
            raise OutcomeUnknown('Task reconciliation does not match sealed receipt')
        with closing(self._open()) as db, db:
            removed = db.execute('DELETE FROM reservation WHERE owner=? AND op=? AND digest=? AND receipt_sha=?',
                                 (*key, hashlib.sha256(raw).hexdigest()))
            if removed.rowcount != 1:
                raise OutcomeUnknown('Concurrent settlement or missing reservation')

    def pending(self):
        with closing(self._open()) as db:
            return db.execute('SELECT owner,op,digest FROM reservation WHERE slot=1').fetchone()

