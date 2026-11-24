"""Rectangular flux quadrature; no NX verification. Patch evaluator returns x,du,dv,sense.
Domain: kind=full_rectangle,no_exclusions=True,single_cover=True,source=str.
Topology: closed=True,oriented=True,complete=True,source=str,senses={name:+/-1}.
Evidence is caller-attested, not proved. Split discontinuities first. Trim unsupported.
Error/floating floor are estimates, NOT bounds; paired rules can alias.
Units: coordinate units cubed. Derivatives must be conditioned and in actual UV.
"""
from dataclasses import dataclass
import math
import sys
import numpy as np

@dataclass(frozen=True)
class Patch:
    name: str
    bounds: tuple
    evaluator: object
    domain: dict
    sense: int

def vec(a):
    a = tuple(float(v) for v in a)
    if len(a) != 3 or not all(math.isfinite(v) for v in a):
        raise ValueError("finite 3-vector required")
    return a

def integrate(patches, *, topology, origin=(0.,0.,0.), abs_budget=1e-6,
              max_depth=6, max_evaluations=2000000, orders=(3,5)):
    """Uniform quartering with adaptive stopping; all callback calls counted.
    Return last COMPLETE level, or estimate=None if first level exceeds limit.
    Positive result is meaningful only with correct caller topology evidence.
    Converged means estimated numerical tolerance, never native acceptance.
    """
    patches = tuple(patches)
    origin = vec(origin)
    if not math.isfinite(abs_budget) or abs_budget <= 0:
        raise ValueError("invalid budget")
    if type(max_depth) is not int or not 0 <= max_depth <= 12:
        raise ValueError("invalid depth")
    if type(max_evaluations) is not int or max_evaluations <= 0:
        raise ValueError("invalid evaluation limit")
    if (len(orders)!=2 or any(type(n) is not int for n in orders)
            or not 2 <= orders[0] < orders[1] <= 16):
        raise ValueError("invalid orders")
    names = [p.name for p in patches]
    if not names or any(not isinstance(n,str) or not n for n in names):
        raise ValueError("named patches required")
    if len(set(names)) != len(names):
        raise ValueError("duplicate patches")
    if (not isinstance(topology,dict)
            or not isinstance(topology.get("source"),str) or not topology["source"]
            or any(topology.get(k) is not True for k in ("closed","oriented","complete"))):
        raise ValueError("separate closed oriented complete topology required")
    senses = topology.get("senses",{})
    if set(senses) != set(names):
        raise ValueError("boundary coverage mismatch")
    for p in patches:
        if (type(p.sense) is not int or p.sense not in (-1,1)
                or type(senses[p.name]) is not int or senses[p.name] != p.sense):
            raise ValueError("orientation mismatch")
        d = p.domain
        if (not isinstance(d,dict) or d.get("kind") != "full_rectangle"
                or d.get("no_exclusions") is not True or d.get("single_cover") is not True
                or not isinstance(d.get("source"),str) or not d["source"]):
            raise ValueError("unknown/trimmed domain unsupported")
        if len(p.bounds)!=4 or not all(math.isfinite(t) for t in p.bounds):
            raise ValueError("invalid bounds")
        a,b,c,d = p.bounds
        if not a<b or not c<d or not callable(p.evaluator):
            raise ValueError("invalid patch")
        if not math.isfinite(b-a) or not math.isfinite(d-c):
            raise ValueError("range overflow")
    rules = [np.polynomial.legendre.leggauss(n) for n in orders]
    count = 0
    eps = sys.float_info.epsilon
    def quad(p, rect, rule):
        nonlocal count
        a,b,c,d = rect
        hu,hv = (b-a)/2.,(d-c)/2.
        mu,mv = a+hu,c+hv
        if not a<mu<b or not c<mv<d:
            raise ValueError("parameter precision exhausted")
        terms, mags, floors = [],[],[]
        for xi,wi in zip(*rule):
            u = mu+hu*float(xi)
            for xj,wj in zip(*rule):
                v = mv+hv*float(xj)
                if not a<u<b or not c<v<d:
                    raise ValueError("node rounded to boundary")
                count += 1
                x,du,dv,s = p.evaluator(u,v)
                if isinstance(s,(bool,np.bool_)) or s != p.sense:
                    raise ValueError("callback orientation mismatch")
                x,du,dv = vec(x),vec(du),vec(dv)
                cross, crossmag = [],[]
                for k in range(3):
                    j,l = (k+1)%3,(k+2)%3
                    t,q = du[j]*dv[l],du[l]*dv[j]
                    cross.append(t-q)
                    crossmag.append(abs(t)+abs(q))
                delta = [x[k]-origin[k] for k in range(3)]
                w = hu*hv*float(wi)*float(wj)/3.
                products = [delta[k]*cross[k] for k in range(3)]
                term = w*s*math.fsum(products)
                mag = abs(w)*math.fsum(abs(t) for t in products)
                scale = math.fsum((abs(x[k])+abs(origin[k])+abs(delta[k]))
                                  *crossmag[k] for k in range(3))
                floor = 32.*eps*(mag+abs(w)*scale)
                if not all(math.isfinite(t) for t in (term,mag,floor)):
                    raise ValueError("nonfinite quadrature")
                terms.append(term); mags.append(mag); floors.append(floor)
        return math.fsum(terms),math.fsum(mags),math.fsum(floors)
    trace, faces = [],[]
    reason = "max_depth"
    for depth in range(max_depth+1):
        n = 2**depth
        needed = len(patches)*n*n*sum(k*k for k in orders)
        if count+needed > max_evaluations:
            reason = "evaluation_limit"
            break
        faces = []
        for p in patches:
            a,b,c,d = p.bounds
            vals,errs,mags,floors = [],[],[],[]
            for i in range(n):
                for j in range(n):
                    r = (a+(b-a)*i/n,a+(b-a)*(i+1)/n,
                         c+(d-c)*j/n,c+(d-c)*(j+1)/n)
                    lo,hi = quad(p,r,rules[0]),quad(p,r,rules[1])
                    vals.append(hi[0]); errs.append(abs(hi[0]-lo[0]))
                    mags.append(hi[1]); floors.append(max(lo[2],hi[2]))
            faces.append(dict(name=p.name,contribution=math.fsum(vals),
                error_estimate=math.fsum(errs),absolute_contributions=math.fsum(mags),
                floating_error_floor=math.fsum(floors)))
        total = math.fsum(f["contribution"] for f in faces)
        error = math.fsum(f["error_estimate"] for f in faces)
        floor = math.fsum(f["floating_error_floor"] for f in faces)
        magnitude = math.fsum(f["absolute_contributions"] for f in faces)
        if not all(math.isfinite(t) for t in (total,error,floor,magnitude,error+floor)):
            raise ValueError("nonfinite sum")
        trace.append(dict(depth=depth,cells=len(patches)*n*n,estimate=total,
            error_estimate=error,floating_error_floor=floor,
            combined_error_estimate=error+floor,evaluations=count,
            change=abs(total-trace[-1]["estimate"]) if trace else None))
        if error+floor <= abs_budget:
            reason = "estimated_tolerance"
            break
        if floor > abs_budget:
            reason = "floating_error_floor"
            break
    last = trace[-1] if trace else {}
    total = last.get("estimate")
    mag = math.fsum(f["absolute_contributions"] for f in faces)
    ratio = None if total is None else (mag/abs(total) if total else
                                        (1. if mag==0 else math.inf))
    return dict(schema_version=1,estimate=total,
        error_estimate=last.get("error_estimate"),
        floating_error_floor=last.get("floating_error_floor"),
        combined_error_estimate=last.get("combined_error_estimate"),
        error_is_bound=False,converged=reason=="estimated_tolerance",stop_reason=reason,
        actual_evaluation_count=count,face_contributions=faces,refinement_trace=trace,
        cancellation_ratio=ratio,origin=list(origin),orders=list(orders),
        absolute_budget=abs_budget,topology_status="caller_attested",
        topology_source=topology["source"],native_verified=False)
