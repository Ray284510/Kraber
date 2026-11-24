"""Local process-observation evidence; no process, Pipe, network or ledger access.

Construction/import never touch the filesystem. The authorized runner calls this
sink only after reaching its observer. Files are created exclusively and never
rewritten or deleted; an interrupted write remains available for diagnosis.
"""
import base64
from copy import deepcopy
import hashlib
import json
import math
import os
from pathlib import Path
import stat
import traceback
import uuid


NATIVE_ROOT = Path(__file__).resolve().parents[2] / 'logs/phase5/p13-process-observation-evidence'
_ERROR_ATTRIBUTES = (
    'returncode', 'stdout', 'stderr', 'output', 'errno', 'winerror', 'cmd',
    'timeout', 'encoding', 'object', 'start', 'end', 'reason', 'filename',
    'filename2', 'strerror', 'characters_written', 'funcname',
    'transport_receipt',
    'receipt_sink_error', 'process_evidence_result_error',
)
_CONSOLE_TYPES = frozenset((
    'BaseException', 'Exception', 'OSError', 'PermissionError', 'FileExistsError',
    'FileNotFoundError', 'NotADirectoryError', 'IsADirectoryError', 'TimeoutError',
    'TimeoutExpired', 'CalledProcessError', 'UnicodeDecodeError',
    'UnicodeEncodeError', 'ValueError', 'TypeError', 'RuntimeError',
    'ProcessObservationError', 'PeerProbeRejected', 'KeyboardInterrupt',
))


def _sha(data):
    return hashlib.sha256(data).hexdigest()


def _json_safe(value, active=None):
    """Retain supported values exactly; label unsupported types without repr()."""
    active = set() if active is None else active
    if value is None or type(value) in (bool, int, str):
        return value
    if type(value) is float:
        return value if math.isfinite(value) else {'type': 'nonfinite_float', 'value': str(value)}
    if isinstance(value, bytes):
        return {'type': 'bytes', 'base64': base64.b64encode(value).decode('ascii'),
                'byte_length': len(value), 'sha256': _sha(value)}
    identity = id(value)
    if identity in active:
        return {'type': 'cycle', 'class': type(value).__name__}
    active.add(identity)
    try:
        if isinstance(value, BaseException):
            return _error_details(value, active)
        if type(value) in (list, tuple):
            items = [_json_safe(item, active) for item in value]
            return items if type(value) is list else {'type': 'tuple', 'items': items}
        if type(value) is dict:
            if all(type(key) is str for key in value):
                return {key: _json_safe(item, active) for key, item in value.items()}
            return {'type': 'mapping', 'items': [
                [_json_safe(key, active), _json_safe(item, active)] for key, item in value.items()]}
        return {'type': 'unsupported', 'class': type(value).__name__,
                'module': type(value).__module__, 'serialized': False}
    finally:
        active.remove(identity)


def _error_details(error, active):
    result = {'type': type(error).__name__, 'module': type(error).__module__,
              'message': str(error), 'args': _json_safe(list(error.args), active)}
    # ExceptionGroup.args is not a replacement for a typed child tree. Keep
    # ordinary exceptions as explicit leaves and never collect frame locals.
    result['subexceptions'] = (
        [_json_safe(child, active) for child in error.exceptions]
        if isinstance(error, BaseExceptionGroup) else [])
    try:
        result['traceback_text'] = ''.join(traceback.format_exception(
            type(error), error, error.__traceback__, chain=False))
        result['traceback_status'] = 'CAPTURED'
    except Exception as formatting_error:
        result['traceback_text'] = None
        result['traceback_status'] = 'FORMAT_FAILED'
        result['traceback_error_type'] = type(formatting_error).__name__
    for name in _ERROR_ATTRIBUTES:
        try:
            value = getattr(error, name)
        except AttributeError:
            continue
        result[name] = _json_safe(value, active)
    if error.__cause__ is not None:
        result['cause'] = _json_safe(error.__cause__, active)
    if error.__context__ is not None and error.__context__ is not error.__cause__:
        result['context'] = _json_safe(error.__context__, active)
    result['suppress_context'] = error.__suppress_context__
    return result


def error_details(error):
    """Private JSON-safe exception evidence, including raw exception byte fields.

    Do not print this value: messages/args/commands/outputs may contain secrets.
    Only known exception attributes are traversed, never arbitrary __dict__.
    """
    if not isinstance(error, BaseException):
        raise TypeError('Exception evidence requires BaseException')
    return _json_safe(error)


def console_error_summary(error):
    """Safe console classification: no messages, args, commands, or raw output."""
    name = type(error).__name__
    result = {'type': name if name in _CONSOLE_TYPES else 'Exception'}
    for name in ('errno', 'winerror', 'returncode'):
        value = getattr(error, name, None)
        if type(value) is int:
            result[name] = value
    for name in ('stdout', 'stderr', 'output'):
        value = getattr(error, name, None)
        if isinstance(value, bytes):
            result[name + '_byte_length'] = len(value)
        elif isinstance(value, str):
            result[name + '_character_length'] = len(value)
    return result


def _checked_directory(path, *, create):
    """Reject aliases/reparse nodes before and after creating each directory."""
    if not path.is_absolute() or '..' in path.parts:
        raise ValueError('Absolute non-aliased evidence directory required')
    for node in reversed((path, *path.parents)):
        try:
            info = node.lstat()
        except FileNotFoundError:
            if not create:
                raise
            node.mkdir()  # Exclusive, no retries even on a concurrent collision.
            info = node.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Reparse or symbolic-link evidence directory rejected')
        if not stat.S_ISDIR(info.st_mode):
            raise NotADirectoryError('Evidence directory is not a directory')
        if os.path.normcase(str(node.resolve(strict=True))) != os.path.normcase(str(node)):
            raise ValueError('Evidence directory alias rejected')


