"""Planar trimmed-face Green integration; standard library only.

Callbacks evaluate actual finite directed edge spans: evaluate(t) -> (x, dx/dt).
Descending bounds reverse traversal; do not also negate the derivative.
Adapters may cache native evaluations or obtain quadrature batches, but must
return actual curve values, never a sparse inside mask or tessellated proxy.

Topology evidence is a REQUIRED caller attestation, not a theorem proved here.
Sampling checks planarity/connectivity only. No global body closure or NX mass
accuracy is established. Error estimates cover quadrature/roundoff heuristics,
not geometry, topology, evaluator error, or unsampled oscillations.
"""
from dataclasses import dataclass
from math import fsum, isfinite, sqrt
from sys import float_info

class TrimError(ValueError):
    pass

class ConvergenceError(TrimError):
    pass

class Cancelled(TrimError):
    pass

@dataclass(frozen=True)
class Edge:
    name: str
    evaluate: object
    start: float
    end: float

@dataclass(frozen=True)
class Loop:
    name: str
    role: str
    edges: tuple
    closure_evidence: str
    sense_evidence: str

@dataclass(frozen=True)
class Topology:
    # Nonempty provenance/argument text, not booleans or "unknown".
    coverage_evidence: str
    simplicity_evidence: str
    disjoint_evidence: str
    containment_evidence: str

def _evidence(value):
    return (isinstance(value, str) and bool(value.strip())
            and value.strip().lower() not in
            ("unknown", "unproven", "none", "false", "true", "pending"))

def _vec(value):
    try:
        v = tuple(float(x) for x in value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise TrimError("invalid vector") from exc
    if len(v) != 3 or not all(isfinite(x) for x in v):
        raise TrimError("expected finite 3-vector")
    return v

def _sub(a, b):
    return tuple(x-y for x, y in zip(a, b))

def _dot(a, b):
    return fsum(x*y for x, y in zip(a, b))

def _cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2],
            a[0]*b[1]-a[1]*b[0])

def _norm(a):
    return sqrt(_dot(a, a))

def _unit(a):
    length = _norm(a)
    if not isfinite(length) or length == 0:
        raise TrimError("invalid normal")
    return tuple(x/length for x in a)

# Positive abscissas and weights for independent Gauss-Legendre 4/8 rules.
_G4 = ((0.3399810435848563, 0.6521451548625461),
       (0.8611363115940526, 0.3478548451374538))
_G8 = ((0.1834346424956498, 0.3626837833783620),
       (0.5255324099163290, 0.3137066458778873),
       (0.7966664774136267, 0.2223810344533745),
       (0.9602898564975363, 0.1012285362903763))

