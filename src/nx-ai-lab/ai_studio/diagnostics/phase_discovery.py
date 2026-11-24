"""Deterministic, read-only discovery of one P13 launch journal.

No launch/pipe/worker APIs. Public entrypoints always require the fixed root.
Filesystem reads are snapshots, not a Windows atomic namespace transaction.
"""
from collections import Counter
import hashlib
import json
import os
from pathlib import Path, PureWindowsPath
import stat
import uuid

from .launch_journal import verify_phase_journal

JOURNAL_ROOT = str(Path(__file__).resolve().parents[2] / 'logs/phase5/p13-strict-launch')
PHASE_FILES = (
    ('PREPARED_VALIDATED', 'attempt'),
    ('START_INTENT_DURABLE', 'START_INTENT_DURABLE'),
    ('START_RETURNED_PID', 'START_RETURNED_PID'),
    ('START_OUTCOME_UNKNOWN', 'START_OUTCOME_UNKNOWN'),
    ('GUARD_RELEASED', 'GUARD_RELEASED'),
    ('GUARD_RELEASE_UNKNOWN', 'GUARD_RELEASE_UNKNOWN'),
)
MAX_RECEIPT_BYTES = 16 * 1024 * 1024
METADATA_CONTRACT = 'p13-windows-stat-contract-v2'
# Windows stdlib documents (st_dev, st_ino) as file identity, st_ino being the
# file index. Type/link/attribute checks remain additional requirements.
IDENTITY_FIELDS = ('device', 'inode', 'mode', 'nlink', 'attributes', 'reparse', 'symlink')
CONTENT_METADATA_FIELDS = ('size', 'mtime_ns')
CROSS_API_FIELDS = IDENTITY_FIELDS + CONTENT_METADATA_FIELDS
SAME_API_FIELDS = CROSS_API_FIELDS + ('ctime_ns',)


def _compare_facts(left, right, fields, relation):
    """Exact comparisons only. Keep the differing values as audit evidence."""
    differences = {k: {'left': left.get(k), 'right': right.get(k)}
                   for k in fields if left.get(k) != right.get(k)}
    return dict(relation=relation, fields=list(fields), differences=differences,
                status='MATCH' if not differences else 'MISMATCH')


def _facts(s):
    attributes = getattr(s, 'st_file_attributes', None)
    return dict(mode=s.st_mode, device=s.st_dev, inode=s.st_ino,
                nlink=s.st_nlink, size=s.st_size, mtime_ns=s.st_mtime_ns,
                ctime_ns=s.st_ctime_ns, attributes=attributes,
                reparse=None if attributes is None else bool(attributes & 0x400),
                symlink=stat.S_ISLNK(s.st_mode))


class _FileReader:
    """Standard-library read-only filesystem adapter; never instantiated in mocks."""
    def __init__(self):
        if os.name != 'nt':
            raise OSError('Windows path metadata required')

    def lstat(self, path):
        return _facts(os.lstat(path))

    def fstat(self, handle):
        return _facts(os.fstat(handle.fileno()))

    def list_names(self, directory):
        with os.scandir(directory) as entries:
            return [entry.name for entry in entries]

    def open_read(self, path):
        return open(path, 'rb')


def _reader_factory():
    return _FileReader()


def _check_facts(facts, *, directory=False):
    required = ('mode', 'device', 'inode', 'nlink', 'size', 'mtime_ns', 'ctime_ns', 'attributes')
    if any(type(facts.get(k)) is not int for k in required):
        raise ValueError('PATH_METADATA_UNKNOWN')
    if facts.get('reparse') is not False or facts.get('symlink') is not False:
        raise ValueError('REPARSE_OR_SYMLINK_OR_UNKNOWN')
    if facts['inode'] <= 0 or facts['nlink'] < 1:
        raise ValueError('PATH_IDENTITY_UNKNOWN')
    if directory:
        if not stat.S_ISDIR(facts['mode']):
            raise ValueError('EXPECTED_DIRECTORY')
    elif not stat.S_ISREG(facts['mode']):
        raise ValueError('EXPECTED_REGULAR_FILE')
    elif facts['nlink'] != 1:
        raise ValueError('HARDLINK_ALIAS_REJECTED')


def _parent_snapshot(reader, result):
    p = PureWindowsPath(JOURNAL_ROOT)
    paths = [str(x) for x in reversed(p.parents)] + [str(p)]
    for path in paths:
        row = {'path': path, 'facts': None, 'rejections': []}
        result.append(row)
        try:
            row['facts'] = reader.lstat(path)
            _check_facts(row['facts'], directory=True)
        except (OSError, ValueError, TypeError, KeyError) as error:
            row['rejections'].append(str(error) if isinstance(error, ValueError) else type(error).__name__)
            raise
    return result


