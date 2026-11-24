"""Finite experimental UV candidate enumeration; not a native Use domain.

Reuse the exact Fraction/GCD/Sturm projector without nearest-distance pruning.
The completeness statement is limited to supplied coefficients and the listed
periodic chart lifts. It is not completeness of spatial projection to a Face.
"""
from fractions import Fraction as F
from itertools import product
from math import ceil,floor
from .nurbs_ordered_topology import Spline
from .projection import project_curve
from .core_types import SplineActiveDomain

def _scope_flags(value):
    """Version 2: `complete` remains a compatibility alias, never global proof."""
    return dict(complete=value,
      candidate_enumeration_complete_for_supplied_uv_and_shifts=value,
      global_param_map_complete=False,global_3d_minimum_proven=False,
      completeness_schema_version=2)

def uv_candidates(degree, knots, poles, uv, periods, evidence_id, max_depth=96):
    s=Spline(degree,knots,poles)
    if any(p[3]<=0 for p in s.poles):
        return dict(candidates=[],unresolved=['non_positive_weights'],**_scope_flags(False))
    choices=[]
    for d in range(2):
        period=F(str(periods[d]));q=F(str(uv[d]))
        if not period:choices.append([0]);continue
        if period<0:raise ValueError('Negative period')
        lo=min(p[d]/p[3] for p in s.poles);hi=max(p[d]/p[3] for p in s.poles)
        # Include every integer translation that can be nearest to any point
        # in the positive-rational control hull, including half-period ties.
        choices.append(list(range(ceil((lo-q)/period-F(1,2)),floor((hi-q)/period+F(1,2))+1)))
    if any(not c for c in choices) or len(choices[0])*len(choices[1])>16:
        return dict(candidates=[],unresolved=['chart_shift_budget'],**_scope_flags(False))
    candidates=[];unresolved=[];continuous=[];branches=[]
    active=SplineActiveDomain(s.domain,evidence_id)
    for shifts in product(*choices):
        target=[F(str(uv[d]))+shifts[d]*F(str(periods[d])) for d in range(2)]+[F(0)]
        result=project_curve(s,target,active,max_depth=max_depth)
        branches.append(dict(shifts=list(shifts),target_UV=[float(x) for x in target[:2]],candidate_complete=result.candidate_complete))
        for c in result.isolated_candidate:
            candidates.append(dict(parameter=float(c.parameter),root_bracket=[float(x) for x in c.parameter_interval],
              side=c.side,uv_distance_squared=float(c.squared_distance),stationary_residual=float(c.root_residual),
              shifts=list(shifts),target_UV=[float(x) for x in target[:2]]))
        unresolved.extend(dict(shifts=list(shifts),interval=str(x)) for x in result.unresolved_interval)
        continuous.extend(dict(shifts=list(shifts),interval=[float(v) for v in x]) for x in result.continuum_solution)
    return dict(candidates=candidates,unresolved=unresolved,continuous=continuous,branches=branches,
      **_scope_flags(not unresolved and not continuous),original_UseDomain=None,
      scope='All stationary brackets and knot-side endpoints in returned spline active region for enumerated relevant UV chart shifts; no 3D global projection claim')
