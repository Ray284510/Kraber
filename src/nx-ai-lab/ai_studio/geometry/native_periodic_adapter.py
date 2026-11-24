"""Periodic annular fixture integration; validated cap results are injected.
No NX import, file writes, model changes or executable CLI.
cap_integral(cap_receipt, origin, abs_budget) -> {
  "estimate": float, "estimated_error": nonnegative float, "converged": bool}
The coordinator must bind this to the existing validated cap integration,
including origin-dependent flux, and retain its full original evidence.
"""
import json
import math
import numpy as np

def _v(x):
    x = np.asarray(x, dtype=float)
    if x.shape != (3,) or not np.isfinite(x).all():
        raise ValueError("finite native 3-vector required")
    return x

def _need(b, msg):
    if not b:
        raise ValueError(msg)

def integrate_periodic(receipt, cap_integral, abs_budget=0.01, max_depth=6):
    _need(receipt.get("ok") is True and receipt.get("unchanged") is True
          and receipt.get("read_only_query") is True, "native audited receipt required")
    b = receipt["boundary"]
    _need(b["family"] == "checked_annular_analytic_fixture", "fixture scope")
    cap = json.loads(b["cap_receipt_json"])
    _need(cap.get("ok") is True, "existing cap query failed")
    _need(math.isfinite(abs_budget) and 0 < abs_budget <= 0.1, "budget")
    _need(type(max_depth) is int and 1 <= max_depth <= 8, "bounded refinement")
    sides = b["sides"]
    _need(len(sides) == 2 and len({x["face_tag"] for x in sides}) == 2, "side coverage")
    cancel = b["boundary_cancellation"]
    _need(len(cancel) == 4 and len({x["edge_tag"] for x in cancel}) == 4, "edge coverage")
    for row in cancel:
        _need(row["cap_sense_in_common_circle_frame"] in (-1, 1)
              and row["cap_sense_in_common_circle_frame"] + row["side_sense_in_common_circle_frame"] == 0,
              "oriented cancellation")
    prepared = []
    for side in sides:
        _need(side["type"] == 16 and side["orientation"] in (-1, 1), "analytic cylinder orientation")
        c, x, y, d = (_v(side[k]) for k in ("center", "x_axis", "y_axis", "delta"))
        r = float(side["radius"])
        _need(math.isfinite(r) and r > 0 and np.linalg.norm(d) > 0, "native geometry")
        sg = side["orientation"]
        # Recheck exported native correspondence and derivatives before integration.
        _need(len(side["witnesses"]) == 85, "complete witness grid")
        for w in side["witnesses"]:
            t, s = w["theta"], w["s"]
            point = c + r*(math.cos(t)*x + math.sin(t)*y) + s*d
            dt = r*(-math.sin(t)*x + math.cos(t)*y)
            _need(np.linalg.norm(point-_v(w["point"])) <= 1e-7, "native point mismatch")
            cross = np.cross(_v(w["du"]), _v(w["dv"]))
            normal = np.cross(dt, d)
            _need(np.linalg.norm(cross) > 0 and np.linalg.norm(normal) > 0, "singular derivative")
            _need(abs(np.dot(cross, normal)/(np.linalg.norm(cross)*np.linalg.norm(normal))) > 1-1e-8,
                  "native tangent plane")
            if 0 < s < 1:
                _need(w["orientation"] == sg and
                      (w["plus_status"], w["minus_status"]) == ((2,1) if sg == 1 else (1,2)),
                      "containment orientation")
        prepared.append((side["face_tag"], c, x, y, d, r, sg))

    def rule(data, origin, n, level):
        _, c, x, y, d, r, sg = data
        nodes, weights = np.polynomial.legendre.leggauss(n)
        pieces = 2**level
        sums = []
        for i in range(pieces):
            t0, t1 = 2*math.pi*i/pieces, 2*math.pi*(i+1)/pieces
            for j in range(pieces):
                s0, s1 = j/pieces, (j+1)/pieces
                for a, wa in zip(nodes, weights):
                    t = (t0+t1)/2 + a*(t1-t0)/2
                    dt = r*(-math.sin(t)*x+math.cos(t)*y)
                    area = sg*np.cross(dt, d)
                    for z, wz in zip(nodes, weights):
                        s = (s0+s1)/2+z*(s1-s0)/2
                        point = c+r*(math.cos(t)*x+math.sin(t)*y)+s*d
                        weight = wa*wz*(t1-t0)*(s1-s0)/4
                        sums.append(float(np.dot(point-origin,area))*weight/3)
        return math.fsum(sums)

    def side_integral(data, origin):
        history = []
        prior = None
        for level in range(max_depth+1):
            lo = rule(data, origin, 3, level)
            hi = rule(data, origin, 5, level)
            # Paired quadrature + cross-level estimate; never a certified bound.
            err = max(abs(hi-lo), abs(hi-prior) if prior is not None else math.inf,
                      128*np.finfo(float).eps*max(1,abs(hi)))
            history.append({"level":level,"low":lo,"high":hi,"estimated_error":None if not math.isfinite(err) else err})
            if level >= 1 and err <= abs_budget/8:
                return {"face_tag":data[0],"estimate":hi,"estimated_error":err,
                        "converged":True,"history":history}
            prior = hi
        return {"face_tag":data[0],"estimate":hi,"estimated_error":err,
                "converged":False,"history":history}

    def complete(origin):
        ss = [side_integral(d, origin) for d in prepared]
        cc = cap_integral(cap, tuple(origin), abs_budget/4)
        _need(cc.get("converged") is True and math.isfinite(cc["estimate"])
              and math.isfinite(cc["estimated_error"]) and cc["estimated_error"] >= 0,
              "validated cap callback must converge")
        value = math.fsum([cc["estimate"]] + [s["estimate"] for s in ss])
        err = math.fsum([cc["estimated_error"]] + [s["estimated_error"] for s in ss])
        return {"origin":list(origin),"estimate":value,"estimated_error":err,
                "converged":all(s["converged"] for s in ss) and err <= abs_budget,
                "caps":cc,"sides":ss}
    zero = complete(np.zeros(3))
    # Non-axial independent flux-origin check, scaled from actual native span/radius.
    scale = max(np.linalg.norm(d[4])+d[5] for d in prepared)
    shifted = complete(prepared[0][1]+scale*np.array([0.173,-0.291,0.419]))
    gap = abs(zero["estimate"]-shifted["estimate"])
    tolerance = zero["estimated_error"]+shifted["estimated_error"]+1e-9
    return {"schema_version":1,"part_path":b["part_path"],"source_sha256":b["source_sha256"],
            "scope":"Exact checked annular analytic fixture; native geometry flux quadrature",
            "zero_origin":zero,"shifted_origin":shifted,"origin_shift_difference":gap,
            "numerically_converged":zero["converged"] and shifted["converged"] and gap <= tolerance,
            "error_kind":"empirical paired-rule and refinement estimate, not rigorous bound",
            "nx_mass_comparison":"PENDING coordinator","cold_reopen":"PENDING coordinator",
            "complex_original_and_step":"PENDING separate integrations"}


