"""Offline tests: python -m unittest ai_studio.geometry.test_trimmed_planar"""
import math
import unittest
from dataclasses import replace
from ai_studio.geometry.trimmed_planar import (
    Edge, Loop, Topology, integrate_face, TrimError, Cancelled, ConvergenceError)

EVIDENCE = Topology(
    "Analytic concentric circles: one outer boundary and one hole, each once.",
    "Each nonzero circle is simple over exactly one revolution.",
    "Distinct positive radii give disjoint boundaries.",
    "Smaller concentric disk lies strictly inside larger disk.")

def annulus(center=(0.,0.,6.), warped=False):
    def loop(radius, role, sign):
        def curve(t):
            # Native derivative is dx/dt even when edge bounds descend.
            theta = 2*math.pi*(t**3 if warped else t)
            speed = 6*math.pi*t*t if warped else 2*math.pi
            return ((center[0]+radius*math.cos(theta),
                     center[1]+radius*math.sin(theta), center[2]),
                    (-radius*math.sin(theta)*speed,
                     radius*math.cos(theta)*speed, 0.))
        edge = Edge(role, curve, 0. if sign == 1 else 1.,
                    1. if sign == 1 else 0.)
        return Loop(role, role, (edge,),
                    "Analytic full revolution has matching endpoints.",
                    "Increasing angle is CCW about +Z; hole bounds descend.")
    return (loop(5., "outer", 1), loop(2., "inner", -1))

class PlanarTests(unittest.TestCase):
    def test_annulus_independent_closed_form(self):
        result = integrate_face(annulus(), (0,0,6), (0,0,1), EVIDENCE)
        self.assertAlmostEqual(result["area"], 21*math.pi, places=10)
        self.assertAlmostEqual(result["flux"], 42*math.pi, places=10)
        self.assertAlmostEqual(result["loops"][0]["flux"], 50*math.pi, places=10)
        self.assertAlmostEqual(result["loops"][1]["flux"], -8*math.pi, places=10)
        self.assertGreaterEqual(result["leaf_panels"], 8)
        self.assertFalse(result["error_is_certified_bound"])
        self.assertFalse(result["global_body_closure_proven"])

    def test_translated_plane_and_warped_parameter(self):
        center = (100., -200., -9.)
        result = integrate_face(annulus(center, True), center, (0,0,7), EVIDENCE)
        self.assertAlmostEqual(result["area"], 21*math.pi, places=9)
        self.assertAlmostEqual(result["flux"], -63*math.pi, places=9)
        self.assertLessEqual(result["area_error_estimate"],
                             max(1e-9, 1e-10*result["area"]))

    def test_vertical_plane(self):
        loops = annulus()
        changed = []
        for loop in loops:
            old = loop.edges[0]
            def rotate(t, old=old):
                x, d = old.evaluate(t)
                return (x[2], x[0]+10, x[1]-20), (d[2], d[0], d[1])
            changed.append(replace(loop, edges=(replace(old, evaluate=rotate),)))
        result = integrate_face(changed, (6,10,-20), (1,0,0), EVIDENCE)
        self.assertAlmostEqual(result["flux"], 42*math.pi, places=10)

    def test_reject_missing_evidence(self):
        with self.assertRaises(TrimError):
            integrate_face(annulus(), (0,0,6), (0,0,1),
                           replace(EVIDENCE, coverage_evidence="unproven"))

    def test_reject_wrong_hole_sense(self):
        loops = list(annulus())
        edge = loops[1].edges[0]
        loops[1] = replace(loops[1], edges=(replace(edge, start=0., end=1.),))
        with self.assertRaises(TrimError):
            integrate_face(loops, (0,0,6), (0,0,1), EVIDENCE)

    def test_reject_gap_and_nonplanarity(self):
        loops = list(annulus())
        edge = loops[0].edges[0]
        loops[0] = replace(loops[0], edges=(replace(edge, end=.9),))
        with self.assertRaises(TrimError):
            integrate_face(loops, (0,0,6), (0,0,1), EVIDENCE)
        with self.assertRaises(TrimError):
            integrate_face(annulus(), (0,0,5), (0,0,1), EVIDENCE)

    def test_cancel_and_budget(self):
        with self.assertRaises(Cancelled):
            integrate_face(annulus(), (0,0,6), (0,0,1), EVIDENCE,
                           cancel=lambda: True)
        with self.assertRaises(ConvergenceError):
            integrate_face(annulus(), (0,0,6), (0,0,1), EVIDENCE,
                           max_evaluations=1)

if __name__ == "__main__":
    unittest.main()
