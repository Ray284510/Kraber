"""Quintic rise/dwell/return law; independent analytic offset-curve construction."""
import math
import numpy as np
from .cam_plan import design as original_design

def law(theta,lift=12):
    t=theta%(2*math.pi);beta=2*math.pi/3
    if t<beta:u=t/beta;sign=1;base=0
    elif t<math.pi:return lift,0.,0.
    elif t<math.pi+beta:u=(t-math.pi)/beta;sign=-1;base=lift
    else:return 0.,0.,0.
    return base+sign*lift*(10*u**3-15*u**4+6*u**5),sign*lift*(30*u**2-60*u**3+30*u**4)/beta,sign*lift*(60*u-180*u**2+120*u**3)/beta**2

def profile(theta,base=35,roller=5,lift=12):
    s,v,a=law(theta,lift);radius=base+roller+s;sn,cs=math.sin(theta),math.cos(theta);dx=v*sn+radius*cs;dy=v*cs-radius*sn;length=math.hypot(dx,dy)
    return [radius*sn+roller*dy/length,radius*cs-roller*dx/length]

def design():
    p=original_design();p['family']='completion_cam_groove_quintic';p['parameters']['law']='quintic_3_4_5';pressure=[];curvature=[]
    for t in np.linspace(0,2*math.pi,7200,endpoint=False):
        s,v,a=law(t);r=40+s;pressure.append(math.degrees(math.atan(abs(v)/r)));curvature.append((r*r+v*v)**1.5/(r*r+2*v*v-r*a))
    assert max(pressure)<30 and min(curvature)>5.2
    p['independent_calculation'].update(max_pressure_angle_deg=max(pressure),min_pitch_curvature_radius_mm=min(curvature),min_convex_working_curvature_mm=min(curvature)-5.2)
    p['law_selection_reason']='Choose quintic rise/return for zero endpoint velocity and acceleration and explicit two-end dwells; this differs from the previously accepted cycloidal law. Jerk and force response are not dynamically validated.'
    omega=math.pi/2
    p['independent_motion_law_samples']=[{'time_s':float(t/omega),'displacement_mm':float(law(t)[0]),'velocity_mm_s':float(law(t)[1]*omega),'acceleration_mm_s2':float(law(t)[2]*omega**2)} for t in np.linspace(0,2*math.pi,721)]
    return p
