import asyncio,json,math
import numpy as np
from phase4_common import Run,Client,LOG
from mechanism_agent.delta_plan import design


async def main():
 assert json.loads((LOG/'acceptance.json').read_text())['planetary']['passed']
 run=Run('delta',design());p=run.plan;initial=p['initial'];refs={}
 async with Client() as c:
  assert not (await c.call('nx_status')).get('part')
  async def tube(name,a,b,diam,body=None,mode='create'):
   a=np.asarray(a);b=np.asarray(b);v=b-a;return await run.shape(c,name,a.tolist(),v.tolist(),diam,float(np.linalg.norm(v)),body,mode)
  async def sphere(name,point,diam,body=None,mode='create'):
   r=await c.call('nx_sphere '+' '.join(map(str,[name,*point,diam,mode,body or '-'])));await c.call('nx_save_part');return r['body_id']
  async def datum(part,name,point,axis):
   r=await c.call('nx_joint_reference '+' '.join(map(str,[name,*point,*axis])));refs.setdefault(part,{})[name]=r
  async def finish(name,color):
   await run.finish(c,name,color);run.save('joint-references.json',refs)
  await c.call('nx_create_part '+str(run.model/'ground.prt'));await c.call('nx_application modeling')
  body=await run.cylinder(c,'top_plate',[0,0],10,200,6)
  for i in range(3):
   A=np.array(initial['anchors'][i]);t=np.array(initial['tangent'][i])
   for sign in [-1,1]:
    x=A+sign*19*t;body=await run.shape(c,'bearing_post',(x+[0,0,-6]).tolist(),[0,0,1],12,16,body)
   body=await tube('bearing_pin',A-19*t,A+19*t,6,body)
   await datum('ground',f'BASE{i+1}',A,t)
  await finish('ground',103)
  for i in range(3):
   name=f'upper{i+1}';A=np.array(initial['anchors'][i]);B=np.array(initial['elbows'][i]);C=np.array(initial['ends'][i]);t=np.array(initial['tangent'][i]);rod=(C-B)/190
   await c.call('nx_create_part '+str(run.model/(name+'.prt')))
   body=await tube('root_boss',A-12*t,A+12*t,16);body=await tube('arm_web',A,B,8,body);body=await tube('fork_crossbar',B-18*t,B+18*t,6,body)
   for sign,side in [(-1,'L'),(1,'R')]:
    pt=B+sign*18*t;body=await sphere('bearing_shell_'+side,pt,15,body);body=await sphere('bearing_clearance_'+side,pt,10.6,body,'subtract');body=await tube('neck_port_'+side,pt,pt+15*rod,8.4,body,'subtract');await datum(name,'U'+side,pt,t)
   body=await tube('root_bore',A-12*t,A+12*t,6.4,body,'subtract');await datum(name,'BASE',A,t);await finish(name,186 if i%2==0 else 211)
  for i in range(3):
   B=np.array(initial['elbows'][i]);C=np.array(initial['ends'][i]);t=np.array(initial['tangent'][i]);n=np.cross(t,C-B);n/=np.linalg.norm(n)
   for sign,side in [(-1,'L'),(1,'R')]:
    name=f'rod{i+1}{side.lower()}';b=B+sign*18*t;end=C+sign*18*t
    await c.call('nx_create_part '+str(run.model/(name+'.prt')));body=await tube('rod',b,end,5);body=await sphere('upper_ball',b,10,body);body=await sphere('lower_ball',end,10,body);await datum(name,'U',b,n);await datum(name,'S',end,[0,0,1]);await finish(name,36 if side=='L' else 14)
  platform=np.array(initial['platform']);await c.call('nx_create_part '+str(run.model/'platform.prt'));body=await run.shape(c,'platform_plate',(platform+[0,0,-14]).tolist(),[0,0,1],60,6)
  for i in range(3):
   B=np.array(initial['elbows'][i]);C=np.array(initial['ends'][i]);t=np.array(initial['tangent'][i]);rod=(B-C)/190
   body=await tube('fork_support',platform+[0,0,-11],C,6,body);body=await tube('fork_crossbar',C-18*t,C+18*t,6,body)
   for sign,side in [(-1,'L'),(1,'R')]:
    pt=C+sign*18*t;body=await sphere('socket_shell',pt,15,body);body=await sphere('socket_clearance',pt,10.6,body,'subtract');body=await tube('socket_port',pt,pt+15*rod,8.4,body,'subtract');await datum('platform',f'S{i+1}{side}',pt,[0,0,1])
  await finish('platform',216)
  await run.add_all(c);await c.call('nx_constraint fix BaseFixed GROUND component -')
  actual={name:await c.call('nx_component_datums '+name.upper()) for name in run.parts};run.save('native-component-datums.json',actual)
  for name,rs in refs.items():
   points={x['name'] for x in actual[name]['prototype_points'] if x['occurrence_available']};curves={x['name'] for x in actual[name]['prototype_curves'] if x['occurrence_available']}
   for r in rs.values():assert r['point_name'] in points and r['curve_name'] in curves
  def ref(part,key,kind):return ('point:' if kind=='point' else 'curve:')+refs[part][key][kind+'_name']
  async def constrain(kind,name,a,keya,b,keyb,geometry='point',value=0):
   return await c.call(f'nx_constraint {kind} {name} {a.upper()} {ref(a,keya,geometry)} {b.upper()} {ref(b,keyb,geometry)} {value} same')
  for i in range(1,4):
   upper=f'upper{i}';await constrain('touch',f'Base{i}_center',upper,'BASE','ground',f'BASE{i}');await constrain('align',f'Base{i}_axis',upper,'BASE','ground',f'BASE{i}','curve')
   for side in ['L','R']:
    rod=f'rod{i}{side.lower()}';await constrain('touch',f'U{i}{side}_center',upper,'U'+side,rod,'U');await constrain('angle',f'U{i}{side}_crosspin',upper,'U'+side,rod,'U','curve',90);await constrain('touch',f'S{i}{side}_center',rod,'S','platform',f'S{i}{side}')
  state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);await c.call('nx_save_part');assert state['all_prototypes_fully_loaded'] and state['unsatisfied_constraint_count']==0
  inter=await c.call('nx_interference');run.save('static-interference.json',inter);await run.export(c,'assembly');await c.call('nx_fit_view');assert not any(x['nx_result']==1 for x in inter['pairs'])
  run.meta['build_passed']=True;run.pointer()


if __name__=='__main__':asyncio.run(main())
