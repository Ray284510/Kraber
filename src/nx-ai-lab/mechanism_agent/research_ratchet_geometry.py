"""Independent preflight for an explicitly ideal indexed ratchet, no NX claims."""
import math,json
import numpy as np
from shapely.geometry import Polygon,Point
from shapely.affinity import rotate
from .calculations import fourbar_pose,fourbar_metrics

R=5.477310858917672
D=np.array([40.,0.]); GAP=.05
METRICS=fourbar_metrics(40,R,40,35)
INITIAL=METRICS['dead_center_input_deg'][0]
DRIVE_ARC=METRICS['stroke_input_arcs_deg'][0]
POSE0=fourbar_pose(40,R,40,35,math.radians(INITIAL));C0=np.array(POSE0['C']);PHI0=POSE0['rocker_angle']
def tip_at_face(deg):
    a=math.radians(deg);return D+24*np.array([math.cos(a),math.sin(a)])+(1.5+GAP)*np.array([math.sin(a),-math.cos(a)])
DRIVE_FACE=58;HOLD_FACE=298;ELBOW_OFFSET=20;TOOTH_PHASE=18
TIP0=tip_at_face(DRIVE_FACE);HTIP0=tip_at_face(HOLD_FACE)
Q=np.array([[math.cos(math.radians(-120)),-math.sin(math.radians(-120))],[math.sin(math.radians(-120)),math.cos(math.radians(-120))]])
H=D+Q@(C0-D)
L=float(np.linalg.norm(TIP0-C0));HL=float(np.linalg.norm(HTIP0-H))
PSI0=math.atan2(*(TIP0-C0)[::-1]);HPSI0=math.atan2(*(HTIP0-H)[::-1])
WPOINTS=[]
for k in range(18):
    for deg,radius in [(TOOTH_PHASE+20*k,25),(TOOTH_PHASE+12+20*k,22),(TOOTH_PHASE+20+20*k,22)]:
        a=math.radians(deg);WPOINTS.append((D+radius*np.array([math.cos(a),math.sin(a)])).tolist())
WHEEL=Polygon(WPOINTS)
def pawl(pivot,length,angle):
    pivot=np.array(pivot);tip=pivot+length*np.array([math.cos(angle),math.sin(angle)])
    # Hook nose first exits radially, then returns to the pivot outside tooth tips.
    radial86=np.array([math.cos(math.radians(DRIVE_FACE)),math.sin(math.radians(DRIVE_FACE))])
    da=angle-PSI0;rot=np.array([[math.cos(da),-math.sin(da)],[math.sin(da),math.cos(da)]])
    elbow=tip+ELBOW_OFFSET*rot@radial86
    poly=Point(*pivot).buffer(5,quad_segs=128)
    for a,b in [(pivot,elbow),(elbow,tip)]:
        u=(b-a)/np.linalg.norm(b-a);v=np.array([-u[1],u[0]])
        poly=poly.union(Polygon([a+1.5*v,b+1.5*v,b-1.5*v,a-1.5*v])).union(Point(*b).buffer(1.5,quad_segs=128))
    return poly
def smooth(x):
    x=max(0,min(1,x));return 10*x**3-15*x**4+6*x**5
def state(theta,drive_lift=35,hold_lift=35):
    cycle=math.floor((theta-INITIAL+1e-9)/360);phase=(theta-INITIAL)%360
    # Avoid final floating-point wrap into another phase.
    if phase>360-1e-8:phase=0
    pose=fourbar_pose(40,R,40,35,math.radians(theta));phi=pose['rocker_angle'];C=np.array(pose['C'])
    forward=phase<=DRIVE_ARC
    wd=cycle*20+(math.degrees(phi-PHI0) if forward else 20)
    # Ideal mechanical pawl lift schedules. They are not unilateral force solutions.
    if forward:dlift=0
    else:
        u=(phase-DRIVE_ARC)/(360-DRIVE_ARC)
        dlift=drive_lift*smooth(u/.12)*(1-smooth((u-.90)/.10))
    # Hold pawl is lifted in advance, then returns after wheel stops.
    # At t=0 the holding pawl starts seated; lift during the slow initial rocker rise.
    hlift=hold_lift*smooth(phase/(DRIVE_ARC*.18))*(1-smooth((phase-DRIVE_ARC)/(360-DRIVE_ARC)/.25))
    psi=PSI0+(phi-PHI0)+math.radians(dlift);hpsi=HPSI0+math.radians(hlift)
    return dict(theta=theta,phase=phase,forward=forward,wheel_deg=wd,phi=phi,C=C,psi=psi,hpsi=hpsi,drive_lift=dlift,hold_lift=hlift,pose=pose)