def _checked_names(reader):
    names = reader.list_names(JOURNAL_ROOT)
    if not isinstance(names, list) or any(not isinstance(n, str) or not n or '/' in n or '\\' in n for n in names):
        raise ValueError('NAMESPACE_ENTRY_UNKNOWN')
    return names


def _belongs_to_attempt(name, attempt):
    # Retain noncanonical spellings, unknown suffixes and prefix variants as
    # rejected evidence; other canonical attempts are not opened or hashed.
    if name.casefold().startswith(attempt.casefold()):
        return True
    stem = name.split('.', 1)[0]
    try:
        return str(uuid.UUID(stem)) == attempt
    except (ValueError, AttributeError):
        return False


def _json_unique(text):
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError('DUPLICATE_JSON_KEY')
            value[key] = item
        return value
    return json.loads(text, object_pairs_hook=unique)


def _read_candidate(reader, row):
    """Hash each safe candidate twice with handle/path identity checks."""
    path = row['path']
    row['metadata_contract'] = METADATA_CONTRACT
    row['comparison_results'] = []
    try:
        before = reader.lstat(path)
        row['before'] = before
        row['bytes'] = before.get('size')
        _check_facts(before)
        if not 0 <= before['size'] <= MAX_RECEIPT_BYTES:
            raise ValueError('RECEIPT_SIZE_OUTSIDE_READ_BOUND')
        with reader.open_read(path) as handle:
            opened = reader.fstat(handle)
            row['opened'] = opened
            _check_facts(opened)
            # Observed Windows lstat/fstat ctime values do not have an assured
            # common meaning. Retain both; do NOT relax same-API comparisons.
            row['cross_api_ctime'] = dict(status='CROSS_API_CTIME_NOT_COMPARABLE',
                path_value=before['ctime_ns'], handle_value=opened['ctime_ns'])
            cross = _compare_facts(before, opened, CROSS_API_FIELDS, 'lstat_before_to_fstat_opened')
            row['comparison_results'].append(cross)
            if cross['status'] != 'MATCH':
                raise ValueError('OPEN_IDENTITY_OR_METADATA_CHANGED')
            data = handle.read(MAX_RECEIPT_BYTES + 1)
            row['sha256'] = hashlib.sha256(data).hexdigest()
            row['read_bytes'] = len(data)
            middle = reader.fstat(handle)
            row['middle'] = middle
            _check_facts(middle)
            handle.seek(0)
            second = handle.read(MAX_RECEIPT_BYTES + 1)
            row['verification_sha256'] = hashlib.sha256(second).hexdigest()
            row['verification_read_bytes'] = len(second)
            after_handle = reader.fstat(handle)
            row['after_handle'] = after_handle
            _check_facts(after_handle)
        after_path = reader.lstat(path)
        row['after'] = after_path
        _check_facts(after_path)
        for left, right, relation in (
                (opened, middle, 'fstat_opened_to_middle'),
                (opened, after_handle, 'fstat_opened_to_after'),
                (before, after_path, 'lstat_before_to_after')):
            row['comparison_results'].append(_compare_facts(left, right, SAME_API_FIELDS, relation))
        row['content_comparison'] = dict(
            first_length_matches=len(data) == before['size'],
            second_length_matches=len(second) == before['size'],
            bytes_equal=data == second,
            hashes_equal=row['sha256'] == row['verification_sha256'])
        if (any(c['status'] != 'MATCH' for c in row['comparison_results'])
                or not all(row['content_comparison'].values())):
            raise ValueError('FILE_CHANGED_DURING_READ')
        row['stable_read'] = True
        return data
    except (OSError, ValueError, TypeError, KeyError) as error:
        row['rejections'].append(str(error) if isinstance(error, ValueError) else type(error).__name__)
        row['read_error'] = {'type': type(error).__name__, 'errno': getattr(error, 'errno', None)}
        return None


