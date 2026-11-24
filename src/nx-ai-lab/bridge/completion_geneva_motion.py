"""Native contact driven indexing attempt; positive penetration prevents acceptance."""
import asyncio,json,re,csv,math,sys,shutil,hashlib
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from mechanism_agent.completion_geneva import wheel_angle
from completion_contact_check import check as check_contact
from completion_native_solve_gate import native_dynamics_completion

async def main(family='completion_geneva',route='legacy',resume='new'):
    assert resume in ('new','resume')
    assert route in ('legacy','analytic','analytic_body','analytic_sheet') and (family=='completion_geneva' or family.startswith('finalization_geneva_'))
    ptr=LOG/f'current-{family}.json';run=json.loads(ptr.read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);plan=run['plan'];p=plan['parameters'];r=p['crank_radius_mm'];center=p['center_distance_mm'];report={}
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    async with Client() as c:
        assert run['build_passed']
        if resume=='resume':
            assert family=='finalization_geneva_analytic3' and route=='analytic_sheet'
            before=out/'motion-before-sheet-bridge-fix.json';assert not before.exists();shutil.copy2(out/'motion-results.json',before)
            status=await c.call('nx_status');assert not status.get('part');await c.call('nx_open_part '+str(m/'mechanism.sim'))
            report['resume_scope']='Same third mechanism design; third and final sheet bridge construction attempt after actual rollback. Original preparation report preserved.'
        else:
            assert (await c.call('nx_status'))['part']==run['assembly'];await c.call('nx_motion_new '+str(m/'mechanism.sim'))
        await c.call('nx_motion_setup');report['analysis']=await c.call('nx_motion_analysis dynamics');save()
        if resume=='new':
            for n,part in [('Link0','GROUND'),('InputLink','INPUT'),('OutputLink','OUTPUT')]:await c.call(f'nx_motion_link {n} {int(n=="Link0")} {part}')
            for j in plan['joints']:await c.call(f'nx_motion_joint {j["name"]} Revolute {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
        if route.startswith('analytic'):
            cap_angle=180-math.degrees(math.acos((p['locking_disk_radius_mm']**2+r*r-p['circular_relief_radius_mm']**2)/(2*p['locking_disk_radius_mm']*r)))
            report['contact_model_scope']={'route':'native sphere-to-extrusion','pin_proxy':'Sphere with same equatorial circle as physical cylindrical pin, centre in wheel midplane. Not a cylindrical line-contact force/stress validation.','locking_proxy':'Partial sphere uses physical locking circumference and circular-relief intersection angle; rim-only analytical idealization. Full physical-solid collision gate remains unchanged.','partial_cap_half_angle_deg':cap_angle,'material_assumption':'Steel E200GPa,nu0.3,rho7800kg/m3; experimental restitution0.1,friction0. No calibrated impact/stress claim.'};save()
            geometry=await c.call('nx_component_geometry OUTPUT');report['extrusion_geometry']=geometry
            faces=[f for f in geometry['faces'] if f['kind']=='Planar' and abs(f['bbox_mm'][2]-27.6)<1e-5 and abs(f['bbox_mm'][5]-27.6)<1e-5];assert len(faces)==1
            reference='body' if route=='analytic_body' else ('sheet:' if route=='analytic_sheet' else '')+faces[0]['reference'];report['analytical_contacts']=[]
            for name,x,radius,angle in [('PinContact',-r,p['pin_radius_mm'],0),('LockContact',0,p['locking_disk_radius_mm'],cap_angle)]:
                contact=await c.call(f'nx_motion_analytic_sphere_extrusion {name} InputLink {x} 0 24 {radius} OutputLink OUTPUT {reference} {angle} 1 0 0',True)
                report['analytical_contacts'].append(contact);save()
                if not contact['ok']:
                    await c.call('nx_save_part');raise RuntimeError('Native analytical contact creation rejected; actual error retained')
                assert abs(contact['young_modulus']['value_in_base_units']-2e8)<1
            report['contact_force_model']='NX12 AnalyticalContact material-based experimental model';save()
        else:
            # Under the actual parallel Z revolutes, only these overlapping lateral
            # faces can meet. Every solid still enters the separate collision gate.
            assert all(j['axis']==[0,0,1] for j in plan['joints'])
            assert center-(r+5)-3>0  # Grounded output shaft cannot reach the input envelope.
            selected={};coverage={}
            for name in ['INPUT','OUTPUT']:
                geometry=await c.call('nx_component_geometry '+name);faces=[];excluded=[]
                for face in geometry['faces']:
                    box=face['bbox_mm'];lateral=box[5]-box[2]>1e-6 and box[5]>20.4+1e-8 and box[2]<27.6-1e-8
                    if lateral:
                        assert (face['kind']=='Cylindrical' and abs(face['direction'][2])>.999999) or (face['kind']=='Planar' and abs(face['direction'][2])<1e-8)
                        faces.append(face)
                    else:excluded.append(face)
                assert faces;selected[name]=','.join(f['reference'] for f in faces);coverage[name]={'included':faces,'excluded':excluded}
            report['contact_geometry_coverage']={'actual_faces':coverage,'z_overlap_mm':[20.4,27.6],'constraint_basis':'Both actual revolute axes are Z, no axial freedom; output shaft outside input radial envelope; full-solid collision checks remain unchanged','full_body_collision_gate_retained':True};save()
            report['contact']=await c.call(f'nx_motion_body_face_contact IndexContact INPUT {selected["INPUT"]} OUTPUT {selected["OUTPUT"]} 1000 .1 .001',True);save();assert report['contact']['ok']
            report['contact_force_model']=await c.call('nx_motion_contact_model IndexContact '+p['contact_force_model']);save()
        report['unactuated']=await c.call('nx_motion_solution Free articulation 4 240');await c.call('nx_motion_driver InputDrive Input revolute 90');report['driven']=await c.call('nx_motion_solution Cycle normal 4 240');report['mass']=await c.tool('nx_motion_mass_state',{});save()
        verified=json.loads((out/'parts-verified.json').read_text())
        for link in report['mass']['links']:
            if link['fixed_link']:continue
            part='input' if link['name']=='InputLink' else 'output';volume=verified[part]['created']['bodies'][0]['volume_mm3'];expected=volume*7800/1e9
            assert link['mass_unit']=='Kilogram' and abs(link['mass']-expected)<max(1e-7,expected*1e-5);assert min(link[k] for k in ['ixx','iyy','izz'])>0
        await c.call(f'nx_motion_marker GInput InputLink {-r} 0 24');a=math.pi/p['slots'];await c.call(f'nx_motion_marker GOutput OutputLink {center+15*math.cos(a)} {15*math.sin(a)} 24');report['precision']=await c.call('nx_motion_integrator_refine Cycle 1e-9 .0001');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();
        if route.startswith('analytic'):
            report['native_input_export']=await c.call('nx_motion_export_input Cycle');capture=out/'analytical-native-input';capture.mkdir(exist_ok=False)
            for file in m.glob('mechanism-*'):
                if file.is_file():shutil.copy2(file,capture/file.name)
            save()
        report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        text=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[s.strip() for s in text.splitlines() if any(k in s.lower() for k in ['degrees of freedom','number of drivers','error','contact'])];save()
        report['native_completion']=native_dynamics_completion(text,plan['drive']['duration_s']);save()
        if not report['native_completion']['passed']:
            await c.call('nx_save_part');raise RuntimeError('Native solution did not complete the requested time span')
        report['contact_solver_valid']=not any(v in text.lower() for v in ['not apply force','cannot be closed','definepolygons: error']);save()
        if not report['contact_solver_valid']:
            await c.call('nx_save_part');raise RuntimeError('Native contact construction/force warning: trajectory cannot be accepted')
        await c.call('nx_application motion');await asyncio.sleep(1);await c.call('nx_view iso .75');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results G');save();data={v['marker']:np.array([a['values'] for a in v['coordinates']]).T for v in report['markers']['markers']};t=np.linspace(0,4,241);inp=np.unwrap(np.arctan2(data['GInput'][:,1],data['GInput'][:,0]));inp-=2*math.pi if inp[0]>0 else 0;out_angle=np.unwrap(np.arctan2(data['GOutput'][:,1],data['GOutput'][:,0]-center));expected=np.array([wheel_angle(v,p['slots']) for v in np.radians(-180+90*t)]);checks={'input_angle_error_deg':float(max(abs(np.degrees(inp)-(-180+90*t)))),'output_angle_error_deg':float(max(abs(np.degrees(out_angle-expected)))),'native_index_angle_deg':float(np.degrees(out_angle[0]-out_angle[-1])),'expected_index_angle_deg':360/p['slots']};report['cross_validation']=checks;save()
        with (out/'native-indexing-trajectory.csv').open('w',newline='') as f:
            w=csv.writer(f);w.writerow(['time_s','input_deg','native_output_deg','independent_output_deg']);w.writerows(zip(t,np.degrees(inp),np.degrees(out_angle),np.degrees(expected)))
        # Finish the trajectory comparison before evaluating strict geometry; no hidden tolerance change.
        report['trajectory_passed']=checks['input_angle_error_deg']<.02 and checks['output_angle_error_deg']<plan['tolerances']['output_angle_deg'];save()
        allowed=[['INPUT','OUTPUT']];run['allowed_contact_pairs']=allowed;ptr.write_text(json.dumps(run,indent=2));report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        for frame in range(241):
            await c.call(f'nx_motion_frame Cycle {frame}');check=await check_contact(c,frame,allowed);report['frames'].append(check);save()
            if not check['passed']:
                report['actual_intersection']=await c.call(f'nx_motion_collision_volumes {frame}');save();await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Actual contact interference hit retained; positive penalty penetration cannot be accepted as a clear mechanism')
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');assert report['trajectory_passed'];report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True);ptr.write_text(json.dumps(run,indent=2))

if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
