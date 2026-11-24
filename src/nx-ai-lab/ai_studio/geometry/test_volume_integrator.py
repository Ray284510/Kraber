"""Offline analytic fixtures, NOT NX evidence.
Run: python -m unittest ai_studio.geometry.test_volume_integrator
"""
import math
import unittest
from dataclasses import replace
from .volume_integrator import Patch, integrate

def domain():
    return dict(kind="full_rectangle",no_exclusions=True,single_cover=True,
                source="analytic fixture construction")
def topo(p):
    return dict(source="analytic boundary construction",closed=True,oriented=True,
                complete=True,senses={q.name:q.sense for q in p})

def box(offset=(0.,0.,0.)):
    lengths = (20.,30.,40.)
    patches = []
    for axis in range(3):
        j,k = (axis+1)%3,(axis+2)%3
        for side in (0,1):
            sign = 2*side-1
            def ev(u,v,axis=axis,j=j,k=k,side=side,sign=sign):
                x = list(offset)
                x[axis] += side*lengths[axis]
                x[j] += lengths[j]*(u-2.)/3.
                x[k] += lengths[k]*(v+1.)/2.
                du,dv = [0.]*3,[0.]*3
                du[j],dv[k] = lengths[j]/3.,lengths[k]/2.
                return x,du,dv,sign
            patches.append(Patch(str(axis)+str(side),(2.,5.,-1.,1.),ev,domain(),sign))
    return patches

def ellipsoid(axes=(3.,3.,3.),offset=(0.,0.,0.)):
    a,b,c = axes
    def ev(u,v):
        cu,su,cv,sv = math.cos(u),math.sin(u),math.cos(v),math.sin(v)
        return ((offset[0]+a*cv*cu,offset[1]+b*cv*su,offset[2]+c*sv),
                (-a*cv*su,b*cv*cu,0.),(-a*sv*cu,-b*sv*su,c*cv),1)
    return [Patch("curved",(0.,2.*math.pi,-math.pi/2.,math.pi/2.),ev,domain(),1)]

class Tests(unittest.TestCase):
    def test_box_jacobian_and_count(self):
        p = box()
        count = [0]
        wrapped = []
        for q in p:
            def ev(u,v,f=q.evaluator):
                count[0] += 1
                return f(u,v)
            wrapped.append(replace(q,evaluator=ev))
        r = integrate(wrapped,topology=topo(p),origin=(10.,15.,20.),abs_budget=1e-7)
        self.assertTrue(r["converged"])
        self.assertAlmostEqual(r["estimate"],20.*30.*40.,places=8)
        self.assertEqual(r["actual_evaluation_count"],count[0])
        self.assertEqual(count[0],6*(9+25))
        for f in r["face_contributions"]:
            self.assertAlmostEqual(f["contribution"],4000.,places=8)
        self.assertFalse(r["native_verified"])
        self.assertFalse(r["error_is_bound"])

    def test_sphere_ellipsoid_orders(self):
        for axes in ((3.,3.,3.),(2.,3.,5.)):
            p = ellipsoid(axes)
            expected = 4.*math.pi*math.prod(axes)/3.
            for orders in ((3,5),(4,6)):
                r = integrate(p,topology=topo(p),orders=orders,abs_budget=1e-6)
                self.assertTrue(r["converged"])
                self.assertLess(abs(r["estimate"]-expected),1e-6)
                t = r["refinement_trace"]
                self.assertGreater(len(t),1)
                self.assertLess(t[-1]["error_estimate"],t[0]["error_estimate"])

    def test_reversed_face(self):
        p = box()
        original = p[0].evaluator
        def reverse(u,v):
            x,du,dv,s = original(u,v)
            return x,du,dv,-s
        with self.assertRaisesRegex(ValueError,"orientation"):
            integrate([replace(p[0],evaluator=reverse)]+p[1:],topology=topo(p))
        with self.assertRaisesRegex(ValueError,"orientation"):
            integrate([replace(p[0],sense=1)]+p[1:],topology=topo(p))

    def test_domain_topology(self):
        p = box()
        for d in ({},dict(domain(),no_exclusions=False),
                  dict(domain(),kind="trimmed_narrow_hole")):
            with self.assertRaisesRegex(ValueError,"domain"):
                integrate([replace(p[0],domain=d)]+p[1:],topology=topo(p))
        for t in (None,dict(topo(p),closed=False)):
            with self.assertRaises(ValueError):
                integrate(p,topology=t)
        with self.assertRaises(ValueError):
            integrate(p[:-1],topology=topo(p))

    def test_limits(self):
        p = ellipsoid()
        r = integrate(p,topology=topo(p),max_depth=0,abs_budget=1e-10)
        self.assertFalse(r["converged"])
        self.assertEqual(r["stop_reason"],"max_depth")
        self.assertGreater(r["error_estimate"],1e-10)
        r = integrate(p,topology=topo(p),max_evaluations=34)
        self.assertEqual(r["actual_evaluation_count"],34)
        self.assertEqual(r["stop_reason"],"evaluation_limit")
        self.assertFalse(r["converged"])
        r = integrate(p,topology=topo(p),max_evaluations=1)
        self.assertIsNone(r["estimate"])
        self.assertEqual(r["actual_evaluation_count"],0)

    def test_translation_and_large_coordinate_guard(self):
        p = ellipsoid((2.,3.,5.))
        offset = (17.,-23.,41.)
        q = ellipsoid((2.,3.,5.),offset)
        a = integrate(p,topology=topo(p))
        b = integrate(q,topology=topo(q),origin=offset)
        self.assertTrue(a["converged"] and b["converged"])
        self.assertLess(abs(a["estimate"]-b["estimate"]),1e-8)
        offset = (1e12,-1e12,1e12)
        q = ellipsoid((2.,3.,5.),offset)
        c = integrate(q,topology=topo(q),origin=offset)
        self.assertGreater(c["actual_evaluation_count"],0)
        self.assertTrue(math.isfinite(c["estimate"]))
        self.assertGreater(c["floating_error_floor"],1e-6)
        self.assertFalse(c["converged"])
        self.assertFalse(c["native_verified"])

if __name__=="__main__":
    unittest.main()
