"""Four-bar driven pawl and separate holding pawl; no prescribed wheel motion."""
import math
import numpy as np
from shapely.geometry import Point,Polygon
from .calculations import fourbar_pose,fourbar_metrics,constraint_rank

def design():
    d,r,l,s=40.,7.,40.,35.;n=18;outer=25.;root=22.;metrics=fourbar_metrics(d,r,l,s);initial=metrics['dead_center_input_deg'][0];pose=fourbar_pose(d,r,l,s,math.radians(initial));A=pose['A'];C=pose['C'];phi=pose['rocker_angle'];centre=np.array([d,0.]);drive_tip=centre+27*np.array([math.cos(phi-math.radians(20)),math.sin(phi-math.radians(20))]);hold_pivot=centre+np.array([30.,20.]);hold_tip=centre+27*np.array([math.cos(math.radians(55)),math.sin(math.radians(55))]);phase=math.radians(6)
    points=[]
    for k in range(n):
        a=phase+2*math.pi*k/n;b=a+math.radians(12);z=a+2*math.pi/n
        points.extend([[d+outer*math.cos(a),outer*math.sin(a)],[d+root*math.cos(b),root*math.sin(b)],[d+root*math.cos(z),root*math.sin(z)]])
    wheel=Polygon(points);assert wheel.is_valid
    def pawl(pivot,tip):
        a=np.array(pivot);b=np.array(tip);u=(b-a)/np.linalg.norm(b-a);v=np.array([-u[1],u[0]]);poly=Polygon([a+5*v,b+1.5*v,b-1.5*v,a-5*v]).union(Point(*a).buffer(5,quad_segs=96)).union(Point(*b).buffer(1.5,quad_segs=96)).simplify(.0005)
        assert poly.geom_type=='Polygon' and poly.is_valid and not poly.interiors
        if poly.intersection(wheel).area>1e-10:raise ValueError('Initial pawl penetration before NX')
        return list(poly.exterior.coords)[:-1]
    profiles={'wheel':points,'drive_pawl':pawl(C,drive_tip),'hold_pawl':pawl(hold_pivot,hold_tip)}
    origins={'O':[0,0,10],'A':A+[28.2],'B':C+[28.2],'D':[d,0,10],'DrivePawl':C+[24],'HoldPawl':hold_pivot.tolist()+[24],'Wheel':[d,0,20.4]};joints=[]
    for name,first,second,key in [('Input','Crank','Link0','O'),('CrankPin','Coupler','Crank','A'),('RockerPin','Coupler','Rocker','B'),('RockerAxis','Rocker','Link0','D'),('WheelAxis','Wheel','Link0','Wheel'),('DrivePawlAxis','DrivePawl','Rocker','DrivePawl'),('HoldPawlAxis','HoldPawl','Link0','HoldPawl')]:joints.append({'name':name,'type':'Revolute','first':first,'second':second,'origin':origins[key],'axis':[0,0,1]})
    rank=constraint_rank(['Link0','Crank','Coupler','Rocker','Wheel','DrivePawl','HoldPawl'],joints,'Link0');assert rank['functional_dof']==4
    output=[fourbar_pose(d,r,l,s,t)['rocker_angle'] for t in np.linspace(0,2*math.pi,1441)]
    plan={'schema_version':1,'family':'completion_ratchet','units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'ground_length_mm':d,'crank_mm':r,'coupler_mm':l,'rocker_mm':s,'teeth':n,'wheel_outer_radius_mm':outer,'wheel_root_radius_mm':root,'initial_input_deg':initial,'initial_wheel_phase_deg':6,'density_kg_m3':7800,'drive_pawl_preload_N_mm':-.04,'hold_pawl_preload_N_mm':.04,'input_speed_deg_s':90},'initial_points':{'O':[0,0],'A':A,'C':C,'D':[d,0],'drive_tip':drive_tip.tolist(),'hold_pivot':hold_pivot.tolist(),'hold_tip':hold_tip.tolist()},'joints':joints,'drive':{'duration_s':8,'steps':480,'velocity_deg_s':90},'independent_calculation':{'fourbar':metrics,'rocker_swing_deg':math.degrees(max(output)-min(output)),'expected_steady_index_deg':360/n,'lower_pair_spatial_rank':rank,'mobility_scope':'Four lower-pair freedoms before unilateral contacts: input loop, wheel and two pawls. Contact active sets and preload determine dynamics; do not report a fabricated global 1 DOF by subtracting the pawls.'},'tolerances':{'steady_index_error_deg':2.,'input_angle_deg':.02,'forbidden_intersection_volume_mm3':0.},'repair_limit':3,'construction':'Continuous input drives a real four-bar rocker carrying a separate pawl; a grounded holding pawl prevents reverse rotation. Real sawtooth wheel, two native unilateral body contacts and equal opposite 0.04 N mm experimental joint preload moments. No wheel/pawl motion driver. No external output load or gravity; real density/mass/inertia required. These numerical preloads are not a designed spring or a rated load/stress claim.'}
    plan['parameters'].update(pawl_bottom_mm=20.8,pawl_thickness_mm=6.4,contact_force_model='midplane')
    plan['construction']+=' Contact-polygon repair uses physically thinner pawls centered at unchanged Z24, avoiding coincident tooth/pawl cap planes; all original contact and interference tolerances remain unchanged.'
    plan['construction']+=' Final contact repair constructs each pawl from an exact circular 5mm pivot end, exact circular 1.5mm tip end and a four-edge connecting web. The sampled profiles are retained for independent initial-envelope checks only, not used for the native pawl outer surfaces. Whole-body contacts remain enabled.'
    plan['independent_calculation']['preload_basis']={'wheel_inertia_kg_mm2':24.098040205759556,'peak_smooth_rocker_accel_rad_s2':0.7231707740575075,'nominal_wheel_torque_N_mm':0.017426998388868076,'factor':4,'estimated_joint_preload_N_mm':0.037198834245322616,'chosen_magnitude_N_mm':0.04,'scope':'Independent polygon inertia and 4096/8192-point acceleration convergence; smooth nominal sizing only, not an impact bound or load rating. Evidence logs/phase4_completion/ratchet-preload-independent-sizing.json.'}
    return plan,profiles

if __name__=='__main__':
    import json
    p,_=design();print(json.dumps(p,indent=2))
