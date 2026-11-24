# Coordinator-side adapter, SOURCE ONLY / NOTRUN. Never trust partial evidence on error.
from ai_studio.geometry.volume_integrator import Patch
def box_patches(receipt):
    if receipt.get("ok") is not True or receipt.get("audit",{}).get("unchanged") is not True:
        raise ValueError("successful unchanged native boundary receipt required")
    boundary = receipt["boundary"]
    patches = []
    for row in boundary["patches"]:
        o, du, dv = tuple(row["origin"]), tuple(row["du"]), tuple(row["dv"])
        sense = row["sense"]
        def evaluator(u,v,o=o,du=du,dv=dv,sense=sense):
            return tuple(o[i]+u*du[i]+v*dv[i] for i in range(3)),du,dv,sense
        patches.append(Patch(row["name"],tuple(row["bounds"]),evaluator,row["domain"],sense))
    return patches,boundary["topology"]
# integrate() remains independent quadrature; its result remains native_verified=False.
