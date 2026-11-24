"""Bounded exact real-root isolation and rational curve projection.

Completeness concerns stationary parameters of the supplied coefficients.
Distances at interval representatives are observations, not global bounds.
"""
from fractions import Fraction as F
from .nurbs_ordered_topology import Spline, add, sub, mul, deriv, val, project
from .core_types import ProjectionCandidate, ProjectionResult


def trim(p):
    p = list(map(F, p))
    while len(p) > 1 and p[-1] == 0:
        p.pop()
    return tuple(p)


def divide(a, b):
    a, b = list(trim(a)), trim(b)
    if b == (0,):
        raise ValueError('Zero divisor')
    result = [F(0)] * max(1, len(a) - len(b) + 1)
    while len(a) >= len(b) and any(a):
        i = len(a) - len(b)
        factor = a[-1] / b[-1]
        result[i] = factor
        for j, c in enumerate(b):
            a[i+j] -= factor*c
        a = list(trim(a))
    return trim(result), trim(a)


def normalized(p):
    p = trim(p)
    scale = max(map(abs, p))
    return tuple(c/scale for c in p) if scale else p


def gcd(a, b):
    a, b = trim(a), trim(b)
    while b != (0,):
        a, b = b, normalized(divide(a, b)[1])
    return normalized(a)


def isolate_real_roots(coefficients, domain=(0, 1), width=F(1, 10**12), max_depth=96):
    """Sturm isolation on square-free exact coefficients, including endpoints.

    Returns every distinct root bracket or explicit unresolved brackets.
    Repeated roots are removed only after an exact polynomial GCD.
    """
    p = normalized(coefficients)
    a, b = map(F, domain)
    if a >= b or width <= 0 or max_depth < 0:
        raise ValueError('Invalid isolation domain or budget')
    if p == (0,):
        return {'roots': (), 'continuum': ((a, b),), 'unresolved': (), 'repeated_factor': ()}
    common = gcd(p, deriv(p))
    square_free = divide(p, common)[0]
    chain = [normalized(square_free), normalized(deriv(square_free))]
    if chain[-1] == (0,):
        chain.pop()
    else:
        while True:
            remainder = divide(chain[-2], chain[-1])[1]
            if remainder == (0,):
                break
            chain.append(normalized(tuple(-c for c in remainder)))

    def variations(t):
        values = [val(q, t) for q in chain]
        signs = [1 if x > 0 else -1 for x in values if x]
        return sum(x != y for x, y in zip(signs, signs[1:]))

    def count_open(lo, hi):
        return variations(lo) - variations(hi) - int(val(square_free, hi) == 0)

    roots = [(t, t) for t in (a, b) if val(p, t) == 0]
    pending = [(a, b, 0)]
    unresolved = []
    while pending:
        lo, hi, depth = pending.pop()
        count = count_open(lo, hi)
        if not count:
            continue
        if count == 1 and hi-lo <= width:
            roots.append((lo, hi))
            continue
        if depth >= max_depth:
            unresolved.append((lo, hi, count))
            continue
        mid = (lo+hi)/2
        if val(square_free, mid) == 0:
            roots.append((mid, mid))
        pending.extend(((lo, mid, depth+1), (mid, hi, depth+1)))
    return {'roots': tuple(sorted(set(roots))), 'continuum': (),
            'unresolved': tuple(unresolved), 'repeated_factor': common}


def project_curve(spline, point, use_domain, max_depth=96):
    """All stationary parameters plus both knot-side endpoints of an active use.

    Unknown use domains stay unresolved. Positive weights are a supported-input
    restriction. Constant curves are singular and remain unresolved.
    """
    if use_domain.interval is None:
        return ProjectionResult((), (), (), (('unknown_use_domain',),), False)
    lo, hi = use_domain.interval
    if lo < spline.domain[0] or hi > spline.domain[1]:
        raise ValueError('Use domain outside spline')
    point = tuple(F(str(x)) for x in point)
    if len(point) != 3:
        raise ValueError('Three dimensional point required')
    if any(p[3] <= 0 for p in spline.poles):
        return ProjectionResult((), (), (), ((lo, hi, 'unverified_weights'),), False)
    candidates, continuous, unresolved = [], [], []
    for a, b in spline.spans():
        left, right = max(a, lo), min(b, hi)
        if left >= right:
            continue
        pieces = spline.piece(a, b)
        w = pieces[3]
        numerator = [sub(mul(deriv(p), w), mul(p, deriv(w))) for p in pieces[:3]]
        if all(trim(p) == (0,) for p in numerator):
            unresolved.append((left, right, 'singular_constant_curve'))
            continue
        stationary = (F(0),)
        for p, d, q in zip(pieces[:3], numerator, point):
            stationary = add(stationary, mul(sub(p, tuple(q*c for c in w)), d))
        domain = ((left-a)/(b-a), (right-a)/(b-a))
        result = isolate_real_roots(stationary, domain, max_depth=max_depth)
        if result['continuum']:
            continuous.append((left, right))
        unresolved.extend((a+x*(b-a), a+y*(b-a), n) for x,y,n in result['unresolved'])
        brackets = list(result['roots']) + [(domain[0], domain[0]), (domain[1], domain[1])]
        for x,y in sorted(set(brackets)):
            t = a+(x+y)/2*(b-a)
            side = 'left' if t == right else 'right'
            xyz, _ = project(*spline.jet(t, side))
            distance = sum((v-q)**2 for v,q in zip(xyz,point))
            candidates.append(ProjectionCandidate((a+x*(b-a),a+y*(b-a)),t,distance,side,
                abs(val(stationary,(x+y)/2))))
    # Keep all stationary candidates. No unproved distance ranking discards ties.
    complete = not unresolved
    return ProjectionResult(tuple(candidates), tuple(candidates) if len(candidates)>1 else (),
                            tuple(continuous), tuple(unresolved), complete)


def project_planar_pcurve(pcurve, point, use_domain, origin, u_axis, v_axis):
    """Exact affine lift on a supplied plane; chart provenance belongs to caller."""
    origin, u_axis, v_axis = [tuple(F(str(x)) for x in v) for v in (origin,u_axis,v_axis)]
    poles = tuple(tuple(origin[i]*w+u_axis[i]*u+v_axis[i]*v for i in range(3))+(w,)
                  for u,v,_,w in pcurve.poles)
    return project_curve(Spline(pcurve.degree,pcurve.knots,poles),point,use_domain)


def unresolved_surface_projection(domain, reason='nonlinear_surface_lift_requires_interval_bounds'):
    """Do not apply polynomial completeness claims to an arbitrary surface lift."""
    return ProjectionResult((), (), (), ((domain,reason),), False)
