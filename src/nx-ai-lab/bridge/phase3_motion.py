import asyncio,json,math,csv
from pathlib import Path
import numpy as np
from phase3_client import Client,ROOT,LOG
def rot(axis,deg):
 a=np.array(axis,float);a/=np.linalg.norm(a);x,y,z=a;K=np.array([[0,-z,y],[z,0,-x],[-y,x,0]]);t=np.deg2rad(deg);return np.eye(3)+np.sin(t)*K+(1-np.cos(t))*(K@K)
def fk(plan,degrees):
 T=np.eye(4);transforms=[T.copy()]
 for p,a,q in zip(plan['joint_centers_mm'],plan['joint_axes'],degrees):
  R=rot(a,q);A=np.eye(4);A[:3,:3]=R;A[:3,3]=np.array(p)-R@p;T=T@A;transforms.append(T.copy())
 return transforms
def actual_angle(parent,child,axis):
 R=parent[:3,:3].T@child[:3,:3];v=np.array([R[2,1]-R[1,2],R[0,2]-R[2,0],R[1,0]-R[0,1]])/2
 return float(np.rad2deg(np.arctan2(np.dot(v,axis),(np.trace(R)-1)/2)))
async def main():
 run=json.loads((LOG/'current-robot.json').read_text());model=Path(run['models']);out=Path(run['logs']);p=run['plan'];report={};sim=model/'robot_motion.sim'
 async with Client() as c:
  status=await c.call('nx_status');assert status['part']==run['assembly']
  report['license']=await c.call('nx_motion_available')
  await c.call('nx_motion_new '+str(sim));await c.call('nx_motion_setup')
  for i in range(7):
   names=[f'LINK{i}']
   if i>0:names+=[f'BUSHING{i}']
   if i<6:names+=[f'PIN{i+1}',f'COLLAR{i+1}']
   await c.call(f'nx_motion_link Link{i} {int(i==0)} '+' '.join(names))
  for i,(pos,axis,limits) in enumerate(zip(p['joint_centers_mm'],p['joint_axes'],p['joint_limits_deg']),1):await c.call(f'nx_motion_joint Joint{i} Revolute Link{i} Link{i-1} '+' '.join(map(str,pos+axis+limits)))
  report['unactuated']=await c.call(f'nx_motion_solution FreeMotion articulation {p["duration_s"]} {p["steps"]}');assert report['unactuated']['gruebler_dof']==6
  for i,v in enumerate(p['demo_velocity_deg_s'],1):
   lo,hi=p['joint_limits_deg'][i-1];assert lo<=min(0,v*p['duration_s'])<=max(0,v*p['duration_s'])<=hi
   d=await c.call(f'nx_motion_driver Drive{i} Joint{i} revolute {v}');assert d['velocity_unit']=='DegreesPerSecond'
  for name,point in [('EE0',[270,0,240]),('EEX',[280,0,240]),('EEY',[270,10,240]),('EEZ',[270,0,250])]:await c.call(f'nx_motion_marker {name} Link6 '+' '.join(map(str,point)))
  report['driven']=await c.call(f'nx_motion_solution SixAxis normal {p["duration_s"]} {p["steps"]}');assert report['driven']['gruebler_dof']==0
  report['audit']=await c.call('nx_motion_audit');assert report['audit']['solver_parameters']['solver']==1
  await c.call('nx_save_part');report['solve']=await c.call('nx_motion_solve SixAxis')
  await c.call('nx_application motion');await asyncio.sleep(.6);await c.call('nx_view iso 0.8');await asyncio.sleep(.3)
  await c.call('nx_motion_frame SixAxis 0');report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
  for step in range(p['steps']+1):
   f=await c.call(f'nx_motion_frame SixAxis {step}');tr=await c.call(f'nx_motion_transforms {step}');coll=await c.call(f'nx_motion_collision_check {step}')
   report['frames'].append({'frame':f,'transforms':tr,'collision':coll});(out/'motion-results.json').write_text(json.dumps(report,indent=2))
   if coll['interference_count']:
    await c.call('nx_motion_frame SixAxis finish');await c.call('nx_save_part');raise RuntimeError('Actual motion collision: '+json.dumps(coll))
  from phase3_results import write_results
  report['markers']=await c.call('nx_motion_marker_results EE');report['joints']=await c.call('nx_motion_joint_results')
  write_results(p,report['markers'],report['joints'],out)
  (out/'accepted-motion-results.json').write_text(json.dumps(report,indent=2))
  await c.call('nx_motion_frame SixAxis finish');await c.call('nx_save_part')
  run['simulation']=str(sim);run['solution']='SixAxis';(LOG/'current-robot.json').write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main())
