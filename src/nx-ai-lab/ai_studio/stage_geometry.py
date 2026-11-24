"""Stage-adapter receipt policy. Pure validation, not a native NX verifier."""
import math


def inspect_geometry(result, expectation, *, stage_id):
    """Fail closed; empty permission is an operational capability, never a pass.

    expected_body_kinds, min/max count and additional mandatory receipt fields
    belong to the stage spec. Baseline evidence fields cannot be relaxed by it.
    """
    def verdict(status, reason):
        return dict(status=status, geometry_pass=status == 'GEOMETRY_RECEIPT_PASS',
                    stage_id=stage_id, reason=reason, scope='receipt validation only; not native acceptance')
    def fail(reason): return verdict('GEOMETRY_REJECTED', reason)
    def integer(value): return type(value) is int and value >= 0
    if not isinstance(expectation, dict): return fail('missing_explicit_stage_geometry_expectation')
    lo, hi = expectation.get('min_bodies'), expectation.get('max_bodies')
    kinds = expectation.get('expected_body_kinds')
    required = expectation.get('required_receipt_fields')
    if not integer(lo) or not integer(hi) or hi < lo: return fail('invalid_expected_body_count')
    if not isinstance(kinds, list) or not kinds or any(k not in ('solid', 'sheet') for k in kinds):
        return fail('invalid_expected_body_kinds')
    if not isinstance(required, dict) or set(required) != {'result', 'body', 'feature'}:
        return fail('missing_explicit_required_receipt_fields')
    if any(not isinstance(v, list) or any(not isinstance(x, str) or not x for x in v) for v in required.values()):
        return fail('invalid_required_receipt_fields')
    if not isinstance(result, dict): return fail('result_not_dict')
    if result.get('ok') is not True: return fail('ok_not_strict_true')
    if any(k not in result for k in required['result']): return fail('required_result_field_missing')
    if type(result.get('failed_feature_count')) is not int or result['failed_feature_count'] != 0:
        return fail('failed_feature_count_not_integer_zero')
    features = result.get('features')
    if not isinstance(features, list): return fail('features_not_list')
    for feature in features:
        if not isinstance(feature, dict): return fail('feature_not_dict')
        if any(k not in feature for k in required['feature']): return fail('required_feature_field_missing')
        for key in ('errors', 'warnings'):
            if not isinstance(feature.get(key), list) or feature[key]: return fail('feature_' + key + '_missing_invalid_or_nonempty')
    bodies = result.get('bodies')
    if not isinstance(bodies, list): return fail('bodies_missing_or_not_list')
    if not lo <= len(bodies) <= hi: return fail('unexpected_body_count')
    if not bodies:
        reason = expectation.get('empty_geometry_reason')
        if (expectation.get('allow_empty_geometry') is True and isinstance(stage_id, str) and stage_id.strip()
                and isinstance(reason, str) and reason.strip()):
            return verdict('EMPTY_STAGE_ALLOWED', reason)
        return fail('empty_geometry_not_explicitly_authorized')
    for body in bodies:
        if not isinstance(body, dict): return fail('body_not_dict')
        if any(k not in body for k in required['body']): return fail('required_body_field_missing')
        if type(body.get('solid')) is not bool: return fail('solid_not_boolean')
        kind = 'solid' if body['solid'] else 'sheet'
        if ('kind' in body and body['kind'] != kind) or kind not in kinds: return fail('unexpected_body_kind')
        if type(body.get('consistency_fault_count')) is not int or body['consistency_fault_count'] != 0:
            return fail('consistency_fault_count_not_integer_zero')
        if type(body.get('faces')) is not int or body['faces'] <= 0: return fail('faces_not_positive_integer')
        volume = body.get('volume_mm3')
        if kind == 'solid':
            if 'volume_applicable' in body and body['volume_applicable'] is not True:
                return fail('solid_volume_applicability_contradiction')
            if type(volume) not in (int, float): return fail('invalid_solid_volume_type')
            try: valid = math.isfinite(volume) and volume > 0
            except (ValueError, OverflowError): valid = False
            if not valid: return fail('solid_volume_not_finite_positive')
        else:
            if expectation.get('allow_sheet_geometry') is not True: return fail('sheet_not_authorized')
            if ('volume_mm3' not in body or volume is not None or body.get('volume_applicable') is not False):
                return fail('sheet_volume_evidence_invalid')
    return verdict('GEOMETRY_RECEIPT_PASS', 'nonempty_expected_bodies_and_receipt_fields_valid')


def compatibility_expectation(allow_sheets=False):
    """Only for legacy boolean helper; batch MUST take its policy from stage spec."""
    return dict(min_bodies=1, max_bodies=2**31-1,
                expected_body_kinds=['solid', 'sheet'] if allow_sheets is True else ['solid'],
                allow_sheet_geometry=allow_sheets is True,
                required_receipt_fields={'result': [], 'body': [], 'feature': []})
