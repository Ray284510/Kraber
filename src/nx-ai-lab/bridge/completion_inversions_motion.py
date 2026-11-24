import asyncio,csv,json,math,re,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from mechanism_agent.completion_inversions import pose
from mechanism_agent.calculations import constraint_rank

def link_frame(kind,link,p):
    A,C,u=[np.array(p[k],float) for k in ['A','C','u']]
    if kind=='fixed_block':
        return (A,A/80) if link=='Lever' else (C,(C-A)/45) if link=='Connecting' else (C,u)
    if link=='Input':return C,(C-A)/25
    return (np.array(p['B']),u) if link in ['Guide','Block'] else (C,u)

async def main(kind):
    family='completion_'+kind;pointer=LOG/f'current-{family}.json';r=json.loads(pointer.read_text(encoding='utf-8'));p=r['plan'];m=Path(r['models']);e=Path(r['logs']);report={}
    def save():(e/'motion-results.json').write_text(json.dumps(report,indent=2))
    async with Client() as c:
        assert r['build_passed'] and (await c.call('nx_status'))['part']==r['assembly'];await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
        for link in p['rigid_links']:
            components=[name for name,owner in r['component_rigid_owners'].items() if owner==link];assert components
            await c.call(f'nx_motion_link {link} {int(link=="Link0")} '+' '.join(components))
        for j in p['joints']:await c.call('nx_motion_joint '+' '.join(map(str,[j['name'],j['type'],j['first'],j['second'],*j['origin'],*j['axis']])))
        report['unactuated']=await c.call('nx_motion_solution Free articulation 4 240')
        if kind=='fixed_block':
            # NX12 reports DegreesPerSecond for harmonic frequency, not Hertz.
            report['driver']=await c.tool('nx_motion_driver_harmonic',{'name':'Actuator','joint':'LeverPivot','kind':'revolute','amplitude':10.,'frequency_hz':.25,'phase_deg':0.,'offset':0.})
            assert report['driver']['operation']['frequency_unit']=='DegreesPerSecond'
            report['frequency_conversion']={'requested_hz':.25,'native_degrees_per_second':90,'basis':'Actual NX driver-details unit query; validate native trajectory period too'}
        else:report['driver']=await c.call('nx_motion_driver Actuator InputPivot revolute 90')
        report['driver_details']=await c.call('nx_motion_driver_details')
        async def marker(name,link,point):await c.call('nx_motion_marker '+' '.join(map(str,[name,link,*point])))
        moving=[x for x in p['rigid_links'] if x!='Link0']
        for link in moving:
            xy,u=link_frame(kind,link,p['initial'])
            await marker('K'+link+'P',link,[*xy,30]);await marker('K'+link+'N',link,[*xy,40]);await marker('K'+link+'U',link,[*(xy+10*u),30])
        for i,j in enumerate(p['joints']):
            await marker(f'KJ{i}F',j['first'],j['origin']);await marker(f'KJ{i}S',j['second'],j['origin'])
        report['driven']=await c.call('nx_motion_solution Cycle normal 4 240');report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        native=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in native.splitlines() if any(k in line.lower() for k in ['degrees of freedom','number of drivers','constraint violation','redundan'])];save()
        assert re.search(r'Number of Degrees of Freedom\s*=\s*0',native) and re.search(r'Number of Drivers\s*=\s*1',native) and report['audit']['solver_parameters']['solver']==1
        blocks=native.split('The following are redundant constraints and will be deleted from the system:')[1:];removed=[re.findall(r'Type:\s+([^\r\n]+)',b.split('The remaining constraints')[0]) for b in blocks];report['redundancy_audit']={'native_counts':[len(x) for x in removed],'types':sorted({t.strip() for row in removed for t in row}),'expected':3};save();assert removed and all(len(row)==3 for row in removed)
        await c.call('nx_application motion');await asyncio.sleep(.5);await c.call('nx_view iso 0.75');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results K');save();data={a['marker']:np.array([x['values'] for x in a['coordinates']]).T for a in report['markers']['markers']};assert all(len(v)==241 for v in data.values())
        times=np.linspace(0,4,241);expected=[pose(kind,t) for t in times];position_errors={};axis_errors={};normal_errors={}
        for link in moving:
            actual=data['K'+link+'P'];directions=(data['K'+link+'U']-actual)/10;normals=data['K'+link+'N']-actual
            target=[link_frame(kind,link,v) for v in expected];position_errors[link]=float(max(np.linalg.norm(actual-np.c_[np.array([x[0] for x in target]),np.full(241,30)],axis=1)));axis_errors[link]=float(max(np.linalg.norm(directions-np.c_[np.array([x[1] for x in target]),np.zeros(241)],axis=1)));normal_errors[link]=float(max(np.linalg.norm(normals-[0,0,10],axis=1)))
        closure=[];ranks=[]
        for k in range(241):
            joints=json.loads(json.dumps(p['joints']));gaps=[]
            for i,j in enumerate(joints):
                first=data[f'KJ{i}F'][k];second=data[f'KJ{i}S'][k];j['origin']=first.tolist()
                if j['type']=='Slider':j['axis']=expected[k]['u']+[0];gap=np.linalg.norm(np.cross(first-second,j['axis']))
                else:gap=np.linalg.norm(first-second)
                gaps.append(float(gap))
            closure.append(max(gaps));ranks.append(constraint_rank(p['rigid_links'],joints,'Link0')['functional_dof'])
        output_link='Rod' if kind=='fixed_block' else 'Guide' if kind=='guide_bar' else 'Block'
        outp=data['K'+output_link+'P'];outu=(data['K'+output_link+'U']-outp)/10
        values=outp[:,0] if kind=='fixed_block' else np.degrees(np.unwrap(np.arctan2(outu[:,1],outu[:,0])));extrema=[]
        for i in range(1,240):
            if (values[i]-values[i-1])*(values[i+1]-values[i])<0:
                shift=(values[i-1]-values[i+1])/(2*(values[i-1]-2*values[i]+values[i+1]));extrema.append(float(times[i]+shift/60))
        assert len(extrema)==2;arcs=[extrema[1]-extrema[0],4-extrema[1]+extrema[0]];ratio=max(arcs)/min(arcs)
        input_link='Lever' if kind=='fixed_block' else 'Input';inaxis=data['K'+input_link+'U']-data['K'+input_link+'P'];input_angles=np.degrees(np.unwrap(np.arctan2(inaxis[:,1],inaxis[:,0])))
        checks={'position_error_mm':position_errors,'axis_vector_error':axis_errors,'normal_error_mm_per_10mm':normal_errors,'joint_closure_error_mm':max(closure),'functional_dof_each_frame':ranks,'input_angle_error_deg':float(max(abs(input_angles-np.array([x['input_deg'] for x in expected])))),'native_output_range':float(np.ptp(values)),'native_output_extreme_times_s':extrema,'native_quick_return_ratio':ratio,'independent_output_range':p['independent_calculation']['output_range'],'independent_quick_return_ratio':p['independent_calculation']['quick_return_ratio']}
        if kind=='fixed_block':
            connecting=data['KConnectingU']-data['KConnectingP'];checks['native_max_pressure_angle_deg']=float(max(abs(np.degrees(np.arctan2(connecting[:,1],connecting[:,0])))));assert checks['native_max_pressure_angle_deg']<45
        report['cross_validation']=checks;save();assert max(position_errors.values())<.01 and max(axis_errors.values())<.001 and max(normal_errors.values())<.001 and max(closure)<.01 and set(ranks)=={1} and checks['input_angle_error_deg']<.02
        assert abs(ratio-p['independent_calculation']['quick_return_ratio'])<.005 and abs(np.ptp(values)-p['independent_calculation']['output_range'])<.02
        with (e/'native-trajectory.csv').open('w',newline='') as file:
            writer=csv.writer(file);writer.writerow(['time_s','native_input_deg','native_output_mm_or_deg','expected_output_mm_or_deg'])
            for k,t in enumerate(times):writer.writerow([t,input_angles[k],values[k],expected[k]['slide_mm' if kind=='fixed_block' else 'output_deg']])
        report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        for frame in range(241):
            state=await c.call(f'nx_motion_frame Cycle {frame}');assert state['active_solution']=='Cycle';check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
            if check['interference_count']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual interference; preserve model before any bounded repair')
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();r.update(motion_passed=True,simulation=str(m/'mechanism.sim'),solution='Cycle');pointer.write_text(json.dumps(r,indent=2))
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
