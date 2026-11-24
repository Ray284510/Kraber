import math
import numpy as np

def planar_mobility(moving_links,lower_pairs,higher_pairs=0,local_dof=0,redundancy=0):
    return {'nominal':3*moving_links-2*lower_pairs-higher_pairs,'functional':3*moving_links-2*lower_pairs-higher_pairs-local_dof+redundancy,'local_dof':local_dof,'redundancy':redundancy}

def compound_revolute_pairs(bodies):
    if len(set(bodies))!=len(bodies) or len(bodies)<2:raise ValueError('Distinct bodies required')
    return [(bodies[0],b) for b in bodies[1:]]

def skew(p):
    x,y,z=p;return np.array([[0,-z,y],[z,0,-x],[-y,x,0]])

def constraint_matrix(bodies,joints,ground='Ground'):
    moving=[b for b in bodies if b!=ground];rows=[]
    for j in joints:
        a=np.array(j.get('axis',[0,0,1]),float);a/=np.linalg.norm(a);u=np.cross(a,[1,0,0] if abs(a[0])<.9 else [0,1,0]);u/=np.linalg.norm(u);v=np.cross(a,u);p=np.array(j['origin'],float)
        trans=np.c_[np.eye(3),-skew(p)];angular=np.c_[np.zeros((3,3)),np.eye(3)]
        typ=j['type']
        if typ=='Revolute':block=np.vstack([trans,np.array([u,v])@angular])
        elif typ=='Slider':block=np.vstack([np.array([u,v])@trans,angular])
        elif typ=='Spherical':block=trans
        elif typ=='Universal':
            b=np.asarray(j['axis2'],float);b/=np.linalg.norm(b);normal=np.cross(a,b)
            if np.linalg.norm(normal)<1e-8:raise ValueError('Universal cross-pin axes are parallel')
            normal/=np.linalg.norm(normal);block=np.vstack([trans,normal@angular])
        elif typ=='Fixed':block=np.vstack([trans,angular])
        else:raise ValueError('Rank model not implemented: '+typ)
        row=np.zeros((len(block),6*len(moving)))
        for body,sign in [(j['first'],1),(j['second'],-1)]:
            if body!=ground:idx=moving.index(body);row[:,idx*6:idx*6+6]=sign*block
        rows.extend(row)
    return np.array(rows).reshape((-1,6*len(moving)))


def constraint_rank(bodies,joints,ground='Ground',local_dof=0):
    moving=[b for b in bodies if b!=ground];J=constraint_matrix(bodies,joints,ground);sv=np.linalg.svd(J,compute_uv=False);rank=int(np.linalg.matrix_rank(J,tol=1e-8))
    return {'coordinates':6*len(moving),'constraint_rows':len(J),'rank':rank,'redundant_rows':len(J)-rank,'instantaneous_dof':6*len(moving)-rank,'functional_dof':6*len(moving)-rank-local_dof,'singular_values':sv.tolist(),'note':'Rank at this configuration; scan configurations to detect singularity. Local freedoms require a documented physical justification.'}

def classify_fourbar(lengths):
    if len(lengths)!=4 or min(lengths)<=0:raise ValueError('Four positive lengths')
    s,p,q,l=sorted(lengths)
    if l>=s+p+q:raise ValueError('Degenerate or unassemblable quadrilateral')
    margin=p+q-s-l;k=int(np.argmin(lengths))
    kind='change_point' if abs(margin)<1e-9 else 'double_rocker' if margin<0 or k==2 else 'double_crank' if k==0 else 'crank_rocker'
    return {'type':kind,'grashof_margin_mm':margin,'grashof':margin>=0,'shortest_index':k}

def fourbar_pose(d,r,l,s,theta,branch=1):
    a=np.array([r*math.cos(theta),r*math.sin(theta)]);o=np.array([d,0.]);delta=o-a;D=float(np.linalg.norm(delta));u=delta/D;x=(l*l-s*s+D*D)/(2*D);h2=l*l-x*x
    if h2< -1e-8:raise ValueError('Linkage cannot close at requested angle')
    c=a+x*u+branch*math.sqrt(max(h2,0))*np.array([-u[1],u[0]])
    mu=math.acos(float(np.clip(np.dot((a-c)/l,(o-c)/s),-1,1)));acute=min(mu,math.pi-mu)
    return {'A':a.tolist(),'C':c.tolist(),'rocker_angle':math.atan2(c[1],c[0]-d),'coupler_angle':math.atan2(c[1]-a[1],c[0]-a[0]),'transmission_angle_deg':math.degrees(acute),'pressure_angle_deg':90-math.degrees(acute)}

