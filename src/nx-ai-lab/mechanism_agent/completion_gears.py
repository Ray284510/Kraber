"""Independent geometry/kinematics for new gear demonstrations, never NX evidence."""
import math
import numpy as np
from shapely.geometry import Polygon
from shapely.affinity import rotate,translate
from .gear_plan import gear_profile

def rack_profile(count=43,module=1.5,thin=.06):
    pitch=math.pi*module;half=math.pi*module/4-thin/2;t=math.tan(math.radians(20));root=-1.25*module;tip=module
    x0=-(count//2)*pitch-pitch/2;pts=[[x0,-8],[x0,root]]
    for i in range(-(count//2),count//2+1):
        x=i*pitch
        pts += [[x-half+root*t,root],[x-half+tip*t,tip],[x+half-tip*t,tip],[x+half-root*t,root]]
    pts += [[-x0,root],[-x0,-8]]
    return pts

def design(kind):
    profiles={};metrics={};centers={};z={};speeds={};joints=[];couplers=[];groups={};motion={}
    def gear(name,teeth,center,level,phase=0):
        profiles[name],metrics[name]=gear_profile(teeth,center=center,phase=phase);centers[name]=center;z[name]=level
    if kind=='multistage_gears':
        gear('input',20,[0,0],20);gear('compound_low',40,[45,0],20,math.pi/40);gear('compound_high',20,[45,0],40);gear('output',60,[105,0],40,math.pi/60)
        centers={'input':[0,0],'compound':[45,0],'output':[105,0]};speeds={'input':90,'compound':-45,'output':15};groups={'Input':['INPUT'],'Compound':['COMPOUND'],'Output':['OUTPUT']}
        couplers=[['Mesh1','InputAxis','CompoundAxis',15,30,15,0,24],['Mesh2','CompoundAxis','OutputAxis',15,45,60,0,44]]
        ratios={'compound_to_input':-.5,'output_to_input':1/6};description='Two external involute meshes on separated axial planes; one rigid compound shaft. Three revolute links and two native Gear relations; one functional DOF.'
    elif kind=='internal_gears_direct':
        profiles['ring_void'],metrics['ring']=gear_profile(80,internal_void=True,phase=math.pi/80)
        gear('pinion',20,[45,0],20,math.pi/20);centers={'ring':[0,0],'pinion':[45,0]};speeds={'ring':22.5,'pinion':90};groups={'Ring':['RING'],'Pinion':['PINION']}
        couplers=[['InternalMesh','RingAxis','PinionAxis',60,-15,60,0,24]]
        ratios={'pinion_to_ring':4};description='Fixed-centre internal ring/pinion. Signed native Gear radii are an UNVERIFIED API hypothesis until actual creation and trajectory tests; General coupler is not a fallback acceptance.'
    elif kind=='rack_pinion':
        # A rack tooth at x=0 meets a pinion space at the bottom.
        gear('pinion',20,[0,15],24,math.pi/20);profiles['rack']=rack_profile();centers={'pinion':[0,15],'rack':[0,-18]};speeds={'pinion':90};groups={'Pinion':['PINION'],'Rack':['RACK']}
        ratios={'rack_mm_per_pinion_rad':15};description='True 20 degree straight-flank rack and sampled-involute pinion, native rack/pinion coupler, captured T-slide; one continuous rotation produces finite rack travel, not a reciprocating motion.'
    else:raise ValueError(kind)
    for name,center in centers.items():
        joints.append({'name':name.title()+'Axis','type':'Slider' if name=='rack' else 'Revolute','first':name.title(),'second':'Link0','origin':center+[24],'axis':[1,0,0] if name=='rack' else [0,0,1]})
    def poly(name):return Polygon(profiles[name])
    maxarea=0.;minclear=math.inf
    for t in np.linspace(0,4,241):
        if kind=='multistage_gears':
            pairs=[(rotate(poly('input'),90*t,origin=(0,0)),rotate(poly('compound_low'),-45*t,origin=(45,0))),(rotate(poly('compound_high'),-45*t,origin=(45,0)),rotate(poly('output'),15*t,origin=(105,0)))]
            areas=[a.intersection(b).area for a,b in pairs];clear=[a.distance(b) for a,b in pairs]
        elif kind=='internal_gears_direct':
            void=rotate(poly('ring_void'),22.5*t,origin=(0,0));pinion=rotate(poly('pinion'),90*t,origin=(45,0));areas=[pinion.difference(void).area];clear=[pinion.distance(void.boundary)]
        else:
            pinion=rotate(poly('pinion'),90*t,origin=(0,15));rack=translate(poly('rack'),15*math.pi/2*t,0);areas=[pinion.intersection(rack).area];clear=[pinion.distance(rack)]
        maxarea=max(maxarea,*areas);minclear=min(minclear,*clear)
    assert maxarea==0,('Independent positive tooth penetration',kind,maxarea)
    plan={'schema_version':1,'family':'completion_'+kind,'units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'module_mm':1.5,'pressure_angle_deg':20,'backlash_each_tooth_thinning_mm':.06,'gear_thickness_mm':8},'joints':joints,'drive':{'duration_s':4.,'steps':240},'tolerances':{'marker_mm':.01,'angle_deg':.02,'intersection_volume_mm3':0},'repair_limit':3,'centers':centers,'gear_levels':z,'groups':groups,'couplers':couplers,'expected_speed_deg_s':speeds,'profile_metrics':metrics,'independent_calculation':{'functional_dof':1,'ratios':ratios,'sampled_2d_overlap_area_mm2':maxarea,'sampled_2d_min_clearance_mm':minclear},'construction':description+' Ideal rigid transmission only; no torque, contact dynamics, tooth stress or manufacturing qualification.'}
    return plan,profiles
