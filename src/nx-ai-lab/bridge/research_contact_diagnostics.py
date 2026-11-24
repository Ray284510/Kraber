"""Read real failed NX contact definitions and independently classify retained solver evidence."""
import asyncio, hashlib, json, re
from pathlib import Path
import numpy as np
from shapely.geometry import Polygon, Point
from shapely.affinity import rotate
from phase4_client import Client, ROOT, LOG


async def main():
    info=json.loads((ROOT/'logs/contact_research/current.json').read_text());out=Path(info['logs']);report={'scope':'Historical native evidence plus fresh read-only definition queries. Unknown active contact Jacobian is not replaced with a guessed rank.','cases':{}}
    async with Client() as c:
        assert not (await c.call('nx_status'))['part']
        for family in ['finalization_geneva_analytic3','finalization_ratchet_analytic3']:
            run=json.loads((LOG/f'current-{family}.json').read_text());m=Path(run['models']);logs=Path(run['logs']);sim=m/'mechanism.sim';before=hashlib.sha256(sim.read_bytes()).hexdigest();motion=json.loads((logs/'motion-results.json').read_text());text=(m/'mechanism-cycle.minf').read_text(errors='replace')
            row={'source_models':str(m),'source_logs':str(logs),'source_sim_sha256':before,'nx_version':None,'native_solver_header':text.splitlines()[:18],'native_completion':motion.get('native_completion'),'requested_precision':motion['precision'],'driver_audit':motion['audit']['drivers'],'raw_solution_dofs':motion['audit']['solutions'],'active_contact_constraint_rank':None,'rank_limitation':'NX12 native contact Jacobian rank not exposed by the current verified bridge; lower-pair rank and solver DOFs reported separately.','warnings':{k:text.count(k) for k in ['Matrix decomposition failed','redundant constraints','Run aborted','not apply force','cannot be closed']}}
            report['cases'][family]=row
            await c.call('nx_open_part '+str(sim));row['nx_version']=(await c.call('nx_status'))['nx'];row['native_contact_definitions']=await c.call('nx_research_analytic_state',True);row['fresh_audit']=await c.call('nx_motion_audit',True);await c.call('nx_close_part 0');assert hashlib.sha256(sim.read_bytes()).hexdigest()==before
            if 'geneva' in family:
                row['cross_validation']=motion['cross_validation'];row['collision']=motion['actual_intersection'];row['independent_lower_pair_dof']=2;row['native_driven_dof']=1
                data={x['marker']:np.array([v['values'] for v in x['coordinates']]).T for x in motion['markers']['markers']};outline=np.loadtxt(m/'wheel.csv',delimiter=',');poly=Polygon(outline);angles=np.degrees(np.unwrap(np.arctan2(data['GOutput'][:,1],data['GOutput'][:,0]-60)));samples=[]
                for i in [0,79,80,100,119,120,121,160,240]:
                    pin=data['GInput'][i,:2];body=rotate(poly,float(angles[i]-30),origin=(60,0));point=Point(*pin);distance=body.boundary.distance(point);samples.append({'frame':i,'time_s':i/60,'pin_centre_mm':pin.tolist(),'native_wheel_angle_deg':float(angles[i]),'signed_pin_centre_boundary_distance_mm':-distance if body.contains(point) else distance,'circle_radius_mm':3,'diagnostic_radial_gap_mm':(-distance if body.contains(point) else distance)-3})
                row['independent_native_pose_gap_diagnosis']=samples
                row['classification']={'geometry_intersection':'confirmed: native positive-volume intersection at frame120','constraint_conflict':'not established; redundant lower-pair constraints alone do not prove conflicting constraints','contact_not_established':'not established; early trajectory follows nominal engagement','contact_lost_or_wrong_normal':'hypothesis near mid-index; force/normal series unavailable','nonconvergence':'native full duration completed with matrix warnings; unstable trajectory is separate failure','drive_direction':'input discrepancy only2.7344e-6deg; no evidence that changing input sign is root fix','out_of_design_range':'confirmed: output index-840.56deg vs60deg'}
            else:
                partial=json.loads((logs/'partial-contact-diagnosis.json').read_text());row['collision']=partial['frames'][-1];row['sample_count']=partial['actual_partial_sample_count'];row['independent_lower_pair_rank']=run['plan']['independent_calculation']['lower_pair_spatial_rank'];row['native_driven_dof']=motion['native_solver_dof_after_redundancy']
                row['classification']={'geometry_intersection':'confirmed: frame2 positive0.133929805677mm3','constraint_conflict':'not established; solver repeatedly removes planar redundant constraints','contact_not_established':'contact objects/export exist; valid sustained force transfer unproven','contact_lost_or_wrong_normal':'material side was reversed; penetration remains; no measured contact-normal/force history','nonconvergence':'full integration not achieved in339.491CPU seconds; stopped normally, not a claimed native convergence failure code','drive_direction':'singlepositive90deg/s input; complete transmissiondirection not established','out_of_design_range':'complete-cycle range unavailable: only4of481samples'}
            (out/'solver-diagnostics.json').write_text(json.dumps(report,indent=2))
    dest=Path(info['reports'])/'NX12_Contact_Solver_Diagnostics.md'
    lines=['# NX12 接触求解专项诊断','', '实际明细：'+str(out/'solver-diagnostics.json'),'','该诊断不执行新的完整机构仿真。历史SIM逐一读取原生接触定义后关闭，不保存；原SIM哈希保持一致。','', '接触法向的瞬态数值、完整活动接触矩阵秩和接触力历史尚未通过现有桥接读取，明确记为无法确认，不拿静态齿廓法向或下副矩阵替代。']
    for family,row in report['cases'].items():
        lines += ['', '## '+family, '', '原生版本：'+str(row['nx_version']), '', '完成状态：`'+json.dumps(row['native_completion'])+'`', '', '驱动后原生剩余DOF：'+str(row['native_driven_dof']), '', '分类判断：']
        lines += ['- '+k+': '+v for k,v in row['classification'].items()]
    lines += ['', 'MotionSolutionBuilder.AnalysisTypes.Dynamic 在本机NX12文档中表示 Dynamic or Kinematic。环境setter不等于原生积分算法改变；本轮Profile小试件原生仍使用DOPRI5。零剩余自由度的预定位置运行是理想运动学用途，不能宣称验证了动力学或接触力。','', '小试件Linear1d驱动的原生警告9803明确指出加速度为零以及BDF在节点重启的问题。后续先验证Akima插值；严禁通过放宽实体碰撞阈值避开插值误差。']
    dest.write_text('\n'.join(lines)+'\n',encoding='utf-8')


if __name__=='__main__':asyncio.run(main())