def fourbar_metrics(d,r,l,s):
    classification=classify_fourbar([d,r,l,s])
    cosine_low=(d*d+r*r-(l+s)**2)/(2*d*r);cosine_high=(d*d+r*r-(l-s)**2)/(2*d*r)
    full_rotation=cosine_low<=-1 and cosine_high>=1
    if not full_rotation:
        lower=math.degrees(math.acos(max(-1,min(1,cosine_high))));upper=math.degrees(math.acos(max(-1,min(1,cosine_low))))
        return {'classification':classification,'grounded_input_full_rotation':False,'feasible_grounded_input_intervals_deg':[[lower,upper],[360-upper,360-lower]],'dead_center_input_deg':None,'stroke_input_arcs_deg':None,'extreme_position_angle_deg':None,'quick_return_ratio':None,'min_transmission_angle_deg':0.,'max_pressure_angle_deg':90.,'planar_mobility':planar_mobility(3,4),'scope':'The specified grounded input rocks within these assembly limits. A complete motion law or another actuated joint is required for time ratio and output extrema; do not assume a 360-degree grounded input.'}
    data=[fourbar_pose(d,r,l,s,t) for t in np.linspace(0,2*np.pi,3601)]
    roots=[]
    for k in [r+l,r-l]:
        if abs(k)<1e-12:continue
        c=(k*k+d*d-s*s)/(2*d*k)
        if abs(c)<=1:
            t=math.acos(c);t=t if k>0 else 2*math.pi-t;roots.append(math.degrees(t))
    delta=(roots[1]-roots[0])%360 if len(roots)==2 else None
    return {'classification':classification,'grounded_input_full_rotation':True,'dead_center_input_deg':roots,'stroke_input_arcs_deg':[delta,360-delta] if delta is not None else None,'extreme_position_angle_deg':abs(delta-180) if delta is not None else None,'quick_return_ratio':max(delta,360-delta)/min(delta,360-delta) if delta is not None else None,'min_transmission_angle_deg':min(x['transmission_angle_deg'] for x in data),'max_pressure_angle_deg':max(x['pressure_angle_deg'] for x in data),'planar_mobility':planar_mobility(3,4),'scope':'Time ratio is not applicable when the output rotates continuously.' if delta is None else 'Constant-speed grounded input and reciprocating output on the selected branch.'}

def slider_crank(r,l,theta,offset=0):
    if not all(math.isfinite(v) for v in (r,l,offset)) or r<=0 or l<=r+abs(offset):raise ValueError('Positive finite dimensions and full rotation required')
    lateral=offset-r*np.sin(theta);root=np.sqrt(l*l-lateral*lateral);x=r*np.cos(theta)+root
    pressure=np.abs(np.arcsin(lateral/l))
    outer=math.sqrt((l+r)**2-offset**2);inner=math.sqrt((l-r)**2-offset**2)
    a=math.asin(offset/(l+r));b=math.pi+math.asin(offset/(l-r));arc=b-a
    return {'x':np.asarray(x).tolist(),'pressure_angle_deg':np.degrees(pressure).tolist(),'inline_stroke':2*r if offset==0 else None,'stroke_mm':outer-inner,'position_extremes_mm':[inner,outer],'quick_return_ratio':max(arc,2*math.pi-arc)/min(arc,2*math.pi-arc),'dead_centers_deg':[math.degrees(a)%360,math.degrees(b)%360],'extreme_position_angle_deg':abs(math.degrees(arc)-180),'maximum_pressure_angle_deg':math.degrees(math.asin((abs(offset)+r)/l))}

def cycloidal(u):
    u=np.asarray(u);return u-np.sin(2*np.pi*u)/(2*np.pi),1-np.cos(2*np.pi*u),2*np.pi*np.sin(2*np.pi*u)

def planetary_ratio(sun,planet,ring):
    if min(sun,planet,ring)<=0 or ring!=sun+2*planet:raise ValueError('Concentric standard planetary geometry requires Zr=Zs+2Zp')
    return {'fixed_ring_sun_to_carrier_ratio':1+ring/sun,'willis_relation':'(ws-wc)/(wr-wc)=-Zr/Zs','sun_planet_mesh':'external','planet_ring_mesh':'internal'}

def planetary_mesh_rank(sun,planet,ring,number):
    planetary_ratio(sun,planet,ring)
    if not isinstance(number,int) or number<1:raise ValueError('Positive planet count required')
    # Coordinates: carrier absolute, sun relative carrier, planets relative carrier.
    rows=[]
    for i in range(number):
        external=[0.]*(number+2);external[1]=sun;external[i+2]=planet;rows.append(external)
        internal=[0.]*(number+2);internal[0]=ring;internal[i+2]=planet;rows.append(internal)
    matrix=np.asarray(rows);rank=int(np.linalg.matrix_rank(matrix))
    return {'coordinate_order':['carrier','sun_relative']+[f'planet{i+1}_relative' for i in range(number)],'matrix':rows,'coordinates':number+2,'constraint_rows':len(rows),'rank':rank,'redundant_equations':len(rows)-rank,'functional_dof':number+2-rank}
