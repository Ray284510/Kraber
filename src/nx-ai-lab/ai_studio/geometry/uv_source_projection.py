"""Offline UV candidate inversion. Uses existing exact root core; no NX or I/O.

Bounding and roots refer to supplied decimal coefficients only. Final distances
are finite evaluations, not a global 3-D deviation bound or native use domain.
"""
from fractions import Fraction as F
from ai_studio.geometry.nurbs_ordered_topology import Spline,bernstein,sub,mul,deriv,add,val,project
from ai_studio.geometry.projection import isolate_real_roots,trim

class UVProjector:
    def __init__(self,degree,knots,poles):
        self.spline=Spline(degree,knots,poles);self.pieces=[]
        for a,b in self.spline.spans():
            ps=self.spline.piece(a,b);bs=[bernstein(p) for p in ps]
            assert min(bs[3])>0,'Unverified rational weights'
            # Degree-elevate polynomial coefficient lists before Bernstein conversion.
            n=max(len(p) for p in ps)
            bs=[bernstein(tuple(p)+(F(0),)*(n-len(p))) for p in ps]
            bounds=[(min(x/w for x,w in zip(bs[d],bs[3])),max(x/w for x,w in zip(bs[d],bs[3]))) for d in range(2)]
            self.pieces.append((a,b,ps,bounds))
    def project(self,uv):
        q=tuple(F(str(x)) for x in uv);candidates=[];unresolved=[];continuous=[];pruned=[]
        def point(ps,s):
            h=tuple(val(p,s) for p in ps);return tuple(h[d]/h[3] for d in range(2))
        def dist(ps,s):return sum((x-y)**2 for x,y in zip(point(ps,s),q))
        # Every seed is an actual curve endpoint, so this is a valid upper bound.
        best=min(dist(ps,s) for a,b,ps,box in self.pieces for s in (F(0),F(1)))
        ranked=[]
        for index,(a,b,ps,box) in enumerate(self.pieces):
            lower=sum((lo-x)**2 if x<lo else (x-hi)**2 if x>hi else F(0) for x,(lo,hi) in zip(q,box))
            ranked.append((lower,index))
        for lower,index in sorted(ranked):
            a,b,ps,box=self.pieces[index]
            # Keep all potentially coincident branches, not just the first best point.
            if lower>max(best,F(1,10**24)*2):
                pruned.append(index);continue
            w=ps[3];stat=(F(0),)
            for p,x in zip(ps[:2],q):
                dp=sub(mul(deriv(p),w),mul(p,deriv(w)))
                stat=add(stat,mul(sub(p,tuple(x*c for c in w)),dp))
            roots=isolate_real_roots(stat,max_depth=96)
            unresolved.extend([dict(span=index,bracket=[float(a+x*(b-a)),float(a+y*(b-a))],count=n) for x,y,n in roots['unresolved']])
            if roots['continuum']:continuous.append(dict(span=index,interval=[float(a),float(b)]))
            for x,y in sorted(set(roots['roots'])|{(F(0),F(0)),(F(1),F(1))}):
                z=(x+y)/2;t=a+z*(b-a);distance2=dist(ps,z);best=min(best,distance2)
                candidates.append(dict(t=float(t),span=index,parameter_bracket=[float(a+x*(b-a)),float(a+y*(b-a))],
                    knot_side='left' if z==1 else 'right',uv=[float(v) for v in point(ps,z)],
                    uv_distance_squared=float(distance2),stationary_residual=float(abs(val(stat,z)))))
        candidates.sort(key=lambda x:x['uv_distance_squared'])
        return dict(candidates=candidates,unresolved=unresolved,continuous=continuous,pruned_spans=pruned,
                    method='exact-rational Bernstein pruning and GCD/Sturm stationary isolation',
                    numerical_candidate_scope_complete=not unresolved,original_use_domain=None)
