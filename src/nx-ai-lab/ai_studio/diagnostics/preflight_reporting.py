"""Offline error serialization only. No process, transport, store or file writes."""
from .process_evidence import error_details


def preflight_error_report(error, *, transport_receipt_references=(), reporting_error=None):
    """Private evidence: keep the original failure separate from reporting errors.

    References point at already captured evidence; this function never opens or
    reconstructs a receipt. Messages/tracebacks belong in evidence, not console.
    """
    refs = []
    for ref in transport_receipt_references:
        if (not isinstance(ref, dict) or set(ref) != {'path', 'sha256'}
                or not isinstance(ref['path'], str) or not ref['path']
                or not isinstance(ref['sha256'], str) or len(ref['sha256']) != 64
                or any(c not in '0123456789abcdef' for c in ref['sha256'])):
            raise ValueError('Expected existing receipt path and lowercase SHA-256 reference')
        refs.append(dict(ref))
    return {
        'schema': 'p13-preflight-error-evidence-v1',
        'primary_error': error_details(error),
        'reporting_error': error_details(reporting_error) if reporting_error is not None else None,
        'transport_receipt_references': refs,
        'retry_allowed': False,
    }


def preflight_console_summary(state):
    """Only scalar classifications/counts; no raw exception messages or args."""
    result = {}
    for key in ('state', 'process_observations', 'MCP_initialize_attempts', 'worker_audit'):
        value = state.get(key)
        if value is None or type(value) in (str, int, bool):
            result[key] = value
        else:
            result[key] = 'UNKNOWN_NONSCALAR'
    result['error_evidence_recorded'] = isinstance(state.get('error'), dict)
    result['retry_allowed'] = False
    return result
