"""Other grounded planetary members; independent Willis and profile sweep."""
import math
import numpy as np
from shapely.geometry import Polygon
from shapely.affinity import rotate,translate
from .gear_plan import design as original_design,gear_profile

def design(kind):
    p,profiles=original_design()
    if kind=='planetary_fixed_carrier':ws,wc,wr,wp=90.,0.,-45.,-180.;fixed='CARRIER';drive='SunAxis';velocity=ws
    elif kind=='planetary_fixed_sun':ws,wc,wr,wp=0.,30.,45.,90.;fixed='SUN';drive='CarrierAxis';velocity=-wc
    else:raise ValueError(kind)
    joints=[]
    def joint(name,a,b,xyz):joints.append({'name':name,'type':'Revolute','first':a,'second':b,'origin':xyz,'axis':[0,0,1]})
    if fixed=='CARRIER':
        joint('SunAxis','Sun','Link0',[0,0,24]);joint('RingAxis','Ring','Link0',[0,0,24]);parent='Link0'
    else:
        # The single grounded-sun / carrier hinge directly measures sun relative to carrier.
        joint('CarrierAxis','Link0','Carrier',[0,0,24]);joint('RingAxis','Ring','Carrier',[0,0,24]);parent='Carrier'
    for i,xy in enumerate(p['planet_centers'],1):joint(f'PlanetAxis{i}',f'Planet{i}',parent,xy+[24])
    sun=Polygon(profiles['sun']);void=Polygon(profiles['ring_void']);planet=Polygon(gear_profile(20,phase=-math.pi/20)[0]);maxarea=0.;minclear=math.inf
    for t in np.linspace(0,4,241):
        s=rotate(sun,ws*t,origin=(0,0));v=rotate(void,wr*t,origin=(0,0))
        for i in range(3):
            phi=math.radians(wc*t+120*i);g=translate(rotate(planet,wp*t,origin=(0,0)),45*math.cos(phi),45*math.sin(phi));maxarea=max(maxarea,g.intersection(s).area,g.difference(v).area);minclear=min(minclear,g.distance(s),g.distance(v.boundary))
    assert maxarea==0
    p.update(family='completion_'+kind,joints=joints,drive={'duration_s':4.,'steps':240,'joint':drive,'velocity_deg_s':velocity},fixed_member=fixed,expected_speeds={'sun':ws,'carrier':wc,'ring':wr,'planet':wp},construction='New annular ground housing supports the rotating ring; grounding the '+fixed.lower()+' changes physical motion. Three native external Gear couplers plus one signed-radius native internal Gear pair. The other two internal mesh relations are independently checked from all actual planet trajectories; no contact-force dynamics is claimed.')
    p['independent_calculation']={'functional_dof':1,'willis_residual':40*(ws-wc)+80*(wr-wc),'planet_sun_relative_residual':40*(ws-wc)+20*(wp-wc),'internal_relative_residual':80*(wr-wc)-20*(wp-wc),'sampled_tooth_overlap_mm2':maxarea,'min_sampled_tooth_clearance_mm':minclear}
    p.pop('selection',None);p['native_coupling_strategy']='External Gear x3 + internal Gear(RingAxis,PlanetAxis1,R60,r-15). The 2 remaining internal equations are dependent and must be checked on native trajectories. Each variant requires its own actual validation.'
    return p,profiles