def integrate_face(loops, point, normal, topology, *, abs_tol=1e-9,
                   rel_tol=1e-10, plane_tol=1e-7, join_tol=1e-7,
                   tangent_tol=1e-9, sample_count=17, min_depth=2,
                   max_depth=18, max_evaluations=500000, cancel=None):
    """Return area, signed origin flux, per-loop terms and estimated errors.

    point is on the plane; normal defines the outward face orientation.
    Outer boundaries must be CCW and holes CW as viewed from +normal.
    Exactly one outer loop is supported; split disconnected faces upstream.
    abs_tol is in squared length units; flux = area*dot(point,unit_normal)/3.
    All evidence must apply to these exact ordered edge uses and finite spans.
    The caller attests simple loops, disjoint boundaries, proper hole
    containment, complete coverage with no duplicates, closure and sense.

    Mandatory 2**min_depth panels per edge precede adaptive subdivision.
    A pilot estimates net area for a cancellation-aware global tolerance.
    Error budgets are divided among edges and panels, never per-loop relative
    tolerances. Failure to converge raises; no partial accepted result.
    cancel() may request interruption at every curve evaluation.
    """
    loops = tuple(loops)
    controls = (abs_tol, rel_tol, plane_tol, join_tol, tangent_tol)
    if not all(isfinite(v) and v >= 0 for v in controls) or abs_tol <= 0:
        raise TrimError("invalid tolerances")
    for value in (sample_count, min_depth, max_depth, max_evaluations):
        if type(value) is not int:
            raise TrimError("integer controls required")
    if (sample_count < 3 or not 1 <= min_depth <= max_depth <= 24
            or max_evaluations < 1):
        raise TrimError("invalid refinement controls")
    if not isinstance(topology, Topology) or not all(_evidence(x) for x in (
            topology.coverage_evidence, topology.simplicity_evidence,
            topology.disjoint_evidence, topology.containment_evidence)):
        raise TrimError("complete explicit topology attestation required")
    if not loops or sum(loop.role == "outer" for loop in loops) != 1:
        raise TrimError("exactly one outer loop required")
    p, n = _vec(point), _unit(_vec(normal))
    axis = min(((1.,0.,0.), (0.,1.,0.), (0.,0.,1.)),
               key=lambda a: abs(_dot(a, n)))
    u = _unit(_cross(axis, n))
    v = _cross(n, u)  # u cross v = n
    height = _dot(p, n)
    evaluations = 0
    max_plane = 0.
    max_join = 0.
    max_tangent = 0.
    names, edge_names = set(), set()

    def evaluate(edge, t):
        nonlocal evaluations, max_plane, max_tangent
        if cancel is not None and cancel():
            raise Cancelled("integration cancelled")
        if evaluations >= max_evaluations:
            raise ConvergenceError("evaluation budget exhausted")
        evaluations += 1
        x, dx = edge.evaluate(t)
        x, dx = _vec(x), _vec(dx)
        q = _sub(x, p)
        residual = abs(_dot(q, n))
        tangent = abs(_dot(dx, n))
        max_plane = max(max_plane, residual)
        max_tangent = max(max_tangent, tangent)
        if residual > plane_tol:
            raise TrimError("sampled curve point is not planar")
        if tangent > tangent_tol * max(1., _norm(dx)):
            raise TrimError("sampled derivative is not tangent to plane")
        return x, 0.5 * (_dot(q, u)*_dot(dx, v)
                          - _dot(q, v)*_dot(dx, u))

    for loop in loops:
        if (not isinstance(loop.name, str) or not loop.name.strip()
                or loop.name in names or loop.role not in ("outer", "inner")
                or not loop.edges or not _evidence(loop.closure_evidence)
                or not _evidence(loop.sense_evidence)):
            raise TrimError("invalid loop or missing closure/sense evidence")
        names.add(loop.name)
        ends = []
        for edge in loop.edges:
            if (not isinstance(edge.name, str) or not edge.name.strip()
                    or edge.name in edge_names or not callable(edge.evaluate)
                    or not isfinite(edge.start) or not isfinite(edge.end)
                    or edge.start == edge.end
                    or not isfinite(edge.end-edge.start)):
                raise TrimError("invalid or duplicate directed edge use")
            edge_names.add(edge.name)
            points = [evaluate(edge, edge.start + (edge.end-edge.start)*i /
                               (sample_count-1))[0]
                      for i in range(sample_count)]
            ends.append((points[0], points[-1]))
        for i, (_, end) in enumerate(ends):
            gap = _norm(_sub(end, ends[(i+1) % len(ends)][0]))
            max_join = max(max_join, gap)
            if gap > join_tol:
                raise TrimError("trim loop is not endpoint-connected")

    def pair(edge, a, b):
        midpoint, half = a + (b-a)/2, (b-a)/2
        if midpoint == a or midpoint == b:
            raise ConvergenceError("parameter resolution exhausted")
        sums, magnitudes = [], []
        for rule in (_G4, _G8):
            terms = [w * evaluate(edge, midpoint + sign*half*z)[1]
                     for z, w in rule for sign in (-1, 1)]
            sums.append(half * fsum(terms))
            magnitudes.append(abs(half) * fsum(abs(t) for t in terms))
        error = max(abs(sums[1]-sums[0]),
                    64*float_info.epsilon*max(magnitudes))
        if not all(isfinite(x) for x in (*sums, error)):
            raise TrimError("nonfinite quadrature")
        return sums[1], error

    count = 2**min_depth
    panels = []
    for loop in loops:
        for edge in loop.edges:
            for i in range(count):
                a = edge.start + (edge.end-edge.start)*i/count
                b = edge.start + (edge.end-edge.start)*(i+1)/count
                value, error = pair(edge, a, b)
                panels.append((loop.name, edge, a, b, value, error))
    pilot = fsum(panel[4] for panel in panels)
    pilot_error = fsum(panel[5] for panel in panels)
    target = max(abs_tol, rel_tol*abs(pilot))
    budget = target/len(panels)
    deepest = min_depth
    leaves = 0

    def refine(edge, a, b, value, error, tol, depth):
        nonlocal deepest, leaves
        deepest = max(deepest, depth)
        if error <= tol:
            leaves += 1
            return value, error
        if depth >= max_depth:
            raise ConvergenceError("maximum refinement reached")
        mid = a + (b-a)/2
        left = pair(edge, a, mid)
        right = pair(edge, mid, b)
        # Include parent/children disagreement in each child's estimate.
        discrepancy = abs(fsum((left[0], right[0], -value)))
        left = (left[0], max(left[1], discrepancy/2))
        right = (right[0], max(right[1], discrepancy/2))
        l = refine(edge, a, mid, *left, tol/2, depth+1)
        r = refine(edge, mid, b, *right, tol/2, depth+1)
        return fsum((l[0], r[0])), fsum((l[1], r[1]))

    terms = {loop.name: [] for loop in loops}
    for name, edge, a, b, value, error in panels:
        terms[name].append(refine(edge, a, b, value, error, budget, min_depth))
    rows = []
    for loop in loops:
        area = fsum(x[0] for x in terms[loop.name])
        err = fsum(x[1] for x in terms[loop.name])
        expected_sign = 1 if loop.role == "outer" else -1
        if expected_sign*area <= err:
            raise TrimError("loop orientation wrong or area unresolved")
        rows.append(dict(name=loop.name, role=loop.role, signed_area=area,
                         area_error_estimate=err, flux=area*height/3,
                         flux_error_estimate=err*abs(height)/3))
    area = fsum(row["signed_area"] for row in rows)
    error = fsum(row["area_error_estimate"] for row in rows)
    final_target = max(abs_tol, rel_tol*abs(area))
    if area <= error:
        raise TrimError("net trimmed area is nonpositive or unresolved")
    if error > final_target:
        raise ConvergenceError("final cancellation-aware tolerance not met")
    flux = area*height/3
    if not isfinite(flux):
        raise TrimError("nonfinite flux")
    gross = fsum(abs(row["signed_area"]) for row in rows)
    return dict(area=area, flux=flux, area_error_estimate=error,
                flux_error_estimate=error*abs(height)/3,
                loops=rows, plane_height=height, normal=n,
                evaluations=evaluations, leaf_panels=leaves,
                deepest_refinement=deepest, cancellation_ratio=gross/area,
                sampled_max_plane_residual=max_plane,
                sampled_max_join_gap=max_join,
                sampled_max_normal_derivative=max_tangent,
                convergence=[dict(stage="minimum_refinement", area=pilot,
                                  error_estimate=pilot_error, panels=len(panels)),
                             dict(stage="adaptive", area=area,
                                  error_estimate=error, panels=leaves)],
                topology_status="caller_attested_with_sampled_checks",
                global_body_closure_proven=False,
                error_is_certified_bound=False)
