"""Two-cycle native pawl-contact experiment; no wheel or pawl motion drivers."""
import asyncio,json,math,csv,re,sys,shutil
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from completion_contact_check import check as check_contact
from mechanism_agent.calculations import fourbar_pose
from completion_native_solve_gate import native_dynamics_completion
from mechanism_agent.round_bar_mass import coupler_mass

async def main(family='completion_ratchet',route='legacy'):
    assert route in ('legacy','analytic_sheet','analytic_sheet_reverse') and (family=='completion_ratchet' or family.startswith('finalization_ratchet_'))
    analytical=route.startswith('analytic_sheet')
    ptr=LOG/f'current-{family}.json';run=json.loads(ptr.read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);plan=run['plan'];p=plan['parameters'];pts=plan['initial_points'];report={}
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    async with Client() as c:
        assert run['build_passed'] and (await c.call('nx_status'))['part']==run['assembly'];await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup');report['analysis']=await c.call('nx_motion_analysis dynamics');save()
        groups={'Link0':'GROUND','Crank':'CRANK','Coupler':'COUPLER','Rocker':'ROCKER','Wheel':'WHEEL','DrivePawl':'DRIVE_PAWL','HoldPawl':'HOLD_PAWL'}
        for link,component in groups.items():await c.call(f'nx_motion_link {link} {int(link=="Link0")} {component}')
        for j in plan['joints']:await c.call(f'nx_motion_joint {j["name"]} Revolute {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
        report['contacts']=[];report['preloads']=[]
        if analytical:
            geometry=await c.call('nx_component_geometry WHEEL');report['actual_wheel_geometry']=geometry;faces=[f for f in geometry['faces'] if f['kind']=='Planar' and abs(f['bbox_mm'][2]-27.6)<1e-5 and abs(f['bbox_mm'][5]-27.6)<1e-5];assert len(faces)==1;reference='sheet:'+faces[0]['reference']
            report['contact_scope']='Native sphere-to-extrusion at actual rounded pawl tip centres/radii with exact native tooth profile sheet. Equatorial 2D contact proxy; not calibrated line-contact force/stress validation. All actual solid pairs remain checked.';save()
        for link,component,torque,key in [('DrivePawl','DRIVE_PAWL',p['drive_pawl_preload_N_mm'],'drive_tip'),('HoldPawl','HOLD_PAWL',p['hold_pawl_preload_N_mm'],'hold_tip')]:
            if analytical:
                xy=pts[key];side=' 1' if route=='analytic_sheet_reverse' else ' 0';contact=await c.call(f'nx_motion_analytic_sphere_extrusion {link}Contact {link} {xy[0]} {xy[1]} 24 1.5 Wheel WHEEL {reference} 0 1 0 0'+side,True)
            else:contact=await c.call(f'nx_motion_body_contact {link}Contact {component} WHEEL 1000 .1 .001',True)
            report['contacts'].append(contact);save()
            if not contact['ok']:
                await c.call('nx_save_part');raise RuntimeError('Native contact construction failed')
            report['preloads'].append(await c.call(f'nx_motion_joint_torque {link}Preload {link}Axis {torque}',True));save();assert report['preloads'][-1]['ok']
            if route=='legacy':report.setdefault('contact_force_models',[]).append(await c.call(f'nx_motion_contact_model {link}Contact '+p['contact_force_model']));save()
        report['unactuated']=await c.call('nx_motion_solution Free articulation 8 480');await c.call('nx_motion_driver InputDrive Input revolute 90');report['driven']=await c.call('nx_motion_solution Cycle normal 8 480');report['automatic_mass_before_repair']=await c.tool('nx_motion_mass_state',{});save()
        report['coupler_independent_mass']=[coupler_mass(order=n) for n in [256,512]]
        report['coupler_native_basis']=await c.call('nx_motion_precise_mass Coupler 0');save()
        hi=report['coupler_native_basis']['higher'];independent=report['coupler_independent_mass'][-1]
        assert abs(hi[2]-independent['mass_kg'])<1e-9
        assert max(abs(a-b) for a,b in zip(sorted(v*1e6 for v in hi[31:34]),sorted(independent['principal_inertia_kg_mm2'])))<1e-6
        center=[(pts['A'][i]+pts['C'][i])/2 for i in range(2)]+[31.4]
        assert max(abs(hi[3+i]*1000-center[i]) for i in range(3))<1e-5
        report['coupler_mass_repair']=await c.call('nx_motion_precise_mass Coupler 1');report['mass']=await c.tool('nx_motion_mass_state',{});save()
        parts=json.loads((out/'parts-verified.json').read_text())
        for link in report['mass']['links']:
            if link['fixed_link']:continue
            name=groups[link['name']].lower();volume=parts[name]['created']['bodies'][0]['volume_mm3'];expected=independent['mass_kg'] if name=='coupler' else volume*7800/1e9
            assert link['mass_unit']=='Kilogram' and abs(link['mass']-expected)<max(1e-7,expected*1e-5);assert min(link[k] for k in ['ixx','iyy','izz'])>0
        for name,link,point in [('RInput','Crank',pts['A']+[28.2]),('RRocker','Rocker',pts['C']+[28.2]),('RCouplerA','Coupler',pts['A']+[28.2]),('RCouplerC','Coupler',pts['C']+[28.2]),('RWheel','Wheel',[55,0,24]),('RDriveTip','DrivePawl',pts['drive_tip']+[24]),('RHoldTip','HoldPawl',pts['hold_tip']+[24])]:await c.call('nx_motion_marker '+name+' '+link+' '+' '.join(map(str,point)))
        report['precision']=await c.call('nx_motion_integrator_refine Cycle 1e-9 .0001');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();
        if analytical:
            report['native_input_export']=await c.call('nx_motion_export_input Cycle');capture=out/'analytical-native-input';capture.mkdir(exist_ok=False)
            for file in m.glob('mechanism-*'):
                if file.is_file():shutil.copy2(file,capture/file.name)
            save()
        report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        text=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in text.splitlines() if any(k in line.lower() for k in ['degrees of freedom','number of drivers','error','contact','redundant'])];save();assert re.search(r'Number of Drivers\s*=\s*1',text)
        native_dofs=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',text,re.M);report['native_solver_dof_after_redundancy']=int(native_dofs[-1]) if native_dofs else None;save();assert report['native_solver_dof_after_redundancy']==plan['independent_calculation']['lower_pair_spatial_rank']['functional_dof']-1
        report['native_completion']=native_dynamics_completion(text,plan['drive']['duration_s']);save()
        if not report['native_completion']['passed']:
            await c.call('nx_save_part');raise RuntimeError('Native solution did not complete the requested time span')
        report['contact_solver_valid']=not any(v in text.lower() for v in ['not apply force','cannot be closed','definepolygons: error']);save()
        if not report['contact_solver_valid']:
            await c.call('nx_save_part');raise RuntimeError('Native contact construction/force warning: trajectory cannot be accepted')
        await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso .75');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results R');save();data={v['marker']:np.array([a['values'] for a in v['coordinates']]).T for v in report['markers']['markers']};assert all(len(v)==481 for v in data.values());t=np.linspace(0,8,481);theta=np.radians(p['initial_input_deg']+90*t);poses=[fourbar_pose(p['ground_length_mm'],p['crank_mm'],p['coupler_mm'],p['rocker_mm'],v) for v in theta];expectedA=np.array([v['A'] for v in poses]);expectedC=np.array([v['C'] for v in poses]);a=data['RInput'];rocker=data['RRocker'];wheel=np.degrees(np.unwrap(np.arctan2(data['RWheel'][:,1],data['RWheel'][:,0]-40)))
        closure=max(float(max(np.linalg.norm(a-data['RCouplerA'],axis=1))),float(max(np.linalg.norm(rocker-data['RCouplerC'],axis=1))));checks={'crank_position_error_mm':float(max(np.linalg.norm(a[:,:2]-expectedA,axis=1))),'rocker_position_error_mm':float(max(np.linalg.norm(rocker[:,:2]-expectedC,axis=1))),'fourbar_closure_error_mm':closure,'first_cycle_index_deg':float(wheel[240]-wheel[0]),'second_cycle_index_deg':float(wheel[480]-wheel[240]),'expected_steady_index_deg':20.,'largest_sample_reverse_step_deg':float(max(0,-min(np.diff(wheel))))};report['cross_validation']={'checks':checks,'scope':'Independently calculated four-bar closure; wheel is measured from native contact dynamics and not prescribed. First-cycle contact settling remains visible.'};save()
        report['trajectory_passed']=max(checks[k] for k in ['crank_position_error_mm','rocker_position_error_mm','fourbar_closure_error_mm'])<.02 and abs(checks['second_cycle_index_deg']-20)<2 and checks['largest_sample_reverse_step_deg']<2;save()
        with (out/'native-ratchet-trajectory.csv').open('w',newline='') as f:
            w=csv.writer(f);w.writerow(['time_s','input_deg','wheel_deg','rocker_x','rocker_y']);w.writerows([t[i],math.degrees(theta[i]),wheel[i],*rocker[i,:2]] for i in range(481))
        allowed=[['WHEEL','DRIVE_PAWL'],['WHEEL','HOLD_PAWL']];run['allowed_contact_pairs']=allowed;ptr.write_text(json.dumps(run,indent=2));report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        for frame in range(481):
            await c.call(f'nx_motion_frame Cycle {frame}');result=await check_contact(c,frame,allowed);report['frames'].append(result);save()
            if not result['passed']:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Native ratchet penetration/unexpected collision retained; no positive-volume allowance')
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');assert report['trajectory_passed'];report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True);ptr.write_text(json.dumps(run,indent=2))

if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
