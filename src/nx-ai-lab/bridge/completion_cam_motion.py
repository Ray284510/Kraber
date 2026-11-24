import asyncio,json,re,sys,csv
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from completion_contact_check import check
from mechanism_agent.completion_cam import pose,R,E

async def main(kind):
    pointer=LOG/f'current-completion_{kind}.json';run=json.loads(pointer.read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);report={};roller='roller' in kind;osc='oscillating' in kind;point=run['plan']['initial']['point'];allowed=[['CAM','ROLLER' if roller else 'FOLLOWER']]
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    async with Client() as c:
        if '--resume-tighter' in sys.argv:
            assert not (await c.call('nx_status')).get('part')
            await c.call('nx_open_part '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
            report=json.loads((out/'motion-results.json').read_text());report.pop('simulation_passed',None);report['frames']=[]
            report['precision_repair']=await c.call('nx_motion_tighten Cycle 1e-12');report['audit']=await c.call('nx_motion_audit');save()
        else:
            assert run['build_passed'] and (await c.call('nx_status'))['part']==run['assembly']
            await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup')
            groups={'Link0':['GROUND']+(['FOLLOWER_SHAFT'] if osc else []),'Cam':['CAM','CAMSHAFT'],'Follower':['FOLLOWER']}
            if roller:groups['Roller']=['ROLLER']
            for name,components in groups.items():await c.call(f'nx_motion_link {name} {int(name=="Link0")} '+' '.join(components))
            for j in run['plan']['joints']:await c.call('nx_motion_joint '+' '.join(map(str,[j['name'],j['type'],j['first'],j['second'],*j['origin'],*j['axis']])))
            cg=await c.call('nx_component_geometry CAM');report['cam_geometry']=cg
            def circle(g,diameter):
                rows=[e for e in g['edges'] if e['kind']=='Circular' and abs(e['bbox_mm'][3]-e['bbox_mm'][0]-diameter)<.001 and abs(e['bbox_mm'][2]-20)<.001]
                assert len(rows)==1;return rows[0]['reference']
            edge=circle(cg,2*R)
            if roller:
                rg=await c.call('nx_component_geometry ROLLER');report['roller_geometry']=rg
                report['contact']=await c.call(f'nx_motion_curve_pair ActualExternalContact CAM {edge} ROLLER {circle(rg,12)} 1')
            else:
                report['contact']=await c.call(f'nx_motion_point_curve ActualSharpContact Follower {point[0]} {point[1]} 20 CAM {edge}')
                report['curve_sampling']=await c.tool('nx_motion_refine_point_curve_sampling',{'name':'ActualSharpContact','spacing_mm':.1})
            report['higher_pairs']=await c.call('nx_motion_higher_pairs');report['free']=await c.call('nx_motion_solution Free articulation 4 240');await c.call('nx_motion_driver Input CamAxis revolute 90')
            for name,link,xyz in [('ECam','Cam',[E,0,24]),('EFollower','Follower',point+[24]),('EFollowerN','Follower',point+[34])]:await c.call('nx_motion_marker '+' '.join(map(str,[name,link,*xyz])))
            if roller:await c.call('nx_motion_marker '+' '.join(map(str,['ERoller','Roller',*point,24])))
            report['driven']=await c.call('nx_motion_solution Cycle normal 4 240');report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['integration_precision']=await c.call('nx_motion_integrator_refine Cycle 1e-9 0.001');report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save()
        report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        native=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[line.strip() for line in native.splitlines() if any(k in line.lower() for k in ['degrees of freedom','number of drivers','redundan','constraint violation'])];save()
        assert re.search(r'Number of Degrees of Freedom\s*=\s*0',native) and re.search(r'Number of Drivers\s*=\s*1',native) and report['audit']['solver_parameters']['solver']==1
        await c.call('nx_application motion');await c.call('nx_view iso 0.75');await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results E');save()
        data={r['marker']:np.array([a['values'] for a in r['coordinates']]).T for r in report['markers']['markers']};assert all(len(v)==241 for v in data.values())
        expected=[pose(kind,t) for t in np.linspace(0,4,241)];actual=data['EFollower'];target=np.c_[np.array([p['point'] for p in expected]),np.full(241,24.)]
        err=float(max(np.linalg.norm(actual-target,axis=1)));centertarget=np.c_[np.array([p['center'] for p in expected]),np.full(241,24.)]
        centererr=float(max(np.linalg.norm(data['ECam']-centertarget,axis=1)));gap=np.linalg.norm(actual-data['ECam'],axis=1)-(R+(6 if roller else 0));normalerr=float(max(np.linalg.norm(data['EFollowerN']-actual-[0,0,10],axis=1)))
        contact_normal=(actual[:,:2]-data['ECam'][:,:2]);contact_normal/=np.linalg.norm(contact_normal,axis=1)[:,None]
        if osc:
            arm=actual[:,:2]-run['plan']['parameters']['pivot_mm'];direction=np.c_[-arm[:,1],arm[:,0]];direction/=np.linalg.norm(direction,axis=1)[:,None]
        else:direction=np.tile([0.,1.],(241,1))
        native_pressure=np.degrees(np.arccos(np.clip(abs(np.sum(contact_normal*direction,axis=1)),0,1)));pressure_error=float(max(abs(native_pressure-np.array([p['pressure_deg'] for p in expected]))))
        report['cross_validation']={'max_follower_error_mm':err,'max_cam_center_error_mm':centererr,'max_abs_contact_gap_mm':float(max(abs(gap))),'minimum_signed_gap_mm':float(min(gap)),'max_normal_error_mm':normalerr,'native_max_pressure_angle_deg':float(max(native_pressure)),'native_pressure_comparison_error_deg':pressure_error,'source':'Native marker coordinates versus independent analytic circle/circle or circle/line closure; not imposed output motion'};save();assert pressure_error<.02
        assert err<.01 and centererr<.01 and max(abs(gap))<.001 and normalerr<.001
        with (out/'native-trajectory.csv').open('w',newline='') as f:
            w=csv.writer(f);w.writerow(['t','native_x','native_y','expected_x','expected_y','signed_gap']);w.writerows([i/60,*actual[i,:2],*target[i,:2],gap[i]] for i in range(241))
        report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        for frame in range(241):
            await c.call(f'nx_motion_frame Cycle {frame}');row=await check(c,frame,allowed);report['frames'].append(row);save()
            if not row['passed']:
                await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');raise RuntimeError('Unaccepted actual intersection/contact result; preserve before bounded repair')
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True,contact_validation_pending=False,allowed_contact_pairs=allowed);pointer.write_text(json.dumps(run,indent=2))
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
