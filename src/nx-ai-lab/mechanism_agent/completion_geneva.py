"""Geometric synthesis of an external Geneva, including a relieved locking disk.

The swept-envelope calculation is a preflight, never a substitute for NX results.
"""
import math
import numpy as np
from shapely.geometry import Point,LineString
from shapely.affinity import rotate,translate
from shapely.ops import unary_union

def wheel_angle(theta,n):
    """Continuous output angle for input starting at -pi, before its first index."""
    t=(theta+math.pi)%(2*math.pi)-math.pi;cycle=math.floor((theta+math.pi)/(2*math.pi));alpha=math.pi/2-math.pi/n
    if t<=-alpha:value=math.pi/n
    elif t>=alpha:value=-math.pi/n
    else:value=math.atan2(-math.sin(t)*math.sin(math.pi/n),1-math.cos(t)*math.sin(math.pi/n))
    return value-cycle*2*math.pi/n

def design(n=6,center=60.,pin=3.,clearance=.2):
    if n<4 or not isinstance(n,int) or center<=30 or pin<=0 or clearance<=0:raise ValueError('Bounded external Geneva geometry required')
    r=center*math.sin(math.pi/n);alpha=math.pi/2-math.pi/n;outer=math.sqrt(center*center-r*r)+2;lock=center-r-4
    if lock<=8 or center-r-pin-clearance<=8:raise ValueError('Insufficient hub material')
    wheel=Point(0,0).buffer(outer,quad_segs=512)
    for k in range(n):
        a=2*math.pi*k/n;slot=LineString([(v*math.cos(a),v*math.sin(a)) for v in [center-r,outer+5]]).buffer(pin+clearance,quad_segs=64);wheel=wheel.difference(slot)
        b=a+math.pi/n;wheel=wheel.difference(Point(center*math.cos(b),center*math.sin(b)).buffer(lock+clearance,quad_segs=512))
    assert wheel.geom_type=='Polygon' and wheel.is_valid and not wheel.interiors
    # Remove only the region actually occupied by the indexing wheel in the input frame.
    # The lower crank plate provides a continuous structural connection to the pin.
    disk=Point(0,0).buffer(lock,quad_segs=512);sweeps=[]
    for t in np.linspace(-alpha,alpha,1201):
        moving=translate(rotate(wheel,math.degrees(wheel_angle(t,n)),origin=(0,0)),xoff=center)
        relative=rotate(moving,-math.degrees(t),origin=(0,0));overlap=relative.intersection(disk.buffer(clearance))
        if not overlap.is_empty:sweeps.append(overlap.buffer(clearance))
    relieved=disk.difference(unary_union(sweeps)).simplify(.0005,preserve_topology=True)
    assert relieved.geom_type=='Polygon' and relieved.is_valid and relieved.contains(Point(0,0)) and not relieved.interiors
    # Replace the many-edge swept relief with a machinable circular relief.
    # Size it from the internal swept boundary, then independently check the
    # entire cycle again below. The physical geometry changes; solver accuracy,
    # fit clearance and acceptance thresholds do not.
    boundary=np.asarray(relieved.exterior.coords)
    interior=boundary[np.linalg.norm(boundary,axis=1)<lock-.01]
    if not len(interior):raise ValueError('Swept relief boundary not identified')
    relief_radius=math.ceil(float(np.linalg.norm(interior-[r,0],axis=1).max())*1000)/1000+.02
    if r-relief_radius<=3.2:raise ValueError('Circular relief removes shaft support core')
    relieved=disk.difference(Point(r,0).buffer(relief_radius,quad_segs=512))
    assert relieved.geom_type=='Polygon' and relieved.is_valid and relieved.contains(Point(0,0))
    wheel=wheel.simplify(.0005,preserve_topology=True)
    checks=[]
    for t in np.linspace(-math.pi,math.pi,1441):
        w=translate(rotate(wheel,math.degrees(wheel_angle(t,n)),origin=(0,0)),xoff=center);d=rotate(relieved,math.degrees(t),origin=(0,0));p=Point(r*math.cos(t),r*math.sin(t)).buffer(pin,quad_segs=64)
        checks.append(max(w.intersection(d).area,w.intersection(p).area))
    if max(checks)>1e-9:raise ValueError('Independent geometric penetration before NX: '+str(max(checks)))
    profile_w=translate(rotate(wheel,180/n,origin=(0,0)),xoff=center);profile_d=rotate(relieved,180,origin=(0,0));profiles={'wheel':list(profile_w.exterior.coords)[:-1],'locking_disk':list(profile_d.exterior.coords)[:-1]}
    assert all(3<=len(v)<=4096 for v in profiles.values())
    p={'slots':n,'center_distance_mm':center,'crank_radius_mm':r,'pin_radius_mm':pin,'radial_clearance_mm':clearance,'wheel_outer_radius_mm':outer,'locking_disk_radius_mm':lock,'circular_relief_radius_mm':relief_radius,'native_profile_construction':'exact circular and rectangular subtractive features','density_kg_m3':7800,'contact_stiffness_native':1000,'contact_damping_native':.1,'contact_depth_mm':.001,'initial_input_deg':-180,'initial_output_deg':180/n}
    plan={'schema_version':1,'family':'completion_geneva','units':{'length':'mm','angle':'degree','time':'s'},'parameters':p,'joints':[{'name':'Input','type':'Revolute','first':'InputLink','second':'Link0','origin':[0,0,14],'axis':[0,0,1]},{'name':'Output','type':'Revolute','first':'OutputLink','second':'Link0','origin':[center,0,20.4],'axis':[0,0,1]}],'drive':{'duration_s':4,'steps':240,'velocity_deg_s':90},'independent_calculation':{'index_angle_deg':360/n,'engagement_input_deg':math.degrees(2*alpha),'dwell_input_fraction':.5+1/n,'functional_geometric_dof':1,'unconstrained_contact_dynamics_dof':2,'offline_max_overlap_area_mm2':max(checks),'profile_simplification_bound_mm':.0005,'profile_point_counts':{k:len(v) for k,v in profiles.items()}},'tolerances':{'output_angle_deg':1.,'input_angle_deg':.02,'forbidden_intersection_volume_mm3':0.},'repair_limit':3,'construction':'External Geneva with real radial slots, concave locking arcs and swept-envelope-relieved input locking disk. One input driver; output must be moved only by native unilateral body contact. Kinematic formula is used only for independent comparison. Steel-density mass/inertia, zero gravity/external load, numerical penalty parameters are experimental and not calibrated material data. Positive body penetration is a failed geometry acceptance, never rounded away.'}
    plan['parameters']['contact_force_model']='midplane'
    plan['parameters']['contact_geometry']='actual_planar_working_faces'
    plan['construction']+=' Revised native construction uses exact circular cuts and straight slot walls; CSV outlines are independent reference geometry, not the NX modeling input. Circular relief radius is synthesized from the swept boundary and rechecked over the full cycle.'
    return plan,profiles

if __name__=='__main__':
    import json
    p,_=design();print(json.dumps(p,indent=2))
