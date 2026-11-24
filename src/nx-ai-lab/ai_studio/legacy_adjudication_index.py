"""Disposable index over an exact legacy snapshot. Never opens an existing destination."""
from contextlib import closing, contextmanager
from datetime import datetime, timezone
from pathlib import Path
import sqlite3
import tempfile

from .legacy_adjudication import Verdict, Rejected, _verified_row, digest, require


def _hash_column(name):
    return f"{name} TEXT NOT NULL CHECK(length({name})=64 AND {name} NOT GLOB '*[^0-9a-f]*')"


_SCHEMA = f"""CREATE TABLE legacy_adjudication_overlay (
    decision_id TEXT NOT NULL PRIMARY KEY CHECK(length(trim(decision_id))>0),
    task_id TEXT NOT NULL CHECK(length(trim(task_id))>0),
    operation_id TEXT NOT NULL CHECK(operation_id IN ('base.bore','fault.conflicting_seat')),
    classification TEXT NOT NULL,
    {_hash_column("evidence_binding_sha256")},
    {_hash_column("decision_record_sha256")},
    replay_allowed INTEGER NOT NULL CHECK(replay_allowed=0),
    production_release INTEGER NOT NULL CHECK(production_release=0),
    created_utc TEXT NOT NULL,
    UNIQUE(task_id, operation_id)
)"""


class _Index:
    def __init__(self, connection, path, source_hash):
        self._db = connection
        self.path = path
        self._source_hash = source_hash
        self._data_version = connection.execute("PRAGMA data_version").fetchone()[0]

    def import_batch(self, requests):
        """Each pair is (Material, detached synthetic credential or None). Reject duplicates."""
        try:
            with self._db:
                self._db.execute("BEGIN IMMEDIATE")
                require(self._db.execute("PRAGMA data_version").fetchone()[0] == self._data_version)
                if self._db.execute("SELECT 1 FROM operations WHERE state IS NULL OR state NOT IN ('failed','succeeded') LIMIT 1").fetchone():
                    raise Rejected(Verdict.UNKNOWN)
                inserted = 0
                for material, issuance in requests:
                    require(digest(material.source_db) == self._source_hash)
                    row = _verified_row(material, issuance)
                    self._db.execute("INSERT INTO legacy_adjudication_overlay VALUES (?,?,?,?,?,?,0,0,?)",
                                     (*row, datetime.now(timezone.utc).isoformat()))
                    inserted += 1
                require(inserted > 0, Verdict.MATERIAL_INCOMPLETE)
            return Verdict.VERIFIED_CASE
        except Rejected as error:
            return error.status
        except KeyError:
            return Verdict.MATERIAL_INCOMPLETE
        except (sqlite3.IntegrityError, ValueError, TypeError, AttributeError, RecursionError, OverflowError):
            return Verdict.EVIDENCE_MISMATCH
        except (OSError, sqlite3.Error):
            return Verdict.UNKNOWN


@contextmanager
def isolated_index(source_db):
    """Create and discard a private clone. No caller-selected database or output path."""
    if not isinstance(source_db, bytes) or not source_db:
        raise ValueError("Expected frozen source database bytes")
    with tempfile.TemporaryDirectory(prefix="p13-adjudication-") as tmp:
        path = Path(tmp) / "rehearsal.sqlite"
        with path.open("xb") as stream:
            stream.write(source_db)
        with closing(sqlite3.connect(path)) as db:
            db.execute("PRAGMA trusted_schema=OFF")
            if db.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
                raise ValueError("Unreadable legacy snapshot")
            tables = set(db.execute("SELECT type,name FROM sqlite_schema WHERE type!='index'"))
            if tables != {("table", n) for n in ("operations", "events", "task_meta")}:
                raise ValueError("Expected a pristine legacy snapshot without triggers, views or overlay")
            db.execute(_SCHEMA)
            yield _Index(db, path, digest(source_db))
