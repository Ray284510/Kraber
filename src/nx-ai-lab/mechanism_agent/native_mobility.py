"""Independent spatial rank for native lower pairs plus native Gear relations."""
import numpy as np
from .calculations import constraint_matrix,skew

def gear_rank(audit,coupler_state,ground='Link0'):
    if not audit.get('ok') or not coupler_state.get('ok'):raise ValueError('Successful native query records required')
    joints=[]
    for j in audit['joints']:
        row={'name':j['name'],'type':j['type'],'first':j['first'],'second':j['second'],'origin':[j['origin1'][k] for k in 'XYZ'],'axis':[j['axis1'][k] for k in 'XYZ']}
        if j['type']=='Universal':row['axis2']=[j['axis2'][k] for k in 'XYZ']
        joints.append(row)
    names={j['name']:j for j in joints};bodies=sorted({j[k] for j in joints for k in ['first','second']});moving=[b for b in bodies if b!=ground];J=constraint_matrix(bodies,joints,ground);rows=[];relations=[]
    for coupling in coupler_state['couplers']:
        if coupling.get('suppressed'):continue
        if coupling['type'] not in ('Gear','RackPinion'):raise ValueError('Do not omit unmodeled coupler type '+coupling['type'])
        row=np.zeros(6*len(moving))
        if coupling['type']=='RackPinion':
            first,second=names[coupling['first_joint']],names[coupling['second_joint']]
            if first['type']!='Slider' or second['type']!='Revolute':raise ValueError('Rack rank currently supports native first=Slider/second=Revolute convention only')
            for joint,scale in [(first,1),(second,coupling['ratio'])]:
                axis=np.array(joint['axis']);axis=axis/np.linalg.norm(axis)
                block=axis@np.c_[np.eye(3),-skew(joint['origin'])] if joint['type']=='Slider' else np.r_[np.zeros(3),axis]
                for body,sign in [(joint['first'],1),(joint['second'],-1)]:
                    if body!=ground:i=moving.index(body);row[6*i:6*i+6]+=scale*sign*block
            rows.append(row);relations.append(coupling['name']);continue
        for key,radius in [('first_joint','first_radius'),('second_joint','second_radius')]:
            joint=names[coupling[key]]
            if joint['type']!='Revolute':raise ValueError('Gear rank supports revolute/revolute only')
            axis=np.array(joint['axis']);axis=axis/np.linalg.norm(axis)
            for body,sign in [(joint['first'],1),(joint['second'],-1)]:
                if body!=ground:i=moving.index(body);row[6*i+3:6*i+6]+=sign*coupling[radius]*axis
        rows.append(row);relations.append(coupling['name'])
    matrix=np.vstack([J,*rows]);rank=int(np.linalg.matrix_rank(matrix,tol=1e-8))
    return {'source':'Independent screw-velocity matrix from actual NX joint origins/axes and signed Gear/RackPinion relations; no prescribed driver included','moving_links':moving,'gear_relations':relations,'coordinates':matrix.shape[1],'constraint_rows':matrix.shape[0],'rank':rank,'redundant_rows':matrix.shape[0]-rank,'functional_dof':matrix.shape[1]-rank,'singular_values':np.linalg.svd(matrix,compute_uv=False).tolist(),'limits':'At queried design configuration. Higher pairs, contact and General are rejected rather than silently ignored. RackPinion equation is specific to the actually verified first=Slider/second=Revolute convention.'}
