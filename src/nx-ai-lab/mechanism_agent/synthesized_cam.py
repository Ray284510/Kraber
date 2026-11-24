"""Realize a selected radial cam law with explicit shaft/roller packaging checks."""
import math
import numpy as np
from .synthesis import normalized_cam_law

def law_from_parameters(q):
    beta=math.radians(q['rise_angle_deg']);lift=q['lift_mm']
    def law(theta):
        t=theta%(2*math.pi)
        if t<beta:u=t/beta;sign=1;base=0
        elif t<math.pi:return lift,0.,0.
        elif t<math.pi+beta:u=(t-math.pi)/beta;sign=-1;base=lift
        else:return 0.,0.,0.
        s,v,a,_=normalized_cam_law(np.array(u),q['law']);return float(base+sign*lift*s),float(sign*lift*v/beta),float(sign*lift*a/beta**2)
    return law

def plan_from_selection(selection):
    if selection['selected']!='disk_cam' or selection.get('nx_acceptance'):raise ValueError('An unverified synthesized disk-cam proposal is required')
    q=dict(selection['selected_parameters']);law=law_from_parameters(q);lift=q['lift_mm'];roller=max(3.5,q['roller_radius_mm']);base=math.ceil(max(q['pitch_base_radius_mm'],roller+10))
    if not 1<=lift<=25:raise ValueError('Current guide realization supports lift 1..25 mm; redesign hardware beyond this range')
    samples=[law(t) for t in np.linspace(0,2*math.pi,7201)];pressure=[];curvature=[]
    for _ in range(100):
        pressure=[];curvature=[]
        for s,v,a in samples:
            r=base+s;den=r*r+2*v*v-r*a
            if den<=0:break
            pressure.append(math.degrees(math.atan(abs(v)/r)));curvature.append((r*r+v*v)**1.5/den)
        else:
            if min(curvature)>roller+.2 and max(pressure)<=selection['requirements']['max_pressure_angle_deg']:break
        base+=1
    else:raise ValueError('No realizable convex groove within bounded radius search')
    radius=math.ceil(base+lift+roller+.2+4);guide=radius+25;slider=guide-15;duration=360/q['input_speed_deg_s'];ymin=-radius-5;ymax=guide+20
    parameters=dict(q,pitch_base_radius=base,roller_radius=roller,lift=lift,disk_radius=radius,guide_center_y=guide,slider_center_y=slider,law=q['law'],groove_radial_clearance=.2)
    plan={'schema_version':1,'family':'synthesized_cam','selection':selection,'units':{'length':'mm','angle':'degree','time':'s'},'parameters':parameters,'realization_adjustments':{'original_pitch_base_radius_mm':q['pitch_base_radius_mm'],'final_pitch_base_radius_mm':base,'original_roller_radius_mm':q['roller_radius_mm'],'final_roller_radius_mm':roller,'reason':'Maintain material around 8.4 mm shaft bore and 4.2 mm roller bore; recompute pressure/curvature after packaging adjustment. These are new design dimensions, not altered NX results.'},'layout':{'base_width':2*radius+10,'base_y':(ymin+ymax)/2,'base_length':ymax-ymin},'joints':[{'name':'CamAxis','type':'Revolute','first':'Cam','second':'Link0','origin':[0,0,19.8],'axis':[0,0,1]},{'name':'Slide','type':'Slider','first':'Carriage','second':'Link0','origin':[0,slider,33],'axis':[0,1,0]},{'name':'RollerAxis','type':'Revolute','first':'Roller','second':'Carriage','origin':[0,base,28.2],'axis':[0,0,1]}],'drive':{'duration_s':duration,'steps':240,'velocity_deg_s':q['input_speed_deg_s']},'independent_calculation':{'functional_dof':1,'total_mobility':2,'local_roller_spin_dof':1,'maximum_pressure_angle_deg':max(pressure),'minimum_pitch_curvature_mm':min(curvature)},'tolerances':{'follower_position_mm':.02,'cross_axis_position_mm':.01},'repair_limit':3,'construction':'Dimensioned grooved cam with real 0.2 mm radial clearance, native nominal PointOnCurve relation and independent local roller spin test. No preload, force contact or backlash dynamics acceptance.'}
    for key,limit in selection['requirements'].get('maxima',{}).items():
        actual={'footprint_area_mm2':plan['layout']['base_width']*plan['layout']['base_length'],'footprint_length_mm':plan['layout']['base_length'],'footprint_width_mm':plan['layout']['base_width']}.get(key)
        if actual is not None and actual>limit:raise ValueError('Realized cam packaging exceeds requirement '+key+'; recompare alternatives')
    plan['independent_motion_law_samples']=[{'time_s':i*duration/720,'displacement_mm':law(i*math.pi/360)[0],'velocity_mm_s':law(i*math.pi/360)[1]*2*math.pi/duration,'acceleration_mm_s2':law(i*math.pi/360)[2]*(2*math.pi/duration)**2} for i in range(721)]
    def profile(theta,offset=0):
        s,v,a=law(theta);r=base+s;sn,cs=math.sin(theta),math.cos(theta);dx=v*sn+r*cs;dy=v*cs-r*sn;length=math.hypot(dx,dy);return [r*sn+offset*dy/length,r*cs-offset*dx/length]
    return plan,law,profile
