"""Additive finite-observation sidecar. No geometry solver or native access.

The P05 numerical coincidence thresholds are retained as a separate check.
Passing them never proves representation identity, trim membership, or a map.
"""
from copy import deepcopy
from decimal import Decimal


def _residual(value):
    d = Decimal(str(value))
    if not d.is_finite() or d < 0:
        raise ValueError('Residual must be finite and nonnegative')
    return d


def finite_observation(*, source_id, target_id, uv_residual,
                       xyz_edge_residual, xyz_face_residual,
                       evidence_refs, topology_receipt=None,
                       source_membership_receipt=None,
                       candidate_enumeration_complete=None,
                       candidate_metadata=None, legacy_status=None):
    """Describe existing finite observations; never infer candidate membership.

    `topology_receipt` is supplied incidence evidence, not point classification.
    Source membership remains attached to its own point and is not inherited.
    The returned object is a new sidecar; archived records are never mutated.
    """
    if not source_id or not target_id or not evidence_refs:
        raise ValueError('Representation IDs and evidence references required')
    uv, edge, face = map(_residual, (uv_residual, xyz_edge_residual, xyz_face_residual))
    strict = uv <= Decimal('1e-12') and max(edge, face) <= Decimal('1e-9')
    below = edge <= Decimal('0.001')
    return {
        'schema_version': 'NXAI.ParamMapObservation/1.0',
        'source_representation_id': source_id,
        'target_representation_id': target_id,
        'representation_identity_match': {
            'status': 'NOT_ESTABLISHED',
            'reason': 'Residuals alone cannot prove representation identity or common origin',
        },
        'P05_numeric_coincidence_check': {
            'status': 'WITHIN_THRESHOLDS' if strict else 'OUTSIDE_THRESHOLDS',
            'UV_max_chart_units': '1e-12', 'XYZ_max_mm': '1e-9',
            'full_P05_source_identity_predicate_applied': False,
            'purpose': 'Cross-representation residual check using unchanged numeric limits; not provenance proof',
        },
        'finite_param_map_status': ('FINITE_CORRESPONDENCE_OBSERVED_STRICT_RESIDUAL_MET'
                                   if strict else 'FINITE_CORRESPONDENCE_OBSERVED_STRICT_RESIDUAL_UNMET'),
        'finite_uv_residual': {'value': str(uv), 'units': 'chart units'},
        'finite_xyz_residual': {'to_edge': str(edge), 'to_face_projection': str(face), 'units': 'mm'},
        'geometry_gate_observation': {
            'status': 'BELOW_THRESHOLD_FINITE_OBSERVATION' if below else 'EXCEEDS_THRESHOLD_FINITE_OBSERVATION',
            'threshold_mm': '0.001', 'boundary_acceptance': 'NOT_ESTABLISHED',
            'global_error_bound': None,
        },
        'trim_membership_status': {
            'FINSP_candidate': 'UNKNOWN',
            'source_point_receipt': deepcopy(source_membership_receipt),
            'inherit_source_to_candidate': False,
        },
        'topology_incidence_status': {
            'status': 'RECORDED_NATIVE_INCIDENCE' if topology_receipt else 'UNKNOWN',
            'receipt': deepcopy(topology_receipt), 'does_not_prove_point_membership': True,
        },
        'candidate_enumeration_complete_for_supplied_uv_and_shifts': candidate_enumeration_complete,
        'candidate_metadata': deepcopy(candidate_metadata),
        'global_param_map_complete': False, 'global_3d_minimum_proven': False,
        'global_proof_status': 'UNKNOWN_NOT_PROVEN', 'uniqueness': 'UNKNOWN',
        'native_FinUseDomain': None, 'native_EdgeUseDomain': None, 'native_phase': None,
        'geometry_release': False,
        'legacy_status': legacy_status,
        'evidence_refs': deepcopy(evidence_refs),
    }
