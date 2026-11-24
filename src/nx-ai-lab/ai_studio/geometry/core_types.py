"""Immutable geometry evidence contracts. No NX, persistence or billing dependencies."""
from dataclasses import dataclass
from fractions import Fraction
from typing import Optional


@dataclass(frozen=True)
class NativeReceipt:
    model_sha256: str
    session_id: str
    nx_version: str
    api: str
    return_code: int
    evidence_id: str


@dataclass(frozen=True)
class GeometryObjectId:
    model_sha256: str
    session_id: str
    native_id: str
    semantic_identity: str


@dataclass(frozen=True)
class TopologyUseId:
    body: GeometryObjectId
    face_id: str
    loop_id: str
    fin_id: str
    edge_id: str
    child_index: int


@dataclass(frozen=True)
class ParameterDomain:
    interval: Optional[tuple]
    evidence_id: Optional[str]

    def __post_init__(self):
        if self.interval is not None:
            a, b = map(Fraction, self.interval)
            if a >= b or not self.evidence_id:
                raise ValueError('Known domain requires ordered bounds and evidence')
            object.__setattr__(self, 'interval', (a, b))


@dataclass(frozen=True)
class EvaluatorDomain(ParameterDomain):
    pass


@dataclass(frozen=True)
class SplineActiveDomain(ParameterDomain):
    pass


@dataclass(frozen=True)
class EdgeUseDomain(ParameterDomain):
    use_id: Optional[TopologyUseId] = None


@dataclass(frozen=True)
class FinUseDomain(ParameterDomain):
    use_id: Optional[TopologyUseId] = None


@dataclass(frozen=True)
class PcurveDomain(ParameterDomain):
    support_id: Optional[str] = None


@dataclass(frozen=True)
class PeriodicChart:
    period: Optional[Fraction]
    phase: Optional[Fraction]
    evidence_id: Optional[str]

    def unwrap(self, parameter, turns):
        if self.period is None or self.phase is None or self.period <= 0:
            raise ValueError('Unknown periodic chart')
        return Fraction(parameter) + self.phase + turns * self.period


@dataclass(frozen=True)
class ParamMap:
    source_id: str
    target_id: str
    source_domain: ParameterDomain
    target_domain: ParameterDomain
    scale: Optional[Fraction]
    shift: Optional[Fraction]
    evidence_id: Optional[str]

    def apply(self, t):
        if self.scale is None or self.shift is None or not self.evidence_id:
            raise ValueError('Unverified map')
        if self.source_domain.interval is None or self.target_domain.interval is None:
            raise ValueError('Unknown map domain')
        if not self.source_domain.interval[0] <= t <= self.source_domain.interval[1]:
            raise ValueError('Outside map source')
        result = self.scale * t + self.shift
        if not self.target_domain.interval[0] <= result <= self.target_domain.interval[1]:
            raise ValueError('Outside map target')
        return result


@dataclass(frozen=True)
class ToleranceProvenance:
    value: float
    unit: str
    purpose: str
    source: str
    error_kind: str


@dataclass(frozen=True)
class CurveRepresentation:
    object_id: GeometryObjectId
    source_api: str
    payload_hash: str
    evaluator_domain: EvaluatorDomain
    spline_active_domain: SplineActiveDomain
    support_id: Optional[str]
    sense: Optional[int]
    chart: PeriodicChart
    param_map: Optional[ParamMap]
    tolerance: ToleranceProvenance
    receipt: NativeReceipt


@dataclass(frozen=True)
class SurfaceRepresentation:
    object_id: GeometryObjectId
    source_api: str
    payload_hash: str
    parent_surface_id: Optional[str]
    offset: Optional[float]
    charts: tuple
    sense: Optional[int]
    receipt: NativeReceipt


@dataclass(frozen=True)
class FinUse:
    use_id: TopologyUseId
    domain: FinUseDomain
    edge_domain: EdgeUseDomain
    orientation: int
    geometry_sense: int
    pcurve_id: str
    receipt: NativeReceipt


@dataclass(frozen=True)
class GeometryWitness:
    topology_incidence: Optional[TopologyUseId]
    face_trim_status: tuple
    edge_point_classification: Optional[int]
    geometric_distance: Optional[float]
    receipt: NativeReceipt


@dataclass(frozen=True)
class ProjectionCandidate:
    parameter_interval: tuple
    parameter: Fraction
    squared_distance: Fraction
    side: str
    root_residual: Fraction


@dataclass(frozen=True)
class ProjectionResult:
    isolated_candidate: tuple
    multiple_candidates: tuple
    continuum_solution: tuple
    unresolved_interval: tuple
    candidate_complete: bool
    minimum_certified: bool = False
    basis: str = 'exact rational coefficients supplied by caller'
