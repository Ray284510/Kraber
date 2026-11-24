"""Joint-graph and compound-hinge analysis from actual NX joint query rows."""
import collections
import numpy as np
from .calculations import constraint_rank

def from_native_audit(audit,ground='Link0'):
    if not audit.get('ok'):raise ValueError('Successful actual NX audit required')
    joints=[]
    for row in audit['joints']:
        joints.append({'name':row['name'],'type':row['type'],'first':row['first'],'second':row['second'],'origin':[row['origin1'][k] for k in 'XYZ'],'axis':[row['axis1'][k] for k in 'XYZ']})
        if row['type']=='Universal':joints[-1]['axis2']=[row['axis2'][k] for k in 'XYZ']
    return analyze(joints,ground)

def analyze(joints,ground='Link0',axis_tolerance_mm=1e-6):
    bodies=sorted({j[k] for j in joints for k in ['first','second']})
    if ground not in bodies:raise ValueError('Explicit grounded body must occur in joint graph')
    groups=[]
    for j in joints:
        if j['type']!='Revolute':continue
        axis=np.array(j['axis'],float);axis/=np.linalg.norm(axis);point=np.array(j['origin'],float)
        for group in groups:
            if np.linalg.norm(np.cross(axis,group['axis']))<1e-9 and np.linalg.norm(np.cross(point-group['point'],axis))<axis_tolerance_mm:
                group['joints'].append(j);break
        else:groups.append({'axis':axis,'point':point,'joints':[j]})
    compounds=[]
    for group in groups:
        adjacency=collections.defaultdict(set)
        for j in group['joints']:adjacency[j['first']].add(j['second']);adjacency[j['second']].add(j['first'])
        remaining=set(adjacency)
        while remaining:
            pending=[remaining.pop()];component=set(pending)
            while pending:
                for neighbour in adjacency[pending.pop()]-component:component.add(neighbour);remaining.discard(neighbour);pending.append(neighbour)
            if len(component)>2:
                edges=[j['name'] for j in group['joints'] if j['first'] in component and j['second'] in component]
                compounds.append({'coaxial_connected_bodies':sorted(component),'native_revolute_names':edges,'equivalent_independent_revolute_pairs':len(component)-1,'extra_graph_edges':len(edges)-(len(component)-1)})
    rank=constraint_rank(bodies,joints,ground)
    counts=dict(collections.Counter(j['type'] for j in joints))
    return {'ground':ground,'moving_bodies':[b for b in bodies if b!=ground],'joint_counts':counts,'ground_incident_joints':[j['name'] for j in joints if ground in [j['first'],j['second']]],'compound_hinge_clusters':compounds,'independent_spatial_rank':rank,'scope':'Lower-pair graph and geometry only. Redundant rows may be expected planar dependencies, not a conflict. Physical local freedoms need an output definition and a mechanical/native variation check; they are never subtracted to force the desired answer. Higher-pair constraints are not included by this routine.'}
