"""Source only; Python 3.12 stdlib. No NX calls, mutations, integration, or I/O.
Exact arithmetic proves statements about supplied decimal coefficients only.
Matching is adaptive measurement, never a global distance bound.
"""
from dataclasses import dataclass
from fractions import Fraction as F
from math import comb, sqrt, floor
import re
from .core_types import (GeometryObjectId, TopologyUseId, EvaluatorDomain,
    SplineActiveDomain, EdgeUseDomain, FinUseDomain, PcurveDomain, FinUse,
    CurveRepresentation, SurfaceRepresentation, ParamMap, PeriodicChart,
    ToleranceProvenance, NativeReceipt, ProjectionResult, GeometryWitness)

def q(x):
    return x if isinstance(x, F) else F(str(x))

def add(a, b):
    return tuple((a[i] if i < len(a) else 0) +
                 (b[i] if i < len(b) else 0) for i in range(max(len(a), len(b))))

def scale(a, s):
    return tuple(x*s for x in a)

def sub(a, b):
    return add(a, scale(b, -1))

def mul(a, b):
    out = [F(0)]*(len(a)+len(b)-1)
    for i, x in enumerate(a):
        for j, y in enumerate(b):
            out[i+j] += x*y
    return tuple(out)

def deriv(a):
    return tuple(i*a[i] for i in range(1, len(a))) or (F(0),)

def val(a, t):
    z = F(0)
    for c in reversed(a):
        z = z*t+c
    return z

def basis_piece(knots, degree, a, b):
    """All basis polynomials on one nonempty knot span, t=(v-a)/(b-a).
    Values at t=0/1 are the appropriate one-sided limits, not nextafter."""
    k = tuple(map(q, knots))
    a, b = q(a), q(b)
    if not a < b or any(a < x < b for x in k):
        raise ValueError("must be a single nonempty knot span")
    mid = (a+b)/2
    n = [(F(int(k[i] <= mid < k[i+1])),) for i in range(len(k)-1)]
    for p in range(1, degree+1):
        nxt = []
        for i in range(len(n)-1):
            left = (F(0),) if k[i+p] == k[i] else scale(
                mul((a-k[i], b-a), n[i]), 1/(k[i+p]-k[i]))
            right = (F(0),) if k[i+p+1] == k[i+1] else scale(
                mul((k[i+p+1]-a, a-b), n[i+1]), 1/(k[i+p+1]-k[i+1]))
            nxt.append(add(left, right))
        n = nxt
    return n

@dataclass(frozen=True)
class Spline:
    degree: int
    knots: tuple
    poles: tuple  # homogeneous tuples: (wx,wy,wz,w); never multiply again

    def __post_init__(self):
        object.__setattr__(self, "knots", tuple(map(q, self.knots)))
        object.__setattr__(self, "poles", tuple(tuple(map(q, p)) for p in self.poles))
        if (self.degree < 0 or len(self.knots) != len(self.poles)+self.degree+1
            or any(a > b for a, b in zip(self.knots, self.knots[1:]))
            or not self.poles or any(len(p) != 4 for p in self.poles)
            or not self.domain[0] < self.domain[1]):
            raise ValueError("invalid spline")

    @property
    def domain(self):
        return self.knots[self.degree], self.knots[len(self.poles)]

    def spans(self):
        a, b = self.domain
        return [(x, y) for x, y in zip(self.knots, self.knots[1:])
                if a <= x < y <= b]

    def piece(self, a, b):
        ns = basis_piece(self.knots, self.degree, a, b)
        return tuple(sum_poly(scale(n, p[d]) for n, p in zip(ns, self.poles))
                     for d in range(4))

    def jet(self, t, side="right"):
        t = q(t)
        if side not in ("left", "right"):
            raise ValueError("side")
        spans = [(a, b) for a, b in self.spans() if a <= t <= b]
        if not spans:
            raise ValueError("outside domain")
        a, b = spans[0 if side == "left" else -1]
        c = self.piece(a, b)
        s = (t-a)/(b-a)
        return (tuple(val(p, s) for p in c),
                tuple(val(deriv(p), s)/(b-a) for p in c))

def sum_poly(items):
    result = (F(0),)
    for p in items:
        result = add(result, p)
    return result

def project(h, dh):
    w = h[3]
    if w == 0:
        raise ValueError("zero homogeneous weight")
    return (tuple(h[i]/w for i in range(3)),
            tuple((dh[i]*w-h[i]*dh[3])/(w*w) for i in range(3)))