def integrate_with_verified_caps(receipt, prior_caps, *, abs_budget=1e-6):
    """Recheck fresh cap evidence and reuse the previously accepted areas."""
    from ai_studio.geometry.native_annular_adapter import integrate_receipt

    sha256 = receipt["boundary"]["source_sha256"]
    _need(prior_caps["part_sha256"] == sha256, "historical cap source hash differs")

    def plane_key(cap):
        face = cap["result"]
        return tuple(face["normal"]), face["plane_height"]

    cached = {plane_key(cap): cap["result"] for cap in prior_caps["caps"]}
    _need(len(cached) == 2, "two distinct verified cap planes required")

    def caps(cap_receipt, origin, budget):
        fresh = integrate_receipt(cap_receipt)
        _need(fresh["part_sha256"] == sha256, "fresh cap source hash differs")
        rows = []
        for cap in fresh["caps"]:
            key = plane_key(cap)
            _need(key in cached, "verified cap plane changed")
            prior = cached[key]
            _need(abs(cap["result"]["area"] - prior["area"]) < 1e-9,
                  "fresh cap area differs from verified result")
            height = prior["plane_height"] - math.fsum(
                x*y for x, y in zip(origin, prior["normal"]))
            rows.append(dict(face_tag=cap["face_tag"], source_previous_hash=sha256,
                             estimate=prior["area"]*height/3,
                             estimated_error=prior["area_error_estimate"]*abs(height)/3,
                             prior_convergence=prior["convergence"]))
        error = math.fsum(row["estimated_error"] for row in rows)
        return dict(estimate=math.fsum(row["estimate"] for row in rows),
                    estimated_error=error, converged=error <= budget, faces=rows)

    return integrate_periodic(receipt, caps, abs_budget=abs_budget)
