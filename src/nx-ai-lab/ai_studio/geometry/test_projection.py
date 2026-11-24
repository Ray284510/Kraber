import unittest
from fractions import Fraction as F
from math import comb
from .nurbs_ordered_topology import Spline, project, mul
from .projection import isolate_real_roots, project_curve, project_planar_pcurve, unresolved_surface_projection
from .core_types import EdgeUseDomain, EvaluatorDomain, ParamMap, PeriodicChart, GeometryObjectId, TopologyUseId, NativeReceipt, GeometryWitness


def polynomial(roots):
    p = (F(1),)
    for r in roots:
        p = mul(p, (-F(r), F(1)))
    return p


def spline_from_power(p):
    degree = len(p)-1
    bernstein = [sum(p[i]*F(comb(k,i),comb(degree,i)) for i in range(k+1)) for k in range(degree+1)]
    return Spline(degree,(0,)*(degree+1)+(1,)*(degree+1),tuple((x,0,0,1) for x in bernstein))


def domain(a=0,b=1):
    return EdgeUseDomain((F(a),F(b)),'synthetic-explicit-domain')


class ProjectionTests(unittest.TestCase):
    def test_sixfold_root(self):
        r=isolate_real_roots(polynomial([F(1,2)]*6))
        self.assertEqual(r['roots'],((F(1,2),F(1,2)),))
        self.assertFalse(r['unresolved'])
        self.assertGreater(len(r['repeated_factor']),1)

    def test_near_multiple_roots(self):
        roots=[F(1,2),F(1,2)+F(1,10**8)]
        r=isolate_real_roots(polynomial(roots))
        self.assertFalse(r['unresolved'])
        self.assertEqual(len(r['roots']),2)
        for target in roots:self.assertTrue(any(a<=target<=b for a,b in r['roots']))

    def test_four_zero_parameters(self):
        roots=list(map(F,('0.05','0.1','0.15','0.2')))
        s=spline_from_power(polynomial(roots))
        result=project_planar_pcurve(s,(0,0,0),domain(),(0,0,0),(1,0,0),(0,1,0))
        self.assertTrue(result.candidate_complete)
        for t in roots:
            self.assertTrue(any(c.parameter_interval[0]<=t<=c.parameter_interval[1] for c in result.multiple_candidates))
        self.assertFalse(result.minimum_certified)

    def test_circle_arc_continuum(self):
        s=Spline(2,(0,0,0,1,1,1),((1,0,0,1),(1,1,0,1),(0,2,0,2)))
        r=project_curve(s,(0,0,0),domain())
        self.assertEqual(r.continuum_solution,((F(0),F(1)),))
        self.assertTrue(r.candidate_complete)

    def test_trim_domain(self):
        r=project_curve(spline_from_power((F(0),F(1))),(.05,0,0),domain('.2','.8'))
        self.assertTrue(r.candidate_complete)
        self.assertEqual(min(r.isolated_candidate,key=lambda c:c.squared_distance).parameter,F('.2'))

    def test_rational_position_derivative(self):
        s=Spline(2,(0,0,0,1,1,1),((1,0,0,1),(1,1,0,1),(0,2,0,2)))
        for t in (F(0),F(1,3),F(1)):
            p,d=project(*s.jet(t));w=1+t*t
            self.assertEqual(p,((1-t*t)/w,2*t/w,0))
            self.assertEqual(d,(-4*t/(w*w),2*(1-t*t)/(w*w),0))

    def test_repeated_node_sides(self):
        s=Spline(1,(0,0,F('.5'),F('.5'),1,1),((0,0,0,1),(1,0,0,1),(1,0,0,1),(1,1,0,1)))
        self.assertEqual(project(*s.jet(F('.5'),'left'))[1],(2,0,0))
        self.assertEqual(project(*s.jet(F('.5'),'right'))[1],(0,2,0))
        r=project_curve(s,(1,0,0),domain())
        self.assertTrue(r.candidate_complete)
        self.assertEqual({c.side for c in r.isolated_candidate if c.parameter==F('.5')},{'left','right'})

    def test_periodic_phase(self):
        c=PeriodicChart(F(1),F('.25'),'explicit')
        self.assertEqual(c.unwrap(F('.9'),-1),F('.15'))
        with self.assertRaises(ValueError):PeriodicChart(None,None,None).unwrap(0,0)

    def test_distinct_face_uses(self):
        obj=GeometryObjectId('hash','session','edge','shared')
        a=TopologyUseId(obj,'face1','loop1','fin1','edge',0)
        b=TopologyUseId(obj,'face2','loop2','fin2','edge',0)
        self.assertNotEqual(a,b)
        self.assertIsNone(EdgeUseDomain(None,None,a).interval)

    def test_reverse_map(self):
        m=ParamMap('forward','reverse',domain(),domain(),F(-1),F(1),'native-use-fixture')
        self.assertEqual(m.apply(F('.2')),F('.8'))
        self.assertEqual(m.apply(m.apply(F('.2'))),F('.2'))

    def test_face_valid_edge_external_keeps_topology(self):
        obj=GeometryObjectId('hash','session','edge','shared')
        use=TopologyUseId(obj,'face','loop','fin','edge',0)
        receipt=NativeReceipt('hash','session','12.0.2.9','synthetic',0,'test')
        w=GeometryWitness(use,(1,1,1),2,0.,receipt)
        self.assertEqual(w.topology_incidence,use)
        self.assertEqual(w.edge_point_classification,2)

    def test_singular_unresolved(self):
        s=Spline(1,(0,0,1,1),((1,0,0,1),(1,0,0,1)))
        r=project_curve(s,(0,0,0),domain())
        self.assertFalse(r.candidate_complete)
        self.assertTrue(r.unresolved_interval)

    def test_unknown_use_and_nonlinear_lift(self):
        r=project_curve(spline_from_power((0,1)),(0,0,0),EdgeUseDomain(None,None))
        self.assertFalse(r.candidate_complete)
        self.assertTrue(unresolved_surface_projection((0,1)).unresolved_interval)

    def test_depth_budget_reports_unresolved(self):
        r=isolate_real_roots((-2,0,3),max_depth=0)
        self.assertTrue(r['unresolved'])

    def test_scaled_coefficients(self):
        p=polynomial([F(1,2)]*6)
        self.assertEqual(isolate_real_roots([c*10**80 for c in p])['roots'],((F(1,2),F(1,2)),))


if __name__=='__main__':unittest.main()