def check(n=721):
    errors=[];small=[]
    for i,theta in enumerate(np.linspace(INITIAL,INITIAL+1080,n)):
        s=state(theta);w=rotate(WHEEL,s['wheel_deg'],origin=D.tolist());a=pawl(s['C'],L,s['psi']);b=pawl(H,HL,s['hpsi'])
        areas=[w.intersection(a).area,w.intersection(b).area,a.intersection(b).area]
        if max(areas)>1e-10:errors.append(dict(frame=i,phase=s['phase'],areas=areas))
        small.append([w.distance(a),w.distance(b)])
    hold=pawl(H,HL,HPSI0)
    reverse={str(d):rotate(WHEEL,-d,origin=D.tolist()).intersection(hold).area for d in [0,.05,.1,.2,.5,1]}
    a=math.radians(HOLD_FACE);normal=np.array([math.sin(a),-math.cos(a)]);lever=HTIP0-H;reverse_torque=float(lever[0]*normal[1]-lever[1]*normal[0])
    # Adjacent outward-facing tooth ramp (outer at face, root at face+12deg).
    edge=22*np.array([math.cos(a+math.radians(12)),math.sin(a+math.radians(12))])-25*np.array([math.cos(a),math.sin(a)])
    ramp_normal=np.array([edge[1],-edge[0]])/np.linalg.norm(edge);advance_torque=float(lever[0]*ramp_normal[1]-lever[1]*ramp_normal[0])
    return dict(scope='OFFLINE PRECHECK ONLY, not NX acceptance',samples=n,failures=errors,minimum_gaps=np.min(small,axis=0).tolist(),reverse_block_area_mm2=reverse,reverse_torque_per_normal_force_mm=reverse_torque,advance_ramp_torque_per_normal_force_mm=advance_torque,parameters=dict(crank=R,initial=INITIAL,drive_arc=DRIVE_ARC,drive_tip=TIP0.tolist(),hold_tip=HTIP0.tolist(),drive_length=L,hold_length=HL,running_gap=GAP))
def design():
    from .completion_ratchet import design as historical
    from .calculations import constraint_rank
    plan,_=historical();p=plan['parameters'];p.update(crank_mm=R,initial_input_deg=INITIAL,initial_wheel_phase_deg=TOOTH_PHASE,hold_pawl_preload_N_mm=None,drive_pawl_preload_N_mm=None,running_gap_mm=GAP,pawl_geometry='hook with two 3mm webs and exact rounded ends',pawl_lift_deg=35,drive_face_deg=DRIVE_FACE,hold_face_deg=HOLD_FACE,hook_radial_extension_mm=ELBOW_OFFSET)
    pts=dict(O=[0,0],A=POSE0['A'],C=C0.tolist(),D=D.tolist(),drive_tip=TIP0.tolist(),hold_pivot=H.tolist(),hold_tip=HTIP0.tolist())
    pts['drive_elbow']=(TIP0+ELBOW_OFFSET*np.array([math.cos(math.radians(DRIVE_FACE)),math.sin(math.radians(DRIVE_FACE))])).tolist()
    pts['hold_elbow']=(HTIP0+ELBOW_OFFSET*np.array([math.cos(math.radians(HOLD_FACE)),math.sin(math.radians(HOLD_FACE))])).tolist()
    plan['initial_points']=pts
    origins=dict(Input=pts['O']+[10],CrankPin=pts['A']+[28.2],RockerPin=pts['C']+[28.2],RockerAxis=pts['D']+[10],WheelAxis=pts['D']+[20.4],DrivePawlAxis=pts['C']+[24],HoldPawlAxis=pts['hold_pivot']+[24])
    for j in plan['joints']:j['origin']=origins[j['name']]
    rank=constraint_rank(['Link0','Crank','Coupler','Rocker','Wheel','DrivePawl','HoldPawl'],plan['joints'],'Link0')
    plan['drive'].update(duration_s=12,steps=720)
    plan['independent_calculation']=dict(fourbar=METRICS,rocker_swing_deg=20,expected_steady_index_deg=20,lower_pair_spatial_rank=rank,mobility_scope='Four physical lower-pair freedoms before contacts; four independent prescribed ideal drivers remove them. Not a global physical one-DOF unilateral mechanism.',preflight_scope='Separate offline geometry screening, not NX verification')
    plan['tolerances'].update(steady_index_error_deg=.01,nominal_pawl_gap_mm=GAP,backstop_geometric_engagement_deg=.2)
    plan['repair_limit']=2
    plan['construction']='Research D+E ideal kinematics only: redesigned crank gives exactly one 20degree tooth pitch; hook-shaped pawls clear the tooth tips, with separate prescribed lift-return paths. 0.05mm declared running clearance and up to0.2degree geometric backlash. Native contact forces, spring behavior, loaded backstop and dynamic impacts are NOT validated. Native full-solid zero-positive-volume checks remain mandatory.'
    profiles=dict(wheel=WPOINTS,drive_pawl=list(pawl(C0,L,PSI0).exterior.coords)[:-1],hold_pawl=list(pawl(H,HL,HPSI0).exterior.coords)[:-1])
    return plan,profiles
if __name__=='__main__':
    import sys
    result=check(int(sys.argv[2]) if len(sys.argv)>2 else 721);open(sys.argv[1],'x').write(json.dumps(result,indent=2));print(json.dumps({k:(len(v) if k=='failures' else v) for k,v in result.items()},indent=2))