@dataclass(frozen=True)
class Surface:
    nu: int
    nv: int
    pu: int
    pv: int
    ku: tuple
    kv: tuple
    poles: tuple  # U fastest, exact decimal strings or Fractions

    def row(self, j):
        return Spline(self.pu, self.ku, self.poles[j*self.nu:(j+1)*self.nu])

    def boundary(self, u, side):
        jets = [self.row(j).jet(u, side) for j in range(self.nv)]
        return (Spline(self.pv, self.kv, tuple(x[0] for x in jets)),
                Spline(self.pv, self.kv, tuple(x[1] for x in jets)))

    def evaluate(self, u, v, uside="right", vside="right"):
        hcurve, ucurve = self.boundary(u, uside)
        h, hv = hcurve.jet(v, vside)
        hu, _ = ucurve.jet(v, vside)
        xyz, dv = project(h, hv)
        _, du = project(h, hu)
        return xyz, du, dv

def sheet_from_log(text):
    """Accept raw log, not tool line-number prefixes. Fail closed on missing data."""
    def knots(axis, expected):
        matches = re.findall(r"^knot axis="+axis+r" i=(\d+) value=([^\s]+)$", text, re.M)
        data = {int(i): q(v) for i, v in matches}
        if len(matches) != expected or sorted(data) != list(range(expected)):
            raise ValueError("missing/duplicate knots")
        return tuple(data[i] for i in range(expected))
    surface_text = text.split("before ask_spline index=0")[0]
    matches = re.findall(r"^pole i=(\d+) tuple=([^\s]+)$", surface_text, re.M)
    data = {int(i): tuple(map(q, p.split(","))) for i, p in matches}
    if len(matches) != 56 or sorted(data) != list(range(56)):
        raise ValueError("missing/duplicate poles")
    return Surface(14, 4, 4, 3, knots("u", 19), knots("v", 8),
                   tuple(data[i] for i in range(56)))

def bernstein(power):
    n = len(power)-1
    return tuple(sum(power[i]*F(comb(k, i), comb(n, i))
                     for i in range(k+1)) for k in range(n+1))

def norm_bound(power):
    return max(map(abs, bernstein(power)))

def positive_lower(power):
    lo = min(bernstein(power))
    if lo <= 0:
        raise ValueError("weight positivity unresolved: subdivide, do not accept")
    return lo

def seam_report(s):
    """Complete coefficient tests for C0, dS/du, dS/dv on EVERY V span.
    Cross products allow different homogeneous gauges. Bounds are componentwise
    seam residual bounds only, NOT pcurve/edge correspondence bounds."""
    u0, u1 = s.row(0).domain
    l, lu = s.boundary(u0, "right")
    r, ru = s.boundary(u1, "left")
    rows = []
    for a, b in l.spans():
        h0, h1 = l.piece(a,b), r.piece(a,b)
        d0, d1 = lu.piece(a,b), ru.piece(a,b)
        v0 = tuple(scale(deriv(p), 1/(b-a)) for p in h0)
        v1 = tuple(scale(deriv(p), 1/(b-a)) for p in h1)
        w0, w1 = h0[3], h1[3]
        low = positive_lower(w0)*positive_lower(w1)
        pos = [sub(mul(h0[i],w1), mul(h1[i],w0)) for i in range(3)]
        def difference(d0, d1):
            return [sub(mul(sub(mul(d0[i],w0),mul(h0[i],d0[3])),mul(w1,w1)),
                        mul(sub(mul(d1[i],w1),mul(h1[i],d1[3])),mul(w0,w0)))
                    for i in range(3)]
        du, dv = difference(d0,d1), difference(v0,v1)
        def metric(polys, den):
            return {"exact_zero": all(all(c == 0 for c in p) for p in polys),
                    "numerator_power_coefficients": [[str(c) for c in p] for p in polys],
                    "component_upper_bounds": [str(norm_bound(p)/den) for p in polys]}
        rows.append({"v_span": [str(a),str(b)],
                     "H_left": [[str(c) for c in p] for p in h0],
                     "H_right": [[str(c) for c in p] for p in h1],
                     "Hu_left": [[str(c) for c in p] for p in d0],
                     "Hu_right": [[str(c) for c in p] for p in d1],
                     "position": metric(pos,low),
                     "du": metric(du,low*low), "dv": metric(dv,low*low)})
    return {"basis": "exact supplied decimal coefficients; one-sided span endpoints",
            "spans": rows, "native_verification": False}

