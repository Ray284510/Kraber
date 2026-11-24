"""Exact eccentric-disk reference geometry, independent of the NX contact solver."""
import math
import numpy as np

KINDS=('cam_external_roller','cam_external_tip','cam_oscillating_roller','cam_oscillating_tip')
R=35.; E=5.; P=np.array([-50.,40.]); ARM=45.

def pose(kind,t):
    theta=math.pi*t/2; center=E*np.array([math.cos(theta),math.sin(theta)])
    roller='roller' in kind; radius=6. if roller else 0.; pitch=R+radius
    if 'oscillating' in kind:
        delta=center-P; distance=np.linalg.norm(delta); u=delta/distance
        along=(ARM**2-pitch**2+distance**2)/(2*distance)
        h=math.sqrt(ARM**2-along**2)
        point=P+along*u+h*np.array([-u[1],u[0]])
        arm=(point-P)/ARM; velocity_direction=np.array([-arm[1],arm[0]])
        angle=math.atan2(arm[1],arm[0]); v=a=None
    else:
        root=math.sqrt(pitch**2-E**2*math.cos(theta)**2)
        point=np.array([0.,center[1]+root]);angle=0.;velocity_direction=np.array([0.,1.])
        v=(E*math.cos(theta)+E**2*math.sin(theta)*math.cos(theta)/root)*math.pi/2
        a=(-E*math.sin(theta)+E**2*math.cos(2*theta)/root-E**4*(math.sin(theta)*math.cos(theta))**2/root**3)*(math.pi/2)**2
    normal=(point-center)/pitch
    pressure=math.degrees(math.acos(min(1.,abs(float(np.dot(normal,velocity_direction))))))
    return {'center':center.tolist(),'point':point.tolist(),'angle_rad':angle,'pressure_deg':pressure,'normal':normal.tolist(),'velocity_mm_s':v,'acceleration_mm_s2':a}

def design(kind):
    assert kind in KINDS
    roller='roller' in kind;osc='oscillating' in kind;initial=pose(kind,0);point=initial['point']
    rows=[('CamAxis','Revolute','Cam','Link0',[0,0,24],[0,0,1]),('FollowerJoint','Revolute' if osc else 'Slider','Follower','Link0',[*P,33.4] if osc else [0,70,33.4],[0,0,1] if osc else [0,1,0])]
    if roller:rows.append(('RollerAxis','Revolute','Roller','Follower',point+[24],[0,0,1]))
    samples=[pose(kind,t) for t in np.linspace(0,4,1441)]
    max_pressure=max(x['pressure_deg'] for x in samples)
    assert max_pressure<45, 'Redesign pivot or base circle before creating NX geometry'
    return {'schema_version':1,'family':'completion_'+kind,'units':{'length':'mm','angle':'degree','time':'s'},'parameters':{'cam_radius_mm':R,'eccentricity_mm':E,'roller_radius_mm':6 if roller else 0,'arm_length_mm':ARM if osc else None,'pivot_mm':P.tolist() if osc else None},'initial':initial,'joints':[{'name':n,'type':k,'first':f,'second':s,'origin':o,'axis':a} for n,k,f,s,o,a in rows], 'drive':{'duration_s':4.,'steps':240,'velocity_deg_s':90},'tolerances':{'marker_mm':.01,'contact_gap_mm':.001,'intersection_volume_mm3':0},'repair_limit':3,'independent_calculation':{'functional_dof':1,'planar_mobility_equation':'3*n - 2*lower_pairs - normal_contact - no_slip = 1' if roller else '3*2 - 2*2 - point_curve = 1','roller_local_spin':'Determined by native no-slip constraint; not silently removed' if roller else None,'max_pressure_angle_deg':max_pressure,'minimum_pitch_curvature_radius_mm':R+(6 if roller else 0),'minimum_actual_curvature_radius_mm':R,'undercut_risk':False,'reason':'Exact convex circle; pitch radius exceeds roller radius by 35 mm','y_range_mm':float(np.ptp([x['point'][1] for x in samples])),'angle_range_deg':math.degrees(float(np.ptp([x['angle_rad'] for x in samples])))},'physical_realization':'Exact eccentric circular disk and actual external follower geometry. Native geometric contact is a bilateral kinematic relation, not a preload or force-contact dynamics validation.'}
