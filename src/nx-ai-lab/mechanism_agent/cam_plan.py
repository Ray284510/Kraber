"""Cycloidal radial disk cam: inverse-kinematic roller envelope in mm/radians."""
import math
import numpy as np
from .selection import Requirement,select

def law(theta,lift=12):
 t=theta%(2*math.pi);beta=2*math.pi/3
 if t<beta:u=t/beta;sign=1;base=0
 elif t<math.pi:return lift,0.,0.
 elif t<math.pi+beta:u=(t-math.pi)/beta;sign=-1;base=lift
 else:return 0.,0.,0.
 return base+sign*lift*(u-math.sin(2*math.pi*u)/(2*math.pi)),sign*lift*(1-math.cos(2*math.pi*u))/beta,sign*lift*2*math.pi*math.sin(2*math.pi*u)/(beta*beta)

def profile(theta,base=35,roller=5,lift=12):
 s,v,a=law(theta,lift);r=base+roller+s;sn,cs=math.sin(theta),math.cos(theta);dx=v*sn+r*cs;dy=v*cs-r*sn;length=math.hypot(dx,dy)
 return [r*sn+roller*dy/length,r*cs-roller*dx/length]

def design(follower="roller"):
 roller=5 if follower=="roller" else 0
 theta=np.linspace(0,2*math.pi,7200,endpoint=False);press=[];curv=[]
 for t in theta:
  s,v,a=law(t);r=35+roller+s;press.append(math.degrees(math.atan(abs(v)/r)));curv.append((r*r+v*v)**1.5/(r*r+2*v*v-r*a))
 assert min(curv)>roller and max(press)<30
 return {'schema_version':1,'family':'cam','selection':select(Requirement(input_motion='continuous_rotation',output_motion='reciprocating_translation',dwell=True,source_text='连续旋转输入，直线从动件往复运动，并在两端停歇')),'units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'base_radius':35,'roller_radius':roller,'follower':follower,'lift':12,'rise_deg':120,'high_dwell_deg':60,'return_deg':120,'low_dwell_deg':60,'law':'cycloidal','profile_points':720,'thickness':8},'independent_calculation':{'max_pressure_angle_deg':max(press),'min_pitch_curvature_radius_mm':min(curv),'min_convex_working_curvature_mm':min(curv)-roller,'functional_mobility':1,'roller_spin_local_dof_if_sliding':1,'closure':'Ideal bilateral curve pair with no-slip; no spring/force/dynamic validation claimed'},'joints':[{'name':'CamAxis','type':'Revolute','first':'Cam','second':'Link0','origin':[0,0,19.8],'axis':[0,0,1]},{'name':'Slide','type':'Slider','first':'Carriage','second':'Link0','origin':[0,60,33],'axis':[0,1,0]},{'name':'RollerAxis','type':'Revolute','first':'Roller','second':'Carriage','origin':[0,40,28.2],'axis':[0,0,1]}],'higher_pair':{'type':'CurveOnCurve','first':'CAM','second':'ROLLER','lock_slip':True},'drive':{'joint':'CamAxis','velocity_deg_s':90,'duration_s':4,'steps':240},'tolerances':{'follower_position_mm':.02,'cross_axis_position_mm':.01,'maximum_pressure_angle_deg':30},'repair_limit':3,'construction':'Raised cam bearing, physically separated carriage guide rails and integral follower pin. Roller follows actual cam edge, not a prescribed displacement driver.'}