def norm(v):
    return sqrt(sum(float(x)*float(x) for x in v))

def delta(a,b):
    return tuple(float(x)-float(y) for x,y in zip(a,b))

def dot(a,b):
    return sum(float(x)*float(y) for x,y in zip(a,b))

@dataclass(frozen=True)
class Capture:
    source_sha256: str
    copy_sha256_before: str
    copy_sha256_after: str
    pid: int
    part_tag: int
    face_tag: int
    edge_tags: tuple
    modified_before: bool
    modified_after: bool
    ownership_verified: bool
    incidence_verified: bool

    def admissible(self, historical_source_sha):
        return (bool(historical_source_sha) and
                self.source_sha256 == historical_source_sha ==
                self.copy_sha256_before == self.copy_sha256_after and
                self.pid > 0 and self.part_tag > 0 and self.face_tag > 0 and
                len(self.edge_tags) == len(set(self.edge_tags)) and
                not self.modified_before and not self.modified_after and
                self.ownership_verified and self.incidence_verified)

@dataclass(frozen=True)
class Pcurve:
    id: str
    curve: Spline
    raw_sense: int
    historical_loop: int
    historical_slot: int
    fin_ids: tuple = ()  # populated only after verified matching, not index pairing

@dataclass(frozen=True)
class Edge:
    id: str
    capture: Capture
    tag: int
    domain: tuple
    closed: bool
    fin_ids: tuple
    vertex_ids: tuple  # native incidence; allow no distinct endpoint vertex

@dataclass(frozen=True)
class Fin:
    id: str
    face_id: str
    loop_id: str
    edge_id: str
    pcurve_id: str
    previous: str
    following: str
    native_orientation: int
    pcurve_to_edge_direction: int | None
    loop_to_pcurve_direction: int | None
    candidates: tuple  # preserve every candidate report

@dataclass(frozen=True)
class Loop:
    id: str
    face_id: str
    fin_ids: tuple  # native order, not sorted area
    native_type: str | None
    containment_parent: str | None
    containment_evidence: str | None
    closure_evidence: dict | None

@dataclass(frozen=True)
class Face:
    id: str
    loop_ids: tuple
    native_tag: int
    chart: str
    region_evidence: str | None  # native topology + periodic-chart region classification

def topology_errors(face, loops, fins, edges, pcurves):
    errors = []
    def need(ok, text):
        if not ok: errors.append(text)
    need(bool(face.region_evidence), "face region/containment unresolved")
    need(set(face.loop_ids) == set(loops), "loop coverage")
    used_fins = []
    for lid in face.loop_ids:
        if lid not in loops:
            continue
        loop = loops[lid]
        need(loop.face_id == face.id, "loop face")
        need(bool(loop.containment_evidence), "containment evidence absent")
        need(loop.containment_parent is None or loop.containment_parent in loops,
             "containment parent absent")
        seen = {lid}
        parent = loop.containment_parent
        while parent is not None and parent in loops:
            if parent in seen:
                errors.append("containment cycle"); break
            seen.add(parent)
            parent = loops[parent].containment_parent
        need(bool(loop.fin_ids), "empty loop")
        need(bool(loop.closure_evidence) and
             loop.closure_evidence.get("accepted") is True, "closure unresolved")
        for i, fid in enumerate(loop.fin_ids):
            used_fins.append(fid)
            if fid not in fins:
                errors.append("missing fin"); continue
            f = fins[fid]
            need(f.loop_id == lid and f.face_id == face.id, "fin parent")
            need(f.previous == loop.fin_ids[i-1] and
                 f.following == loop.fin_ids[(i+1)%len(loop.fin_ids)], "fin order")
            need(f.edge_id in edges and f.pcurve_id in pcurves, "missing incidence")
            if f.edge_id in edges:
                need(fid in edges[f.edge_id].fin_ids, "edge reverse adjacency")
            need(f.pcurve_to_edge_direction in (-1,1) and
                 f.loop_to_pcurve_direction in (-1,1), "direction unresolved")
            need(len(f.candidates) == 1, "ambiguous or missing match")
    need(len(used_fins) == len(set(used_fins)) and set(used_fins) == set(fins),
         "fin coverage")
    for e in edges.values():
        need(bool(e.fin_ids) and len(set(e.fin_ids)) == len(e.fin_ids), "edge uses")
        for fid in e.fin_ids:
            need(fid in fins and fins[fid].edge_id == e.id, "reverse fin adjacency")
    need({f.pcurve_id for f in fins.values()} == set(pcurves), "pcurve coverage")
    for pc in pcurves.values():
        expected = {f.id for f in fins.values() if f.pcurve_id == pc.id}
        need(set(pc.fin_ids) == expected and len(pc.fin_ids) == len(expected),
             "pcurve reverse adjacency")
    return errors