def discover_attempt_phase_files(directory, attempt_uuid, *, read_legacy_summary=True):
    """Discover exact filenames in protocol order. Never mutate or infer no-start.

    Rejects aliases rather than canonicalizing caller input. Safe rejected regular
    files are hashed; unsafe/unknown paths are NOT opened and carry a reason.
    read_legacy_summary=False retains only the non-authoritative legacy file's
    metadata, so a bounded audit opens only the phase payloads. The default
    preserves the historical hash-only handling of legacy summaries.
    """
    manifest = dict(schema='p13-phase-discovery-v1', directory=str(directory), attempt_uuid=attempt_uuid,
                    status='UNKNOWN', expected_phases=[], candidates=[], phase_paths=[], envelopes=[],
                    rejections=[], replay_allowed=False, repair_performed=False, legacy_result_used=False,
                    start_outcome='UNKNOWN_START_OUTCOME', ordering='FIXED_PROTOCOL_NOT_GLOB_OR_MTIME',
                    max_receipt_bytes=MAX_RECEIPT_BYTES, metadata_contract=METADATA_CONTRACT,
                    read_legacy_summary=read_legacy_summary)
    try:
        if type(read_legacy_summary) is not bool:
            raise ValueError('EXPLICIT_LEGACY_READ_POLICY_REQUIRED')
        if not isinstance(attempt_uuid, str) or str(uuid.UUID(attempt_uuid)) != attempt_uuid:
            raise ValueError('CANONICAL_UUID_REQUIRED')
        names = {f'{attempt_uuid}.{suffix}.json': phase for phase, suffix in PHASE_FILES}
        legacy = f'{attempt_uuid}.result.json'
        for name, phase in names.items():
            manifest['expected_phases'].append(dict(phase=phase, path=str(PureWindowsPath(JOURNAL_ROOT)/name),
                presence='UNKNOWN', bytes=None, sha256=None, candidate_indices=[], rejections=[]))
        if str(directory) != JOURNAL_ROOT:
            raise ValueError('FIXED_EXACT_ROOT_REQUIRED_NO_ALIASES')
        reader = _reader_factory()
        manifest['parent_before'] = []
        _parent_snapshot(reader, manifest['parent_before'])
        all_names = _checked_names(reader)
        observed = [n for n in all_names if _belongs_to_attempt(n, attempt_uuid)]
        manifest['namespace_before'] = observed
        manifest['unrelated_name_count'] = len(all_names) - len(observed)
        counts = Counter(observed)
        for name, count in counts.items():
            if count > 1:
                manifest['rejections'].append('DUPLICATE_NAMESPACE_ENTRY:' + name)
        # Enumeration order is evidence only. Fixed protocol controls every phase.
        ordered = [name for name in names for _ in range(counts[name])]
        ordered += [n for n in observed if n not in names]
        expected = {row['path']: row for row in manifest['expected_phases']}
        seen_ids = {}
        for name in ordered:
            path = str(PureWindowsPath(JOURNAL_ROOT)/name)
            role = 'phase' if name in names else 'legacy_summary' if name == legacy else 'rejected_name'
            row = dict(name=name, path=path, role=role, bytes=None, sha256=None, stable_read=False, rejections=[])
            manifest['candidates'].append(row)
            if role == 'rejected_name':
                row['rejections'].append('UNKNOWN_SUFFIX_OR_NONCANONICAL_NAME')
            if any(c in name for c in ':<>"|?*') or name.endswith(('.', ' ')):
                row['rejections'].append('WINDOWS_NAME_ALIAS_UNSAFE_NOT_OPENED')
                data = None
            elif role == 'legacy_summary' and not read_legacy_summary:
                # A bounded phase-only audit must not open a fifth data file.
                # Presence/metadata remain evidence, never authoritative state.
                data = None
                row['read_status'] = 'LEGACY_BYTES_NOT_OPENED'
                try:
                    row['before'] = reader.lstat(path)
                    row['bytes'] = row['before'].get('size')
                    _check_facts(row['before'])
                    row['metadata_only'] = True
                except (OSError, ValueError, TypeError, KeyError) as error:
                    row['rejections'].append(str(error) if isinstance(error, ValueError) else type(error).__name__)
            else:
                data = _read_candidate(reader, row)
            if data is not None:
                file_id = (row['before']['device'], row['before']['inode'])
                if file_id in seen_ids:
                    row['rejections'].append('DUPLICATE_FILE_ID_OR_PATH_ALIAS:' + seen_ids[file_id])
                seen_ids[file_id] = name
            if role == 'phase':
                target = expected[path]
                target.update(presence='PRESENT', bytes=row['bytes'], sha256=row['sha256'])
                target['candidate_indices'].append(len(manifest['candidates']) - 1)
                manifest['phase_paths'].append(path)
                if data is not None:
                    try:
                        envelope = _json_unique(data.decode('utf-8-sig'))
                        payload = _json_unique(envelope['payload_json'])
                        if payload['attempt_uuid'] != attempt_uuid:
                            raise ValueError('PAYLOAD_UUID_MISMATCH')
                        if payload['phase'] != names[name]:
                            raise ValueError('FILENAME_PHASE_MISMATCH')
                        manifest['envelopes'].append(envelope)
                    except (ValueError, KeyError, TypeError, UnicodeError) as error:
                        row['rejections'].append(str(error) if type(error) is ValueError else type(error).__name__)
                target['rejections'].extend(row['rejections'])
            # Legacy bytes are retained only as a hash, never parsed as state.
            manifest['rejections'].extend(name + ':' + r for r in row['rejections'])
        for (name, _), row in zip(names.items(), manifest['expected_phases']):
            if not counts[name]:
                row['presence'] = 'MISSING'
                row['rejections'].append('NOT_PRESENT_AT_SNAPSHOT')
        presence = {phase: counts[name] > 0 for name, phase in names.items()}
        for a,b in [('START_RETURNED_PID','START_OUTCOME_UNKNOWN'),('GUARD_RELEASED','GUARD_RELEASE_UNKNOWN')]:
            if presence[a] and presence[b]:
                manifest['rejections'].append('CONFLICTING_PHASES:' + a + '+' + b)
        if not presence['PREPARED_VALIDATED'] or not presence['START_INTENT_DURABLE']:
            manifest['rejections'].append('MISSING_PREPARED_OR_INTENT')
        if not (presence['START_RETURNED_PID'] or presence['START_OUTCOME_UNKNOWN']):
            manifest['rejections'].append('MISSING_START_OUTCOME')
        if not (presence['GUARD_RELEASED'] or presence['GUARD_RELEASE_UNKNOWN']):
            manifest['rejections'].append('MISSING_GUARD_OUTCOME')
        # Detect replacement/append during discovery, without any retry or repair.
        manifest['namespace_after'] = [n for n in _checked_names(reader) if _belongs_to_attempt(n, attempt_uuid)]
        if Counter(manifest['namespace_after']) != counts:
            manifest['rejections'].append('ATTEMPT_NAMESPACE_CHANGED')
        for row in manifest['candidates']:
            if row['stable_read'] or row.get('metadata_only'):
                try:
                    row['final_metadata'] = reader.lstat(row['path'])
                    _check_facts(row['final_metadata'])
                    final = _compare_facts(row['before'], row['final_metadata'], SAME_API_FIELDS,
                                           'lstat_before_to_discovery_end')
                    row.setdefault('comparison_results', []).append(final)
                    if final['status'] != 'MATCH':
                        raise ValueError('FILE_CHANGED_BEFORE_DISCOVERY_COMPLETED')
                except (OSError, ValueError, TypeError) as error:
                    reason = str(error) if isinstance(error, ValueError) else type(error).__name__
                    row['rejections'].append(reason)
                    manifest['rejections'].append(row['name'] + ':' + reason)
                    if row['path'] in expected:
                        expected[row['path']]['rejections'].append(reason)
        manifest['parent_after'] = []
        _parent_snapshot(reader, manifest['parent_after'])
        if manifest['parent_before'] != manifest['parent_after']:
            manifest['rejections'].append('PARENT_PATH_CHANGED')
        if not manifest['rejections']:
            manifest['status'] = 'DISCOVERED'
    except (OSError, ValueError, TypeError, KeyError) as error:
        manifest['rejections'].append(str(error) if isinstance(error, ValueError) else type(error).__name__)
    return manifest


