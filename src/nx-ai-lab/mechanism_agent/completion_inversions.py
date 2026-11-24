"""Distinct grounded members and physical P-pair realizations of the R-R-R-P chain."""
import math
import numpy as np
from .calculations import constraint_rank

CANONICAL={'links':{'L0':'original frame with guide','L1':'original crank','L2':'original connecting rod','L3':'original slider'},'edges':[{'type':'R','links':['L0','L1']},{'type':'R','links':['L1','L2']},{'type':'R','links':['L2','L3']},{'type':'P','links':['L3','L0']}]}

def pose(kind,time):
    if kind in ['guide_bar','oscillating_block']:
        theta=math.radians(30+90*time);pivot=np.array([80.,0]);C=pivot+25*np.array([math.cos(theta),math.sin(theta)]);u=C/np.linalg.norm(C)
        return {'B':[0.,0.],'A':pivot.tolist(),'C':C.tolist(),'u':u.tolist(),'input_deg':math.degrees(theta),'output_deg':math.degrees(math.atan2(C[1],C[0])),'slide_mm':float(np.linalg.norm(C))}
    if kind=='fixed_block':
        theta=math.radians(12+10*math.sin(2*math.pi*.25*time));A=80*np.array([math.cos(theta),math.sin(theta)]);C=np.array([A[0]+math.sqrt(45**2-A[1]**2),0.])
        return {'B':[0.,0.],'A':A.tolist(),'C':C.tolist(),'u':[1.,0.],'input_deg':math.degrees(theta),'output_deg':0.,'slide_mm':float(C[0]),'connecting_deg':math.degrees(math.atan2(-A[1],C[0]-A[0]))}
    raise ValueError(kind)

def design(kind):
    initial=pose(kind,0);B,A,C,u=[initial[k] for k in ['B','A','C','u']]
    if kind=='guide_bar':
        roles={'fixed_original':'L1','Input':'L2','Slider':'L3','Guide':'L0'}
        rows=[('InputPivot','Revolute','Input','Link0',A,18.2,[0,0,1]),('MovingPin','Revolute','Slider','Input',C,27.6,[0,0,1]),('Slide','Slider','Slider','Guide',C,33.8,u+[0]),('GuidePivot','Revolute','Guide','Link0',B,28.2,[0,0,1])];links=['Link0','Input','Slider','Guide']
        physical='Long pivoted T-slot guide and captive moving slider block. Ground is the original crank; the P pair travels with the guide.'
    elif kind=='oscillating_block':
        roles={'fixed_original':'L2','Input':'L1','Rod':'L0','Block':'L3'}
        rows=[('InputPivot','Revolute','Input','Link0',A,18.2,[0,0,1]),('MovingPin','Revolute','Rod','Input',C,27.6,[0,0,1]),('Slide','Slider','Rod','Block',B,33.8,u+[0]),('BlockPivot','Revolute','Block','Link0',B,28.6,[0,0,1])];links=['Link0','Input','Rod','Block']
        physical='Short closed pivoting sleeve and long sliding rod. Ground is the original connecting rod; the original slider rocks about its grounded revolute.'
    elif kind=='fixed_block':
        roles={'fixed_original':'L3','Lever':'L2','Connecting':'L1','Rod':'L0'}
        rows=[('LeverPivot','Revolute','Lever','Link0',B,18.2,[0,0,1]),('LeverPin','Revolute','Connecting','Lever',A,27.6,[0,0,1]),('RodPin','Revolute','Connecting','Rod',C,40,[0,0,1]),('Slide','Slider','Rod','Link0',B,46.2,[1,0,0])];links=['Link0','Lever','Connecting','Rod']
        physical='Fixed short sleeve is the original slider. The original frame becomes a long translating pump rod; an 80 mm grounded rocker drives a 45 mm connecting member. It is not a moving slider block on a grounded long rail. The 35 mm candidate was rejected before modeling because its maximum pressure angle was 58.9 degrees; 45 mm keeps it below 45 degrees.'
    else:raise ValueError(kind)
    joints=[{'name':name,'type':typ,'first':first,'second':second,'origin':xy+[z],'axis':axis} for name,typ,first,second,xy,z,axis in rows]
    dense=[pose(kind,t) for t in np.linspace(0,4,8001)];out=np.array([r['slide_mm'] if kind=='fixed_block' else r['output_deg'] for r in dense]);extrema=[]
    for i in range(1,len(out)-1):
        if (out[i]-out[i-1])*(out[i+1]-out[i])<0:extrema.append(i*4/8000)
    arcs=[extrema[1]-extrema[0],4-extrema[1]+extrema[0]];rank=constraint_rank(links,joints,'Link0');assert rank['functional_dof']==1
    if kind=='guide_bar':angles={'transmission_angle_deg':90.,'pressure_angle_deg':0.,'scope':'Centric guide output; ideal normal slot force is tangential to the guide rotation.'}
    elif kind=='oscillating_block':angles={'transmission_angle_deg':None,'pressure_angle_deg':None,'scope':'The short sleeve transfers a couple through its finite prismatic bearing. A single force-line transmission angle is not defined for this output; use closure and motion Jacobian.'}
    else:
        pressure=[abs(r['connecting_deg']) for r in dense];angles={'max_pressure_angle_deg':max(pressure),'min_transmission_angle_deg':90-max(pressure),'scope':'Force direction in the pin-pin connecting member relative to translating rod velocity.'}
    independent={'rank':rank,'local_freedoms':0,'compound_hinges':[],'virtual_constraints':'Three dependent spatial rows for planar R-R-R-P, corroborate native reduction and body normals','output_min':float(min(out)),'output_max':float(max(out)),'output_range':float(np.ptp(out)),'output_unit':'mm' if kind=='fixed_block' else 'degree','extreme_times_s':extrema,'stroke_durations_s':arcs,'quick_return_ratio':max(arcs)/min(arcs),'extreme_position_angle_deg':None if kind=='fixed_block' else abs(arcs[0]*90-180),'stationary_input_angles_deg':[pose(kind,t)['input_deg'] for t in extrema],'rotation_condition':{'fixed_pivot_spacing_mm':80,'crank_radius_mm':25,'full_rotation':True} if kind!='fixed_block' else {'rocker_range_deg':[2,22],'closure_limit_deg':math.degrees(math.asin(45/80)),'full_grounded_rocker_rotation':False},**angles}
    drive={'joint':'LeverPivot','type':'harmonic','amplitude_deg':10.,'frequency_hz':.25,'phase_deg':0,'offset_deg':0.,'duration_s':4.,'steps':240} if kind=='fixed_block' else {'joint':'InputPivot','type':'constant','velocity_deg_s':90.,'duration_s':4.,'steps':240}
    return {'schema_version':1,'family':'completion_'+kind,'units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'ground_distance_or_lever_mm':80.,'crank_or_connecting_mm':45. if kind=='fixed_block' else 25.,'shaft_diameter':6.,'bore_diameter':6.4,'prismatic_side_clearance_mm':.2,'prismatic_vertical_clearance_mm':.2},'canonical_chain':CANONICAL,'grounded_member_and_roles':roles,'physical_realization':physical,'initial':initial,'rigid_links':links,'joints':joints,'independent_calculation':independent,'drive':drive,'tolerances':{'marker_mm':.01,'angle_deg':.02,'quick_return_ratio':.005,'body_normal_mm_per_10mm':.001},'repair_limit':3}
