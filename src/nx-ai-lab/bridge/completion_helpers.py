"""Shared actual-NX construction helpers for explicit mechanism topology."""
import numpy as np

async def rectangle(run,c,name,origin,u,start,end,width,z,depth,body=None,mode='create',offset=0):
    u=np.array(u,float);u/=np.linalg.norm(u);n=np.array([-u[1],u[0]]);origin=np.array(origin,float)+offset*n
    points=[origin+start*u-width/2*n,origin+end*u-width/2*n,origin+end*u+width/2*n,origin+start*u+width/2*n]
    run.counter+=1;file=run.model/(name+'_'+str(run.counter)+'.csv');file.write_text('\n'.join(','.join(map(str,p)) for p in points))
    if body:r=await c.call(f'nx_profile_boolean {name} {file} {z} {depth} {"subtract" if mode=="subtract" else "unite"} {body} polyline')
    else:r=await c.call(f'nx_profile_solid {name} {file} {z} {depth} polyline')
    await c.call('nx_save_part');return r['body_id']

async def datum(c,refs,part,key,point,axis=(0,0,1)):
    refs.setdefault(part,{})[key]=await c.call('nx_joint_reference '+' '.join(map(str,[key,*point,*axis])))

async def hinges_and_slider(run,c,refs,hinges,slider):
    actual={name:await c.call('nx_component_datums '+name.upper()) for name in refs};run.save('native-datums.json',actual)
    for part,items in refs.items():
        points={x['name'] for x in actual[part]['prototype_points'] if x['occurrence_available']};curves={x['name'] for x in actual[part]['prototype_curves'] if x['occurrence_available']}
        for row in items.values():assert row['point_name'] in points and row['curve_name'] in curves
    for key,a,b in hinges:
        for kind,field,prefix in [('touch','point_name','point:'),('align','curve_name','curve:')]:await c.call(f'nx_constraint {kind} {key}_{kind} {a.upper()} {prefix+refs[a][key][field]} {b.upper()} {prefix+refs[b][key][field]} 0 same')
    child,parent,u,side_child,side_parent,bottom_child,floor_parent=slider;n=np.array([-u[1],u[0],0.]);cg=await c.call('nx_component_geometry '+child.upper());pg=await c.call('nx_component_geometry '+parent.upper());run.save('slider-native-faces.json',{'child':cg,'parent':pg})
    def plane(geo,direction,plane_coordinate):
        eligible=[f for f in geo['faces'] if f['kind']=='Planar' and np.dot(f['direction'],direction)>.999]
        chosen=min(eligible,key=lambda f:abs(np.dot(f['point_mm'],direction)-plane_coordinate))
        assert abs(np.dot(chosen['point_mm'],direction)-plane_coordinate)<.001
        return chosen['reference']
    face_c=plane(cg,n,side_child);face_p=plane(pg,-n,-side_parent)
    await c.call(f'nx_constraint distance SlideSide {child.upper()} {face_c} {parent.upper()} {face_p} {side_parent-side_child} opposite')
    face_c=plane(cg,[0,0,-1],-bottom_child);face_p=plane(pg,[0,0,1],floor_parent)
    await c.call(f'nx_constraint distance SlideHeight {child.upper()} {face_c} {parent.upper()} {face_p} {bottom_child-floor_parent} opposite')

async def finish_assembly(run,c,owners):
    state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);await c.call('nx_save_part')
    assert state['all_prototypes_fully_loaded'] and state['unsatisfied_constraint_count']==0 and all(x['dof_result']=='Success' for x in state['components'])
    inter=await c.call('nx_interference');run.save('static-interference.json',inter);await run.export(c,'assembly')
    assert not any(p['nx_result']==1 or (p['nx_result']==3 and owners[p['a']]!=owners[p['b']]) for p in inter['pairs'])
    run.meta['build_passed']=True;run.meta['component_rigid_owners']=owners;run.pointer()