def lifted_pcurve(surface, pcurve):
    def evaluate(t):
        h, dh = pcurve.curve.jet(t)
        uv, uv_t = project(h,dh)
        xyz, su, sv = surface.evaluate(uv[0],uv[1])
        tangent = tuple(su[i]*uv_t[0]+sv[i]*uv_t[1] for i in range(3))
        return tuple(map(float,xyz)), tuple(map(float,tangent))
    return evaluate

def match_measurement(a_eval, a_breaks, b_eval, b_breaks,
                      closest_on_a, closest_on_b, distance_tol,
                      sampling_tol, min_depth=3, max_depth=12):
    """Callbacks evaluate ORIGINAL curves as (xyz,dxyz/dparameter).
    Closest callbacks return ALL retained stationary/minimum candidates:
      [{'parameter': float, 'distance': float, 'status': 'ok'}, ...].
    Adapter must include endpoints, knot-side limits and ambiguous minima.
    b callback is the original UF_EVAL edge closest-point evaluator.
    closest_on_a uses the lifted pcurve, never a UV-only metric.
    No nearest-sample substitute and no assumed equal parameterization.
    Two-sided full-domain recursion records nonuniform correspondence.
    Closed endpoints alone never decide direction or reject a phase shift.
    """
    if distance_tol <= 0 or sampling_tol <= 0 or min_depth < 0 or max_depth < min_depth:
        raise ValueError("tolerances/depth")
    records, unresolved, limits = [], [], []
    def run(label, ev, breaks, other_ev, closest):
        if len(breaks) < 2 or any(x >= y for x,y in zip(breaks,breaks[1:])):
            raise ValueError("full ordered domain breakpoints required")
        cache = {}
        def sample(t):
            if t in cache: return cache[t]
            point, tangent = ev(t)
            choices = closest(point)
            row = {"side":label, "parameter":t, "point":point,
                   "endpoint": t in (breaks[0],breaks[-1]), "candidates":[]}
            for c in choices:
                if c.get("status") != "ok":
                    unresolved.append((label,t,"closest status",c)); continue
                ot = c["parameter"]
                op, ov = other_ev(ot)
                d = norm(delta(point,op))
                speed = norm(tangent)*norm(ov)
                cosine = dot(tangent,ov)/speed if speed else None
                row["candidates"].append({**c, "measured_distance":d,
                    "tangent_cosine":cosine,
                    "local_direction": None if cosine is None or abs(cosine)<0.9
                                      else (1 if cosine>0 else -1)})
                if abs(d-c["distance"]) > sampling_tol:
                    unresolved.append((label,t,"closest residual disagreement"))
            if not row["candidates"]:
                unresolved.append((label,t,"no closest candidate"))
            records.append(row)
            cache[t] = (point,tangent,row)
            return cache[t]
        def visit(lo,hi,depth):
            mid = (lo+hi)/2
            l, m, r = sample(lo),sample(mid),sample(hi)
            chord = tuple((float(x)+float(y))/2 for x,y in zip(l[0],r[0]))
            sag = norm(delta(m[0],chord))
            turns = max(norm(delta(l[1],m[1])),norm(delta(m[1],r[1])))*(hi-lo)
            distances = [min((c["measured_distance"] for c in x[2]["candidates"]),
                             default=float("inf")) for x in (l,m,r)]
            varying = max(distances)-min(distances) > sampling_tol
            refine = depth < min_depth or sag > sampling_tol or turns > sampling_tol or varying
            if refine and depth < max_depth:
                visit(lo,mid,depth+1); visit(mid,hi,depth+1)
            elif refine:
                limits.append((label,lo,hi))
        for lo,hi in zip(breaks,breaks[1:]): visit(lo,hi,0)
    run("pcurve_to_edge",a_eval,a_breaks,b_eval,closest_on_b)
    run("edge_to_pcurve",b_eval,b_breaks,a_eval,closest_on_a)
    retained = [[c for c in row["candidates"] if c["measured_distance"] <= distance_tol]
                for row in records]
    signs = {c["local_direction"] for cs in retained for c in cs}
    unambiguous = all(len(cs)==1 for cs in retained)
    return {"records":records, "unresolved":unresolved, "depth_limit_intervals":limits,
            "sampled_bidirectional_within_tolerance":all(bool(cs) for cs in retained),
            "direction": next(iter(signs)) if unambiguous and len(signs)==1 and None not in signs else None,
            "candidate_ambiguity":not unambiguous,
            "global_bound":False, "topology_pass":False}

