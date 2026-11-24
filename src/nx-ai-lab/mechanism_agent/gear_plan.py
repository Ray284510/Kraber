"""Independent involute profiles and Willis checks, not NX simulation results."""
import math
import numpy as np
from .selection import Requirement,select
from .calculations import planetary_ratio,planetary_mesh_rank

def gear_profile(z,module=1.5,internal_void=False,phase=0,center=(0,0),thin=.06):
 rp=z*module/2;rb=rp*math.cos(math.radians(20));rmin=rp-module if internal_void else rp-1.25*module;rmax=rp+1.25*module if internal_void else rp+module
 inv=lambda a:math.tan(a)-a
 h=lambda r:math.pi/(2*z)+(thin if internal_void else -thin)/(2*rp)+inv(math.radians(20))-inv(math.acos(rb/max(r,rb)))
 pts=[];maxerr=0
 def polar(r,a):return np.array([r*math.cos(a),r*math.sin(a)])
 radii=np.linspace(max(rmin,rb),rmax,19).tolist()
 if rmin<rb:radii=[rmin]+radii
 for tooth in range(z):
  angle=phase+2*math.pi*tooth/z
  sequence=[(r,angle-h(r)) for r in radii]
  sequence += [(rmax,a) for a in np.linspace(angle-h(rmax),angle+h(rmax),5)[1:]]
  sequence += [(r,angle+h(r)) for r in radii[-2::-1]]
  sequence += [(rmin,a) for a in np.linspace(angle+h(rmin),angle+2*math.pi/z-h(rmin),7)[1:-1]]
  for r,a in sequence:pts.append((polar(r,a)+np.array(center)).tolist())
 # Bound the sampled involute chord approximation by dense interior samples.
 for x,y in zip(radii[:-1],radii[1:]):
  a=polar(x,h(x));b=polar(y,h(y));v=b-a
  for r in np.linspace(x,y,51):
   p=polar(r,h(r));q=a+np.clip((p-a)@v/(v@v),0,1)*v;maxerr=max(maxerr,float(np.linalg.norm(p-q)))
 rootarc=(2*math.pi/z-2*h(rmin))/6;tiparc=2*h(rmax)/4;maxerr=max(maxerr,rmin*(1-math.cos(rootarc/2)),rmax*(1-math.cos(tiparc/2)))
 return pts,{'teeth':z,'module_mm':module,'pressure_angle_deg':20,'pitch_radius_mm':rp,'base_radius_mm':rb,'root_radius_mm':rmax if internal_void else rmin,'tip_radius_mm':rmin if internal_void else rmax,'tooth_thinning_mm':thin,'profile_points':len(pts),'max_sampled_chord_error_mm':maxerr,'root_form':'Radial root relief with circular root segments; not a generated hob trochoid or stress-qualified tooth'}

def design():
 zs,zp,zr,n=40,20,80,3;module=1.5;center=(zs+zp)*module/2;assert zr==zs+2*zp and (zs+zr)%n==0 and zp+2<(zs+zp)*math.sin(math.pi/n)
 profiles={};metrics={}
 profiles['sun'],metrics['sun']=gear_profile(zs)
 profiles['ring_void'],metrics['ring']=gear_profile(zr,internal_void=True,phase=math.pi/zr)
 centers=[]
 for i in range(n):
  phi=i*2*math.pi/n;c=[center*math.cos(phi),center*math.sin(phi)];centers.append(c);profiles[f'planet{i+1}'],metrics[f'planet{i+1}']=gear_profile(zp,phase=-math.pi/zp,center=c)
 assert max(x['max_sampled_chord_error_mm'] for x in metrics.values())<.003
 from shapely.geometry import Polygon
 from shapely.affinity import rotate,translate
 sun=Polygon(profiles['sun']);void=Polygon(profiles['ring_void']);planet=Polygon(gear_profile(zp,phase=-math.pi/zp)[0]);assert sun.is_valid and void.is_valid and planet.is_valid
 maxarea=0;minclear=float('inf')
 for t in np.linspace(0,4,241):
  carrier=30*t;rsun=rotate(sun,90*t,origin=(0,0))
  for i in range(3):
   phi=math.radians(carrier+120*i);p=translate(rotate(planet,-90*t,origin=(0,0)),center*math.cos(phi),center*math.sin(phi));maxarea=max(maxarea,p.intersection(rsun).area,p.difference(void).area);minclear=min(minclear,p.distance(rsun),void.boundary.distance(p))
 assert maxarea==0,('Independent tooth penetration',maxarea)
 plan={'schema_version':1,'family':'planetary','selection':select(Requirement(output_motion='continuous_rotation',coaxial=True,source_text='设计紧凑同轴传动，输入每秒30度，输出每秒90度，比较定轴轮系与行星轮系')),'units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'sun_teeth':zs,'planet_teeth':zp,'ring_teeth':zr,'planets':n,'module':module,'pressure_angle':20,'center_distance':center,'gear_thickness':8,'carrier_radius':53,'backlash_each_tooth_thinning':.06,'initial_planet_phase_deg':-9,'initial_ring_void_phase_deg':2.25},'profile_metrics':metrics,'planet_centers':centers,'independent_calculation':{**planetary_ratio(zs,zp,zr),'input':'carrier 30 degrees/s','output':'sun 90 degrees/s','carrier_to_sun_speed_ratio':3,'input_output_ratio':1/3,'planet_absolute_speed_deg_s':-90,'planet_relative_carrier_speed_deg_s':-120,'functional_mobility':1,'rotational_coordinates_before_mesh':5,'mesh_equations':6,'mesh_equation_rank':planetary_mesh_rank(zs,zp,zr,n)['rank'],'redundant_mesh_equations':planetary_mesh_rank(zs,zp,zr,n)['redundant_equations'],'assembly_divisibility':(zs+zr)//n,'adjacent_planet_clearance_mm':2*center*math.sin(math.pi/n)-module*(zp+2),'sampled_2d_tooth_overlap_area_mm2':maxarea,'sampled_2d_min_clearance_mm':minclear},'joints':[{'name':'CarrierAxis','type':'Revolute','first':'Carrier','second':'Link0','origin':[0,0,5],'axis':[0,0,1]},{'name':'SunAxis','type':'Revolute','first':'Sun','second':'Carrier','origin':[0,0,18],'axis':[0,0,1]}]+[{'name':f'PlanetAxis{i+1}','type':'Revolute','first':f'Planet{i+1}','second':'Carrier','origin':[*c,18],'axis':[0,0,1]} for i,c in enumerate(centers)],'drive':{'joint':'CarrierAxis','velocity_deg_s':30,'duration_s':4,'steps':240},'tolerances':{'marker_mm':.02,'angle_deg':.02,'profile_chord_mm':.003},'repair_limit':3,'native_coupling_strategy':'Three external gear couplers plus one rotational two-joint Willis relation: planet_relative + 4*carrier_absolute = 0. Other internal mesh equations are redundant and checked from actual trajectories.','construction':'Fixed ring, rotating carrier input, sun output; 3 true sampled-involute planet gears. Nominal rigid gear coupling with specified tooth backlash; torque, tooth stress, lubrication and backlash impacts are outside this kinematic demonstration.'}
 return plan,profiles

