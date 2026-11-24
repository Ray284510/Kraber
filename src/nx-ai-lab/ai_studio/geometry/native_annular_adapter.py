"""B2 receipt adapter; source only. No NX calls or mass-derived area."""
from math import cos, sin, pi, isfinite
from ai_studio.geometry.trimmed_planar import Edge, Loop, Topology, integrate_face

from pathlib import Path

FIXTURE = str(Path(__file__).resolve().parents[2] / 'models' / 'phase4_phase5_staged_20261108_212227' / 'independent_volume_20261109' / 'phase4_iv_b2_annular_20261110_v1.prt')

def integrate_receipt(receipt):
    def need(test, message):
        if not test:
            raise ValueError(message)
    need(receipt.get("ok") is True and receipt.get("read_only_query") is True,
         "successful native query required")
    need(receipt.get("command") == "nx_independent_annular_caps", "wrong command")
    audit = receipt["audit"]
    need(audit["unchanged"] is True and audit["modified_before"] == audit["modified_after"] == 0
         and audit["sha256_before"] == audit["sha256_after"]
         and audit["objects_before"] == audit["objects_after"], "read-only audit failed")
    b = receipt["boundary"]
    need(b["schema_version"] == 1 and b["family"] == "annular_planar_caps_only"
         and b["units"] == "mm" and b["part_path"].lower() == FIXTURE.lower(),
         "wrong fixture/schema")
    need(b["native_mass_used"] is False and len(b["caps"]) == 2
         and b["consistency_fault_count"] == b["failed_feature_count"] == 0,
         "invalid native evidence")
    results = []
    seen_faces, seen_edges = set(), set()
    for cap in b["caps"]:
        need(cap["face_tag"] not in seen_faces, "duplicate cap")
        seen_faces.add(cap["face_tag"])
        need(len(cap["loops"]) == 2 and
             [v["role"] for v in cap["loops"]] == ["outer", "hole"], "wrong loops")
        loops = []
        for row in cap["loops"]:
            need(row["edge_tag"] not in seen_edges, "duplicate edge use")
            seen_edges.add(row["edge_tag"])
            c, a, y = (tuple(row[key]) for key in ("center", "x_axis", "y_axis"))
            r = row["radius"]
            need(len(c) == len(a) == len(y) == 3 and
                 all(isfinite(v) for v in (*c, *a, *y, r)) and r > 0,
                 "invalid circular geometry")
            need(row["periodic"] is True and all(
                abs(row[key][1]-row[key][0]-2*pi) < 1e-10
                for key in ("arc_limits", "evaluator_limits")), "not full circle")
            need((row["start"], row["end"]) in ((0.0, 2*pi), (2*pi, 0.0)),
                 "invalid analytic span")
            def evaluate(t, c=c, a=a, y=y, r=r):
                return (tuple(c[i]+r*(a[i]*cos(t)+y[i]*sin(t)) for i in range(3)),
                        tuple(r*(-a[i]*sin(t)+y[i]*cos(t)) for i in range(3)))
            edge = Edge(row["edge_tag"], evaluate, row["start"], row["end"])
            loops.append(Loop(row["edge_tag"], "inner" if row["role"] == "hole" else "outer", (edge,),
                              row["closure_evidence"], row["sense_evidence"]))
        topology = Topology(**cap["topology"])
        # Deterministic offline convergence check, no NX operation or billing.
        runs = [integrate_face(tuple(loops), cap["point"], cap["normal"], topology,
                               abs_tol=tol, rel_tol=1e-11, min_depth=depth)
                for tol, depth in ((1e-7, 2), (1e-9, 3))]
        delta = abs(runs[1]["area"]-runs[0]["area"])
        need(delta <= runs[0]["area_error_estimate"]+
             runs[1]["area_error_estimate"]+1e-9, "refinement disagreement")
        results.append(dict(face_tag=cap["face_tag"], result=runs[-1],
                            refinement_runs=runs, area_change_mm2=delta))
    return dict(schema_version=1, part_sha256=audit["sha256_before"], caps=results,
                whole_body_volume_computed=False, global_body_closure_proven=False,
                nx_native_accuracy2_required=True, step_volume_gate_mm3=0.1,
                error_scope="Quadrature and roundoff heuristics only; excludes geometry, topology and evaluator uncertainty.")
