"""Verify real gear couplers against independent signed ratios and all native frames."""
import asyncio,json,re,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG

async def main(kind,stem='mechanism'):
    pointer=LOG/f'current-completion_{kind}.json';run=json.loads(pointer.read_text());plan=run['plan'];m=Path(run['models']);out=Path(run['logs']);report={}
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    async with Client() as c:
        assert run['build_passed'] and (await c.call('nx_status'))['part']==run['assembly']
        await c.call('nx_motion_new '+str(m/(stem+'.sim')));await c.call('nx_motion_setup');await c.call('nx_motion_link Link0 1 GROUND')
        for name,parts in plan['groups'].items():await c.call(f'nx_motion_link {name} 0 '+' '.join(parts))
        for j in plan['joints']:await c.call(f'nx_motion_joint {j["name"]} {j["type"]} {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
        report['couplers']=[]
        if kind=='rack_pinion':
            report['couplers'].append(await c.tool('nx_motion_rack_pinion',{'name':'RackMesh','revolute_joint':'PinionAxis','slider_joint':'RackAxis','radius_mm':15,'contact_mm':[0,0,28]}));save()
        else:
            for row in plan['couplers']:report['couplers'].append(await c.call('nx_motion_gear_pair '+' '.join(map(str,row))));save()
        report['unactuated']=await c.call('nx_motion_solution Free articulation 4 240');save();assert report['unactuated']['gruebler_dof']==1
        input_name='Input' if kind=='multistage_gears' else ('Ring' if kind=='internal_gears_direct' else 'Pinion')
        await c.call(f'nx_motion_driver InputDriver {input_name}Axis revolute {plan["expected_speed_deg_s"][input_name.lower()]}')
        for name,xy in plan['centers'].items():
            await c.call(f'nx_motion_marker G{name} {name.title()} {xy[0]+8} {xy[1]} 24')
        report['driven']=await c.call('nx_motion_solution Cycle normal 4 240');save();assert report['driven']['gruebler_dof']==0
        report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['integrator']=await c.tool('nx_motion_refine_integrator',{'solution':'Cycle','tolerance':1e-9,'maximum_step_s':.001})
        report['audit']=await c.call('nx_motion_audit');report['coupler_state']=await c.call('nx_motion_coupler_state');await c.call('nx_save_part');save()
        report['solve']=await c.call('nx_motion_solve Cycle');save()
        text=(m/(stem+'-cycle.minf')).read_text(errors='replace');report['native_summary']=[s.strip() for s in text.splitlines() if any(k in s.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation','error'])];save()
        dof=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',text,re.M);assert dof and int(dof[-1])==0 and re.search(r'Number of Drivers\s*=\s*1',text)
        await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso 0.8');await c.call('nx_motion_frame Cycle 0')
        report['markers']=await c.call('nx_motion_marker_results G');save();data={r['marker']:np.array([a['values'] for a in r['coordinates']]).T for r in report['markers']['markers']};t=np.linspace(0,4,241);checks={};trajectories={}
        for name,xy in plan['centers'].items():
            actual=data['G'+name];assert len(actual)==241
            if name=='rack':
                expected=np.array([8+15*np.pi/2*t,np.full(241,-18),np.full(241,24)]).T;checks['rack_position_error_mm']=float(np.max(np.linalg.norm(actual-expected,axis=1)));trajectories[name]=actual[:,0].tolist()
            else:
                angle=np.degrees(np.unwrap(np.arctan2(actual[:,1]-xy[1],actual[:,0]-xy[0])));checks[name+'_angle_error_deg']=float(max(abs(angle-plan['expected_speed_deg_s'][name]*t)));checks[name+'_radius_error_mm']=float(max(abs(np.linalg.norm(actual[:,:2]-np.array(xy),axis=1)-8)));trajectories[name]=angle.tolist()
        report['cross_validation']={'checks':checks,'native_trajectories':trajectories,'independent_ratios':plan['independent_calculation']['ratios']};save();assert all(v<(plan['tolerances']['angle_deg'] if k.endswith('_deg') else plan['tolerances']['marker_mm']) for k,v in checks.items()),checks
        report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        for frame in range(241):
            await c.call(f'nx_motion_frame Cycle {frame}');check=await c.call(f'nx_motion_collision_check {frame}');report['frames'].append(check);save()
            if check['interference_count']:
                report['collision_volume']=await c.call(f'nx_motion_collision_volumes {frame}');save();await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual gear interference; preserve fault before repair')
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update(simulation=str(m/(stem+'.sim')),solution='Cycle',motion_passed=True);pointer.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