def _write_new(path, data):
    with path.open('xb') as stream:
        written = stream.write(data)
        if written != len(data):
            raise OSError('Incomplete evidence write')
        stream.flush()
        os.fsync(stream.fileno())


def _json_bytes(value):
    # Escaping also preserves lone surrogate code points in exception/text data.
    return (json.dumps(value, ensure_ascii=True, allow_nan=False, indent=2) + '\n').encode('utf-8')


def _stream(value, *, provided, partial):
    data = None
    kind = 'not_provided' if not provided else 'none'
    encoding = None
    if isinstance(value, bytes):
        data, kind, encoding = value, 'bytes', 'binary'
    elif isinstance(value, str):
        kind, encoding = 'str', 'utf-8'
        try:
            data = value.encode('utf-8')
        except UnicodeEncodeError:
            data, encoding = value.encode('utf-8', errors='surrogatepass'), 'utf-8-surrogatepass'
    elif value is not None:
        raise TypeError('Process stream must be bytes, str or None')
    valid = None
    if data is not None:
        try:
            data.decode('utf-8', errors='strict')
            valid = True
        except UnicodeDecodeError:
            valid = False
    return data, {
        'source_kind': kind, 'raw_bytes': kind == 'bytes', 'encoding': encoding,
        'utf8_valid': valid, 'byte_length': None if data is None else len(data),
        'sha256': None if data is None else _sha(data), 'truncated': False,
        'capture_status': ('NOT_PROVIDED' if not provided else 'UNAVAILABLE')
        if data is None else ('PARTIAL_OR_UNKNOWN' if partial else 'CAPTURED'),
        'partial': None if data is None else partial,
    }


class ProcessEvidenceSink:
    """Single-observation private files with a content-free public reference.

    A custom root is exclusively for explicit synthetic/mock tests. The native
    runner supplies no root; it always uses NATIVE_ROOT. Append-only here means
    exclusive creation by this writer, not OS-enforced protection from the user.
    """
    def __init__(self, root=None, *, provenance='native_process_observation_after_explicit_authorization'):
        if root is not None and provenance != 'synthetic/mock':
            raise ValueError('Custom evidence root requires synthetic/mock provenance')
        self.root = NATIVE_ROOT if root is None else Path(root)
        self.provenance = provenance
        self._last_reference = None
        self._result_attempted = False

    @property
    def last_reference(self):
        return deepcopy(self._last_reference)

    def __call__(self, receipt):
        if type(receipt) is not dict:
            raise TypeError('Process observation receipt must be a dict')
        self._last_reference = None
        self._result_attempted = False
        _checked_directory(self.root, create=True)
        directory = self.root / ('observation-' + str(uuid.uuid4()))
        directory.mkdir()  # Exactly one UUID and mkdir; collisions propagate.
        self._last_reference = {
            'schema_version': 'p13-process-observation-reference-v1',
            'directory': str(directory), 'metadata_path': str(directory / 'metadata.json'),
            'complete': False, 'streams': {},
        }
        _checked_directory(directory, create=False)
        partial = (receipt.get('backend_error') is not None
                   or receipt.get('failure_stage') == 'backend' or receipt.get('partial') is True)
        streams = {}
        for name in ('stdout', 'stderr', 'output'):
            if name == 'output' and name not in receipt:
                continue
            data, details = _stream(receipt.get(name), provided=name in receipt, partial=partial)
            details['path'] = None if data is None else str(directory / (name + '.bin'))
            details['persisted'] = False
            streams[name] = details
            self._last_reference['streams'][name] = deepcopy(details)
            if data is not None:
                _checked_directory(directory, create=False)
                _write_new(Path(details['path']), data)
                details['persisted'] = True
                self._last_reference['streams'][name]['persisted'] = True
        metadata = {
            'schema_version': 'p13-process-observation-evidence-v1',
            'provenance': self.provenance, 'persistence_status': 'COMPLETE',
            'capture_status': 'PARTIAL_OR_UNKNOWN' if partial else 'AS_RECEIVED',
            'truncated': False, 'streams': streams,
            'receipt': _json_safe({key: value for key, value in receipt.items()
                                   if key not in ('stdout', 'stderr', 'output')}),
        }
        _checked_directory(directory, create=False)
        _write_new(directory / 'metadata.json', _json_bytes(metadata))
        self._last_reference['complete'] = True
        return self.last_reference

    def save_result(self, receipt):
        """Save the runner result once beside a complete observation; never repair."""
        if self._last_reference is None or not self._last_reference['complete']:
            raise RuntimeError('No complete process observation evidence for runner result')
        directory = Path(self._last_reference['directory'])
        path = directory / 'runner-result.json'
        if self._result_attempted:
            raise FileExistsError('Runner result evidence was already attempted')
        self._result_attempted = True
        self._last_reference['runner_result_path'] = str(path)
        self._last_reference['runner_result_complete'] = False
        _checked_directory(directory, create=False)
        _write_new(path, _json_bytes({
            'schema_version': 'p13-process-runner-result-v1',
            'provenance': self.provenance, 'receipt': _json_safe(receipt),
        }))
        self._last_reference['runner_result_complete'] = True
        return self.last_reference
