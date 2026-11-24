"""Independent flux integration for the checked rectangular two-bore fixture."""
import math
import numpy as np
from .trimmed_planar import Edge, Loop, Topology, integrate_face

TOL = 1e-7


def need(condition, message):
    if not condition:
        raise ValueError(message)


def near(a, b):
    return np.linalg.norm(np.asarray(a)-np.asarray(b)) < TOL


def circle_curve(circle):
    c, x, y = (np.asarray(circle[k], float) for k in ('center', 'x', 'y'))
    r = circle['radius']
    def evaluate(t):
        return c+r*(math.cos(t)*x+math.sin(t)*y), r*(-math.sin(t)*x+math.cos(t)*y)
    return evaluate


def rectangle(edges, normal):
    first, *rest = edges
    pending = list(rest)
    ordered = [(first, np.asarray(first['start']), np.asarray(first['end']), 1)]
    while pending:
        end = ordered[-1][2]
        matches = [(e, np.asarray(e['start']), np.asarray(e['end']), 1) for e in pending if near(e['start'], end)]
        matches += [(e, np.asarray(e['end']), np.asarray(e['start']), -1) for e in pending if near(e['end'], end)]
        need(len(matches) == 1, 'ambiguous linear boundary ordering')
        ordered.append(matches[0]); pending.remove(matches[0][0])
    need(near(ordered[-1][2], ordered[0][1]), 'open rectangle boundary')
    u, v = ordered[0][2]-ordered[0][1], ordered[1][2]-ordered[1][1]
    need(near(ordered[2][2]-ordered[2][1], -u) and near(ordered[3][2]-ordered[3][1], -v)
         and abs(np.dot(u,v)) < TOL and abs(np.dot(np.cross(u,v), normal)) > TOL, 'nonrectangular loop')
    if np.dot(np.cross(u,v),normal) < 0:
        ordered = [(e,b,a,-sense) for e,a,b,sense in reversed(ordered)]
    return ordered


def cylinder_flux(curve, delta, sense, origin):
    history = []
    previous = None
    for level in range(5):
        parts = 2**level
        values = []
        for order in (3,5):
            nodes, weights = np.polynomial.legendre.leggauss(order)
            terms = []
            for i in range(parts):
                for u, wu in zip(nodes,weights):
                    t = 2*math.pi*(i+(u+1)/2)/parts
                    point, derivative = curve(t)
                    area = sense*np.cross(derivative,delta)
                    for j in range(parts):
                        for v,wv in zip(nodes,weights):
                            s = (j+(v+1)/2)/parts
                            terms.append(float(np.dot(point+s*delta-origin,area))*wu*wv*math.pi/(6*parts**2))
            values.append(math.fsum(terms))
        lo, hi = values
        error = None if previous is None else max(abs(lo-hi),abs(previous-hi),128*np.finfo(float).eps*max(1,abs(hi)))
        history.append(dict(level=level,low=lo,high=hi,estimated_error=error))
        if error is not None and error < 1e-7:
            return dict(estimate=hi,estimated_error=error,convergence=history)
        previous=hi
    raise ValueError('cylindrical flux did not converge')


