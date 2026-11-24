"""SOURCE ONLY / NOTRUN. Native analytic receipt -> independent flux patches."""
import math
from ai_studio.geometry.volume_integrator import Patch

def analytic_patches(receipt):
    if (receipt.get("ok") is not True
        or receipt.get("command") != "nx_independent_analytic_boundary"
        or receipt.get("audit", {}).get("unchanged") is not True):
        raise ValueError("successful unchanged analytic receipt required")
    d = receipt["boundary"]
    if d.get("schema_version") != 1 or d.get("units") != "mm":
        raise ValueError("unsupported schema/units")
    rows, topo = d["patches"], d["topology"]
    kinds = sorted(row["kind"] for row in rows)
    expected = {"sphere": ["sphere"], "cylinder": ["cylinder", "disk", "disk"]}
    if d.get("family") not in expected or kinds != expected[d["family"]]:
        raise ValueError("incomplete primitive boundary")
    if any(topo.get(k) is not True for k in ("closed", "oriented", "complete")):
        raise ValueError("unproven topology")
    def vector(v):
        v = tuple(float(x) for x in v)
        if len(v) != 3 or not all(math.isfinite(x) for x in v):
            raise ValueError("invalid vector")
        return v
    patches = []
    for row in rows:
        o,a,b,w = (vector(row[k]) for k in ("origin","a","b","axis"))
        frame = (a,b,w)
        for i in range(3):
            for j in range(3):
                if abs(math.fsum(frame[i][k]*frame[j][k] for k in range(3))-(i==j))>1e-9:
                    raise ValueError("nonorthonormal frame")
        cross = (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
        if math.fsum(cross[k]*w[k] for k in range(3)) < 1-1e-9:
            raise ValueError("left-handed frame")
        r,h = float(row["radius"]),float(row["height"])
        kind,s = row["kind"],row["sense"]
        if not math.isfinite(r) or r<=0 or not math.isfinite(h):
            raise ValueError("invalid dimensions")
        if type(s) is not int or s not in (-1,1) or (kind!="disk" and s!=1):
            raise ValueError("invalid orientation")
        bounds = (0.,2*math.pi,-math.pi/2,math.pi/2) if kind=="sphere" else (0.,2*math.pi,0.,h if kind=="cylinder" else r)
        if tuple(row["bounds"]) != bounds or bounds[3]<=bounds[2]:
            raise ValueError("invalid parameter domain")
        def evaluate(u,v,o=o,a=a,b=b,w=w,r=r,kind=kind,s=s):
            radial = tuple(math.cos(u)*a[k]+math.sin(u)*b[k] for k in range(3))
            tangent = tuple(-math.sin(u)*a[k]+math.cos(u)*b[k] for k in range(3))
            if kind=="sphere":
                x = tuple(o[k]+r*(math.cos(v)*radial[k]+math.sin(v)*w[k]) for k in range(3))
                du = tuple(r*math.cos(v)*tangent[k] for k in range(3))
                dv = tuple(r*(-math.sin(v)*radial[k]+math.cos(v)*w[k]) for k in range(3))
            elif kind=="cylinder":
                x = tuple(o[k]+r*radial[k]+v*w[k] for k in range(3))
                du,dv = tuple(r*t for t in tangent),w
            else:
                x = tuple(o[k]+v*radial[k] for k in range(3))
                du,dv = tuple(v*t for t in tangent),radial
            return x,du,dv,s
        patches.append(Patch(row["name"],bounds,evaluate,row["domain"],s))
    if topo.get("senses") != {p.name:p.sense for p in patches} or len({p.name for p in patches})!=len(patches):
        raise ValueError("face/sense coverage mismatch")
    return patches,topo
# integrate(patches,topology=topology,origin=receipt["boundary"]["center"],...)
# Repeat with origin=(0,0,0), multiple rule orders and translated/rotated fixtures.
# Numerical errors remain estimates; native_verified remains False in the kernel.
