"""Analyze real NX Marker/joint output. FK predictions remain explicitly separate."""
import json,csv
import numpy as np
from phase3_motion import fk
def write_results(plan,markers,joints,out):
 assert markers['ok'] and joints['ok']
 m={v['marker']:np.array([c['values'] for c in v['coordinates']]).T for v in markers['markers']}
 js={v['joint']:v['angles'] for v in joints['joints']};rows=[];poserrs=[];roterrs=[];angleerrs=[]
 for i in range(plan['steps']+1):
  t=i*plan['duration_s']/plan['steps'];q=np.array(plan['demo_velocity_deg_s'])*t;pred=fk(plan,q)[-1]
  position=m['EE0'][i];R=np.column_stack([(m[name][i]-position)/10 for name in ['EEX','EEY','EEZ']]);angles=[]
  for j in range(1,7):
   parts=js[f'Joint{j}'];assert abs(parts[1]['radians'][i])<1e-6
   a=float(np.rad2deg(parts[0]['radians'][i]+parts[2]['radians'][i]));angles.append(a);lo,hi=plan['joint_limits_deg'][j-1];assert lo-1e-3<=a<=hi+1e-3
  pp=(pred@np.array([*plan['tool_center_mm'],1]))[:3];pe=float(np.linalg.norm(position-pp));re=float(np.linalg.norm(R-pred[:3,:3]));ae=float(np.max(np.abs(np.array(angles)-q)))
  poserrs.append(pe);roterrs.append(re);angleerrs.append(ae)
  rows.append({'frame':i,'time_s':t,'position_mm_native':position.tolist(),'orientation_matrix_native':R.tolist(),'joint_degrees_native':angles,'joint_degrees_commanded':q.tolist(),'position_mm_fk_prediction':pp.tolist(),'position_error_mm':pe,'rotation_matrix_error':re})
 result={'source':'Native NX UF_MOTION marker XYZ and joint rotation results; four markers define EE frame','max_position_error_mm':max(poserrs),'max_rotation_matrix_error':max(roterrs),'max_joint_angle_error_deg':max(angleerrs),'thresholds':{'position_mm':.01,'rotation_matrix_norm':1e-5,'joint_angle_degrees':.001},'frames':rows}
 (out/'end-effector-trajectory.json').write_text(json.dumps(result,indent=2))
 assert max(poserrs)<.01 and max(roterrs)<1e-5 and max(angleerrs)<.001,result
 with (out/'end-effector-trajectory.csv').open('w',newline='') as f:
  w=csv.writer(f);w.writerow(['frame','time_s','X_mm','Y_mm','Z_mm',*[f'R{i}{j}' for i in range(3) for j in range(3)],*[f'joint{i}_degrees' for i in range(1,7)]])
  for r in rows:w.writerow([r['frame'],r['time_s'],*r['position_mm_native'],*np.array(r['orientation_matrix_native']).ravel(),*r['joint_degrees_native']])
 rng=np.random.default_rng(120029);limits=np.array(plan['joint_limits_deg']);qs=rng.uniform(limits[:,0],limits[:,1],size=(3000,6));pts=np.array([(fk(plan,q)[-1]@np.array([*plan['tool_center_mm'],1]))[:3] for q in qs])
 np.savetxt(out/'workspace-analytical-samples.csv',np.column_stack([qs,pts]),delimiter=',',header='q1,q2,q3,q4,q5,q6,x,y,z',comments='')
 ee=np.array(plan['tool_center_mm']);J=np.column_stack([np.r_[np.cross(a,ee-np.array(o)),a] for a,o in zip(np.array(plan['joint_axes']),plan['joint_centers_mm'])]);rank=int(np.linalg.matrix_rank(J));assert rank==6
 workspace={'analytical_sample_count':len(pts),'analytical_bbox_mm':[pts.min(0).tolist(),pts.max(0).tolist()],'native_verified_trajectory_samples':len(rows),'native_trajectory_bbox_mm':[m['EE0'].min(0).tolist(),m['EE0'].max(0).tolist()],'initial_spatial_jacobian_rank':rank,'limitations':'Analytical workspace samples respect bounds but are not individually NX-solved or collision checked. Native trajectory is separately measured and checked at 0.05 s; no continuous collision-free workspace certification.'}
 (out/'workspace-analysis.json').write_text(json.dumps(workspace,indent=2));return result
