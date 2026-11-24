"""Independent Delta loop closure, mobility and sampled singularity analysis."""
import math
import numpy as np
from .calculations import constraint_rank
from .selection import Requirement, select

R, PLATFORM_R, UPPER, LOWER, HALF_WIDTH = 85., 25., 65., 190., 18.


def forward(angles_deg):
    q = np.radians(angles_deg)
    radial = np.array([[math.cos(i*2*math.pi/3), math.sin(i*2*math.pi/3), 0] for i in range(3)])
    tangent = np.cross([0,0,1], radial)
    anchors = R*radial
    elbows = anchors + UPPER*np.cos(q)[:,None]*radial - np.c_[np.zeros((3,2)), UPPER*np.sin(q)]
    centers = elbows - PLATFORM_R*radial
    ex = centers[1]-centers[0];d=np.linalg.norm(ex);ex/=d
    v=centers[2]-centers[0];i=v@ex;ey=v-i*ex;j=np.linalg.norm(ey)
    if min(d,j)<1e-8:raise ValueError('Degenerate sphere-center triangle')
    ey/=j;ez=np.cross(ex,ey);x=d/2;y=(i*i+j*j-2*i*x)/(2*j)
    h2=LOWER**2-x*x-y*y
    if h2<=0:raise ValueError('Unreachable or tangent sphere intersection')
    base=centers[0]+x*ex+y*ey
    candidates=[base+sign*math.sqrt(h2)*ez for sign in [-1,1]]
    platform=min(candidates,key=lambda p:p[2])
    ends=platform+PLATFORM_R*radial
    rods=ends-elbows;unit=rods/LOWER
    A=unit;dB=-UPPER*np.sin(q)[:,None]*radial-np.c_[np.zeros((3,2)),UPPER*np.cos(q)]
    B=np.sum(unit*dB,axis=1)/UPPER
    singular=np.linalg.svd(A,compute_uv=False)
    return {'angles_deg':list(map(float,angles_deg)),'radial':radial.tolist(),'tangent':tangent.tolist(),
            'anchors':anchors.tolist(),'elbows':elbows.tolist(),'platform':platform.tolist(),'ends':ends.tolist(),
            'rod_directions':unit.tolist(),'A_min_singular':float(min(singular)),'A_condition':float(max(singular)/min(singular)),
            'B_min_abs_normalized':float(min(abs(B))),'sphere_closure_max_mm':float(max(abs(np.linalg.norm(rods,axis=1)-LOWER)))}


def joints_at(pose):
    result=[]
    for i in range(3):
        t=np.array(pose['tangent'][i]);b=np.array(pose['elbows'][i]);c=np.array(pose['ends'][i]);normal=np.cross(t,c-b);normal/=np.linalg.norm(normal)
        result.append({'name':f'Base{i+1}','type':'Revolute','first':f'Upper{i+1}','second':'Link0','origin':pose['anchors'][i],'axis':t.tolist()})
        for sign,side in [(-1,'L'),(1,'R')]:
            name=f'{i+1}{side}';B=(b+sign*HALF_WIDTH*t).tolist();C=(c+sign*HALF_WIDTH*t).tolist()
            result.append({'name':f'U{name}','type':'Universal','first':f'Upper{i+1}','second':f'Rod{name}','origin':B,'axis':t.tolist(),'axis2':normal.tolist()})
            result.append({'name':f'S{name}','type':'Spherical','first':f'Rod{name}','second':'Platform','origin':C,'axis':[0,0,1]})
    return result


def design():
    initial=forward([30,30,30]);joints=joints_at(initial);links=['Link0','Upper1','Upper2','Upper3']+[f'Rod{i}{s}' for i in range(1,4) for s in ['L','R']]+['Platform']
    rank=constraint_rank(links,joints,'Link0');assert rank['functional_dof']==3 and rank['redundant_rows']==0
    workspace=[]
    for a in np.linspace(20,45,6):
        for b in np.linspace(20,45,6):
            for c in np.linspace(20,45,6):
                q=forward([a,b,c]);workspace.append({k:q[k] for k in ['angles_deg','platform','A_min_singular','A_condition','B_min_abs_normalized']})
    minA=min(x['A_min_singular'] for x in workspace);minB=min(x['B_min_abs_normalized'] for x in workspace)
    assert minA>.2 and minB>.2
    velocities=[4,-2,3];samples=[forward(np.array([30,30,30])+np.array(velocities)*t) for t in np.linspace(0,2,61)]
    return {'schema_version':1,'family':'delta','selection':select(Requirement(input_motion='three_rotary_inputs',output_motion='spatial_translation',source_text='设计一个三维平移、动平台保持姿态的轻型并联机构')),
      'units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'base_joint_radius':R,'platform_joint_radius':PLATFORM_R,'upper_length':UPPER,'lower_length':LOWER,'parallel_rod_spacing':2*HALF_WIDTH,'initial_angles_deg':[30,30,30],'absolute_joint_range_deg':[20,45],'rod_diameter':5,'ball_diameter':10,'socket_inner_diameter':10.6,'socket_outer_diameter':15,'socket_port_diameter':8.4,'base_pin_diameter':6,'base_bore_diameter':6.4},
      'initial':initial,'rigid_links':links,'joints':joints,'independent_calculation':{'rank':rank,'functional_mobility':3,'local_unmodeled_spin_dof':0,'topology':'3 base R, 6 upper U, 6 lower S; 10 moving rigid bodies; 60 - (15+24+18) = 3','six_S_S_rods_alternative':'Spherical joints at both rod ends introduce six additional local rod spins. This design uses explicit native upper Universal joints, not a subtraction from the solver DOF.'},
      'workspace':{'source':'Independent sphere-intersection calculation, not NX collision validation','samples':workspace,'min_A_singular':minA,'min_B_abs_normalized':minB},
      'drive':{'joints':['Base1','Base2','Base3'],'velocity_deg_s':velocities,'duration_s':2,'steps':60,'relative_limits_deg':[-10,15]},'independent_trajectory':samples,
      'tolerances':{'marker_mm':.02,'orientation_deg':.01,'joint_closure_mm':.02,'axis_dot':1e-4,'min_A_singular':.2,'min_B_abs_normalized':.2},'repair_limit':3,
      'construction':'Independent base, three upper forks, six rods and a platform. Real spherical bearing envelopes have 0.3 mm radial clearance and relieved neck ports. Upper native Universal cross-pin joints prevent rod spin; spherical envelope geometry is a simplified bearing housing, not a detailed manufactured gimbal. No bearing strength, load rating or dynamics claim. Component transforms start at identity; dimensions and joint centres establish the closed structure.'}
