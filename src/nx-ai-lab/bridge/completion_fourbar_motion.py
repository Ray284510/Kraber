import asyncio,csv,json,math,re,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from mechanism_agent.completion_fourbar import pose,measure
from mechanism_agent.calculations import constraint_rank

async def main(kind,resume=False):
    family='completion_'+kind;pointer=LOG/f'current-{family}.json';run=json.loads(pointer.read_text(encoding='utf-8'));p=run['plan'];m=Path(run['models']);out=Path(run['logs']);report={}
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    async with Client() as c:
        assert run['build_passed'] and (await c.call('nx_status'))['part']==(str(m/'mechanism.sim') if resume else run['assembly'])
        if not resume:
            await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
            for name,fixed,components in [('Link0',1,['GROUND','PIN_O','PIN_D']),('Input',0,['INPUT','PIN_A','COLLAR_A']),('Coupler',0,['COUPLER']),('Output',0,['OUTPUT','PIN_C','COLLAR_C'])]:await c.call(f'nx_motion_link {name} {fixed} '+' '.join(components))
            for j in p['joints']:await c.call('nx_motion_joint '+' '.join(map(str,[j['name'],j['type'],j['first'],j['second'],*j['origin'],*j['axis']])))
            report['unactuated']=await c.call('nx_motion_solution Free articulation 4 240');await c.call(f'nx_motion_driver Actuator {p["drive"]["joint"]} revolute 90')
        else:
            report=json.loads((out/'motion-results.json').read_text());await c.call('nx_motion_setup')
        A=p['initial_points']['A'];C=p['initial_points']['C']
        marker_rows=[('QA','Input',A,27.6),('QB','Coupler',A,27.6),('QC','Coupler',C,40),('QD','Output',C,40)] if not resume else []
        marker_rows += [('QAZ','Input',A,37.6),('QBZ','Coupler',A,37.6),('QDZ','Output',C,50)]
        for name,link,xy,z in marker_rows:await c.call('nx_motion_marker '+' '.join(map(str,[name,link,*xy,z])))
        if not resume:report['driven']=await c.call('nx_motion_solution Cycle normal 4 240')
        report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save()
        report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        minf=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[s.strip() for s in minf.splitlines() if any(k in s.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save()
        assert re.search(r'Number of Degrees of Freedom\s*=\s*0',minf) and re.search(r'Number of Drivers\s*=\s*1',minf) and report['audit']['solver_parameters']['solver']==1
        blocks=minf.split('The following are redundant constraints and will be deleted from the system:')[1:];removed=[re.findall(r'Type:\s+([^\r\n]+)',b.split('The remaining constraints')[0]) for b in blocks]
        report['redundancy_audit']={'blocks':len(removed),'removed_counts':list(map(len,removed)),'native_types':sorted({t.strip() for rows in removed for t in rows}),'expected_redundancy_from_independent_full_rank':p['independent_calculation']['rank']['redundant_rows'],'explanation':'Native elimination basis can mix dependent translation and rotation rows. Verify rank, full trajectory, joint closures and each actual body normal, not a prescribed equation-type list.'};save();assert removed and all(len(rows)==p['independent_calculation']['rank']['redundant_rows'] for rows in removed)
        await c.call('nx_application motion');await asyncio.sleep(.5);await c.call('nx_view iso 0.7');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results Q');save()
        data={r['marker']:np.array([v['values'] for v in r['coordinates']]).T for r in report['markers']['markers']};assert all(len(x)==241 for x in data.values())
        angles=p['parameters']['initial_driver_deg']+np.linspace(0,360,241);theory=[pose(kind,q) for q in angles];actual=[];ranks=[]
        for i in range(241):
            xy={'O':[0,0],'D':p['initial_points']['D'],'A':data['QA'][i,:2].tolist(),'C':data['QC'][i,:2].tolist()};actual.append(measure(xy));joints=json.loads(json.dumps(p['joints']));joints[1]['origin']=data['QA'][i].tolist();joints[2]['origin']=data['QC'][i].tolist();ranks.append(constraint_rank(p['rigid_links'],joints,'Link0')['functional_dof'])
        inp=np.unwrap([r['input_angle_rad'] for r in actual]);coupler=np.unwrap([r['coupler_angle_rad'] for r in actual]);output=np.unwrap([r['output_angle_rad'] for r in actual]);drive=inp if kind=='double_crank' else coupler-inp;drive_deg=np.degrees(drive-drive[0])+angles[0]
        extremes=[]
        for i in range(1,240):
            if (output[i]-output[i-1])*(output[i+1]-output[i])<0:
                shift=(output[i-1]-output[i+1])/(2*(output[i-1]-2*output[i]+output[i+1]));extremes.append(float((angles[i]+shift*1.5)%360))
        extremes.sort();arcs=[extremes[1]-extremes[0],360-extremes[1]+extremes[0]] if len(extremes)==2 else []
        errors={name:float(max(np.linalg.norm(data[marker][:,:2]-np.array([x[name] for x in theory]),axis=1))) for name,marker in [('A','QA'),('C','QC')]}
        closure=max(float(max(np.linalg.norm(data['QA']-data['QB'],axis=1))),float(max(np.linalg.norm(data['QC']-data['QD'],axis=1))))
        normals=max(float(max(np.linalg.norm(data[z]-data[a]-np.array([0,0,10]),axis=1))) for a,z in [('QA','QAZ'),('QB','QBZ'),('QD','QDZ')]);report['body_normal_error_mm_over_10mm']=normals;assert normals<.001
        checks={'max_marker_error_mm':max(errors.values()),'marker_errors_mm':errors,'joint_closure_error_mm':closure,'driver_angle_error_deg':float(max(abs(drive_deg-angles))),'input_winding_deg':float(np.degrees(inp[-1]-inp[0])),'output_winding_deg':float(np.degrees(output[-1]-output[0])),'input_range_deg':float(np.degrees(np.ptp(inp))),'output_range_deg':float(np.degrees(np.ptp(output))),'native_output_stationary_driver_angles_deg':extremes,'native_quick_return_ratio':max(arcs)/min(arcs) if arcs else None,'min_geometric_transmission_angle_deg':min(x['geometric_transmission_angle_deg'] for x in actual),'functional_dof_each_frame':ranks}
        report['cross_validation']=checks;save();assert checks['max_marker_error_mm']<.01 and closure<.01 and checks['driver_angle_error_deg']<.01 and set(ranks)=={1}
        if kind=='double_crank':assert abs(checks['input_winding_deg']-360)<.01 and abs(checks['output_winding_deg']-360)<.01 and not extremes
        else:
            assert abs(checks['input_winding_deg'])<.01 and abs(checks['output_winding_deg'])<.01 and len(extremes)==2 and abs(checks['native_quick_return_ratio']-1)<.005
            assert abs(checks['output_range_deg']-p['independent_calculation']['output_range_deg'])<.01
        with (out/'native-trajectory.csv').open('w',newline='') as f:
            w=csv.writer(f);w.writerow(['time_s','driven_relative_deg','ground_input_deg','ground_output_deg','A_x','A_y','C_x','C_y'])
            for i,t in enumerate(np.linspace(0,4,241)):w.writerow([t,drive_deg[i],math.degrees(inp[i]),math.degrees(output[i]),*data['QA'][i,:2],*data['QC'][i,:2]])
        report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        for frame in range(241):
            state=await c.call(f'nx_motion_frame Cycle {frame}');assert state['active_solution']=='Cycle';check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
            if check['interference_count']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual collision; retain state and use bounded repair')
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update(motion_passed=True,simulation=str(m/'mechanism.sim'),solution='Cycle');pointer.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main(sys.argv[1],'--resume-audit' in sys.argv))