def integrate_multihole(native, origin=(0,0,0)):
    need(native['ok'] and native['unchanged'], 'audited native receipt required')
    origin=np.asarray(origin,float)
    faces={f['tag']:f for f in native['faces']}; edges={e['tag']:e for e in native['edges']}
    need(len(faces)==8 and len(edges)==16, 'fixture topology mismatch')
    planar=[f for f in faces.values() if f['type']==22]; cylinders=[f for f in faces.values() if f['type']==16]
    caps=[f for f in planar if len(f['edges'])==6]
    need(len(planar)==6 and len(caps)==2 and len(cylinders)==2, 'fixture face families mismatch')
    uses={tag:[] for tag in edges}; results=[]
    for tag,e in edges.items():
        actual={f['tag'] for f in faces.values() if tag in f['edges']}
        need(len(actual)==2 and actual==set(e['faces']), 'nonreciprocal native incidence')
    for f in planar:
        n=np.asarray(f['normal']); point=np.asarray(f['point']); local=[edges[t] for t in f['edges']]
        ring=rectangle([e for e in local if e['kind']=='Linear'],n)
        need(all(abs(np.dot(a-point,n))<TOL and abs(np.dot(b-point,n))<TOL for _,a,b,_ in ring),'noncoplanar rectangle')
        linear=[]
        for e,a,b,sg in ring:
            def line(t,a=a,b=b):return a+t*(b-a)-origin,b-a
            linear.append(Edge(e['tag'],line,0.,1.));uses[e['tag']].append((f['tag'],sg))
        loops=[Loop('outer','outer',tuple(linear),'ordered endpoint joins','positive normal orientation')]
        holes=[e for e in local if e['kind']=='Circular']
        need(len(holes)==(2 if f in caps else 0),'unexpected cap boundary count')
        for e in holes:
            c=e['circle']; center=np.asarray(c['center']); radius=c['radius']; axis=np.cross(c['x'],c['y']);dot=np.dot(axis,n)
            need(abs(abs(dot)-1)<TOL and abs(np.dot(center-point,n))<TOL,'circle off cap plane')
            for _,a,b,_ in ring:
                need(np.dot(np.cross(b-a,center-a),n)/np.linalg.norm(b-a)>radius+TOL,'hole not strictly inside outer rectangle')
            sg=-1 if dot>0 else 1; uses[e['tag']].append((f['tag'],sg));curve=circle_curve(c)
            def shifted(t,curve=curve):
                p,d=curve(t);return p-origin,d
            limits=(0.,2*math.pi) if sg==1 else (2*math.pi,0.)
            loops.append(Loop(e['tag'],'inner',(Edge(e['tag'],shifted,*limits),),'native full periodic circle','negative normal orientation'))
        if holes:
            a,b=[e['circle'] for e in holes]
            need(np.linalg.norm(np.asarray(a['center'])-b['center'])>a['radius']+b['radius']+TOL,'inner rings intersect')
        evidence=Topology('complete reciprocal boundary edges','ordered rectangle and native full circles','strict distance separation','circles strictly inside convex rectangle')
        q=integrate_face(loops,point-origin,n,evidence)
        results.append(dict(face_tag=f['tag'],kind='cap' if f in caps else 'outer_wall',estimate=q['flux'],estimated_error=q['flux_error_estimate'],ordered_edge_tags=[[e.name for e in loop.edges] for loop in loops],detail=q))
    for f in cylinders:
        a,b=[edges[t] for t in f['edges']];c,d=a['circle'],b['circle'];delta=np.asarray(d['center'])-c['center'];axis=delta/np.linalg.norm(delta)
        ca=np.cross(c['x'],c['y']);cb=np.cross(d['x'],d['y']); n=np.asarray(f['normal']); native_axis=np.asarray(f['axis'],dtype=float);native_axis/=np.linalg.norm(native_axis)
        off=np.asarray(c['center'])-f['axis_point']
        need(abs(c['radius']-d['radius'])<TOL and abs(c['radius']-f['radius'])<TOL and all(abs(abs(np.dot(k,axis))-1)<TOL for k in (ca,cb,native_axis)) and np.linalg.norm(np.cross(off,axis))<TOL,'native cylinder and loops disagree')
        need(len(set(a['faces']+b['faces']) & {q['tag'] for q in caps})==2 and f['periodic'],'side must span both caps with one period')
        curve=circle_curve(c);p,dt=curve(0);area=np.cross(dt,delta);sg=1 if np.dot(area,n)>0 else -1
        need(near(p+delta/2,f['point']) and abs(abs(np.dot(area,n))/np.linalg.norm(area)-1)<TOL and np.dot(n,c['x'])<-.999999,'hole witness orientation mismatch')
        uses[a['tag']].append((f['tag'],sg));uses[b['tag']].append((f['tag'],-sg*(1 if np.dot(ca,cb)>0 else -1)))
        results.append(dict(face_tag=f['tag'],kind='bore_wall',radius_mm=c['radius'],**cylinder_flux(curve,delta,sg,origin)))
    for tag,directions in uses.items():
        need(len(directions)==2 and {f for f,_ in directions}==set(edges[tag]['faces']) and sum(s for _,s in directions)==0,'oriented boundary does not cancel')
    return dict(origin=origin.tolist(),sha256=native['sha256'],faces=results,edge_cancellation=uses,estimate=math.fsum(f['estimate'] for f in results),estimated_error=math.fsum(f['estimated_error'] for f in results),closure_scope='Checked eight-face analytic fixture; exact line and circle boundary classes and oriented reciprocal incidence',error_is_certified_bound=False)