def audit_attempt_journal(directory, attempt_uuid, *, read_legacy_summary=True):
    """One discovery snapshot followed by pure validation of the captured bytes.

    This verifies recorded events, never NX readiness or permission to replay.
    """
    discovery = discover_attempt_phase_files(directory, attempt_uuid, read_legacy_summary=read_legacy_summary)
    result = dict(schema='p13-attempt-audit-v1', status='UNKNOWN', discovery_status=discovery['status'],
                  discovery=discovery, phase_chain_status='NOT_EVALUATED_DISCOVERY_UNKNOWN', phase_chain=None,
                  start_outcome='UNKNOWN_START_OUTCOME', guard_outcome='UNKNOWN', replay_allowed=False,
                  legacy_result_used=False, repair_performed=False, native_readiness_proven=False)
    if discovery['status'] != 'DISCOVERED':
        return result
    chain = verify_phase_journal(discovery['envelopes'])
    result.update(status=chain['status'], phase_chain=chain,
                  phase_chain_status='VALID' if chain['phase_chain_valid'] and chain['complete'] else 'INVALID_OR_INCOMPLETE',
                  start_outcome=chain['start_outcome'], guard_outcome=chain['guard_outcome'])
    return result


if __name__ == '__main__':
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('attempt_uuid')
    parser.add_argument('--directory', default=JOURNAL_ROOT)
    args = parser.parse_args()
    report = audit_attempt_journal(args.directory, args.attempt_uuid)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(0 if report['status'] == 'RECORDED_START_RETURNED_PID' else 2)
