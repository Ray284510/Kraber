"""Four-R inversions with explicit grounded link and real motion-input choice."""
import math
import numpy as np
from .calculations import classify_fourbar,constraint_rank

def pose(kind,angle_deg):
    q=math.radians(angle_deg)
    if kind=='double_crank':
        d,r,l,s=40.,90.,70.,100.
        A=np.array([r*math.cos(q),r*math.sin(q)]);delta=np.array([d,0])-A;distance=np.linalg.norm(delta)
        along=(l*l-s*s+distance*distance)/(2*distance);normal=math.sqrt(l*l-along*along);u=delta/distance
        C=A+along*u+normal*np.array([-u[1],u[0]])
    elif kind=='double_rocker':
        d,r,l,s=100.,90.,25.,70.
        # Driven coordinate is the relative coupler/input angle, not either grounded rocker.
        vector=complex(r,0)+l*complex(math.cos(q),math.sin(q));radius=abs(vector)
        absolute=math.acos((radius*radius+d*d-s*s)/(2*radius*d))-math.atan2(vector.imag,vector.real)
        A=r*np.array([math.cos(absolute),math.sin(absolute)])
        C=A+l*np.array([math.cos(absolute+q),math.sin(absolute+q)])
    else:raise ValueError(kind)
    return {'O':[0.,0.],'A':A.tolist(),'C':C.tolist(),'D':[d,0.]}

def measure(points):
    O,A,C,D=[np.asarray(points[k]) for k in ['O','A','C','D']]
    qin=math.atan2(A[1],A[0]);qout=math.atan2(C[1]-D[1],C[0]-D[0]);qc=math.atan2(C[1]-A[1],C[0]-A[0])
    u=A-C;v=D-C;angle=math.degrees(math.acos(float(np.clip(u@v/np.linalg.norm(u)/np.linalg.norm(v),-1,1))))
    return {'input_angle_rad':qin,'output_angle_rad':qout,'coupler_angle_rad':qc,'geometric_transmission_angle_deg':min(angle,180-angle)}

def design(kind):
    dims={'double_crank':[40.,90.,70.,100.],'double_rocker':[100.,90.,25.,70.]}[kind]
    initial=30. if kind=='double_crank' else 60.;points=pose(kind,initial);O,A,C,D=[points[k] for k in ['O','A','C','D']]
    joints=[{'name':name,'type':'Revolute','first':a,'second':b,'origin':xy+[z],'axis':[0,0,1]} for name,a,b,xy,z in [('J0','Input','Link0',O,18.2),('J1','Coupler','Input',A,27.6),('J2','Coupler','Output',C,40.),('J3','Output','Link0',D,49.4)]]
    angles=np.linspace(initial,initial+360,7201);values=[measure(pose(kind,q)) for q in angles];out=np.unwrap([v['output_angle_rad'] for v in values]);inp=np.unwrap([v['input_angle_rad'] for v in values]);extrema=[]
    for i in range(1,len(out)-1):
        if (out[i]-out[i-1])*(out[i+1]-out[i])<0:extrema.append(float(angles[i]%360))
    extrema.sort();arcs=[extrema[1]-extrema[0],360-extrema[1]+extrema[0]] if len(extrema)==2 else []
    rank=constraint_rank(['Link0','Input','Coupler','Output'],joints,'Link0');assert rank['functional_dof']==1
    classification=classify_fourbar(dims);assert classification['type']==kind
    metrics={'classification':classification,'rank':rank,'local_freedoms':0,'compound_hinges':[],'output_winding_deg':math.degrees(out[-1]-out[0]),'grounded_input_winding_deg':math.degrees(inp[-1]-inp[0]),'output_range_deg':math.degrees(np.ptp(out)),'grounded_input_range_deg':math.degrees(np.ptp(inp)),'output_stationary_driver_angles_deg':extrema,'stroke_driver_arcs_deg':arcs,'quick_return_ratio':max(arcs)/min(arcs) if arcs else None,'extreme_position_angle_deg':abs(arcs[0]-180) if arcs else None,'min_geometric_transmission_angle_deg':min(v['geometric_transmission_angle_deg'] for v in values),'max_geometric_pressure_angle_deg':90-min(v['geometric_transmission_angle_deg'] for v in values),'scope':'Stationary points and time ratio refer to the actual driven coordinate. Double-crank output does not reciprocate, so quick-return ratio and extreme-position angle are not applicable. For internally actuated double-rocker, coupler is not a two-force link; geometric angles are not force-efficiency validation.'}
    return {'schema_version':1,'family':'completion_'+kind,'units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'lengths_ground_input_coupler_output':dims,'initial_driver_deg':initial,'input_z':18.4,'coupler_z':30.8,'output_z':43.2,'bar_thickness':6.,'bore':6.4,'shaft':6.,'upper_ground_bearing_bottom':49.6,'upper_bridge_height':63.6,'pin_c_head_top_z':30.6,'pin_c_coupler_axial_clearance_mm':.2},'fixed_link_choice':'Shortest member grounded' if kind=='double_crank' else 'Member opposite shortest coupler grounded','initial_points':points,'rigid_links':['Link0','Input','Coupler','Output'],'joints':joints,'independent_calculation':metrics,'drive':{'joint':'J0' if kind=='double_crank' else 'J1','velocity_deg_s':90,'duration_s':4,'steps':240},'tolerances':{'marker_mm':.01,'angle_deg':.01,'quick_return_ratio':.005,'stationary_angle_deg':.1},'construction':'Input and output bars occupy separate axial layers. Opposed ground bearings and an elevated frame avoid a fixed shaft crossing the rotating input. Coupler connects in the middle layer with real stepped-length pivot shafts. No collision-hiding component repositioning.','repair_limit':3}