def periodic_step_options(previous, raw, periods, tie_tol=1e-12):
    """Nearest-lift options only for already resolved short steps.
    A half-period tie remains ambiguous. Caller must subdivide steps known or
    suspected to travel >= half a period; sparse endpoints cannot recover winding."""
    options = [()]
    for x,y,p in zip(previous,raw,periods):
        if p is None:
            choices = [float(y)]
        else:
            if p <= 0: raise ValueError("period")
            n = floor((x-y)/p)
            cand = [y+n*p,y+(n+1)*p]
            d = min(abs(z-x) for z in cand)
            choices = [float(z) for z in cand if abs(abs(z-x)-d)<=tie_tol]
        options = [prefix+(v,) for prefix in options for v in choices]
    return options

def closure_report(uv_start, uv_end, lifted_start, lifted_end,
                   xyz_start, xyz_end, periods, uv_tol, xyz_tol):
    raw = delta(uv_end,uv_start)
    lift = delta(lifted_end,lifted_start)
    residues, winding = [], []
    for d,p in zip(lift,periods):
        n = round(d/p) if p is not None else 0
        residues.append(d-n*p if p is not None else d)
        winding.append(n if p is not None else 0)
    return {"raw_uv_gap":raw, "raw_uv_closed":norm(raw)<=uv_tol,
            "lifted_uv_gap":lift, "winding":winding,
            "periodic_uv_residual":residues,
            "periodic_uv_closed":norm(residues)<=uv_tol,
            "xyz_gap":norm(delta(xyz_end,xyz_start)),
            "xyz_closed":norm(delta(xyz_end,xyz_start))<=xyz_tol,
            "accepted":False}  # adjacency, validated lift and chart region still required


def ordered_parameter_trace(parameters, direction, domain, closed, tolerance):
    """Input MUST be a selected, continuously unwrapped closest-point branch.
    This only verifies sampled order/coverage; never chooses among branches.
    For a closed curve a full circuit may start at any phase."""
    lo, hi = domain
    if direction not in (-1,1) or hi <= lo or len(parameters)<2:
        raise ValueError("trace inputs")
    diffs = [direction*(b-a) for a,b in zip(parameters,parameters[1:])]
    travel = direction*(parameters[-1]-parameters[0])
    monotone = all(d >= -tolerance for d in diffs)
    coverage = abs(travel-(hi-lo)) <= tolerance
    endpoints = closed or (
        abs(parameters[0]-(lo if direction==1 else hi))<=tolerance and
        abs(parameters[-1]-(hi if direction==1 else lo))<=tolerance)
    return {"sampled_monotone":monotone, "sampled_one_cover":coverage,
            "endpoint_parameter_check":endpoints, "travel":travel,
            "global_proof":False}

def ordered_topology_gate(face, loops, fins, edges, pcurves,
                          historical_source_sha, evidence):
    """Fail closed. Evidence is a separately reviewed native/read-only receipt,
    NOT a boolean inferred by this module from adaptive sampling."""
    errors = topology_errors(face,loops,fins,edges,pcurves)
    for e in edges.values():
        if not e.capture.admissible(historical_source_sha) or e.tag not in e.capture.edge_tags:
            errors.append("capture hash/PID/ownership/incidence gate: "+e.id)
    for key in ("native_incidence_receipt", "all_knot_side_limits_receipt",
                "all_candidate_branch_review", "ordered_correspondence_receipt",
                "periodic_chart_region_and_containment_receipt",
                "seam_coefficient_report"):
        if not evidence.get(key):
            errors.append("missing reviewed evidence: "+key)
    return {"pass":not errors, "errors":errors,
            "integration_implemented":False}
