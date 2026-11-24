"""Native NX ideal ratchet with distinct wheel and pawl profiles. No contact-force claim."""
import asyncio,csv,json,math,sys,re
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from completion_contact_check import check
from mechanism_agent import research_ratchet_geometry as g

async def main(family):
    assert family.startswith('research_ratchet_')
    ptr=LOG/f'current-{family}.json';run=json.loads(ptr.read_text());m=Path(run['models']);out=Path(run['logs']);p=run['plan']['initial_points'];report={'scope':run['plan']['construction']}
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    rows=[g.state(g.INITIAL+90*t) for t in np.linspace(0,12,6001)]
    for name,fun in [('Wheel',lambda s:s['wheel_deg']),('DrivePawl',lambda s:math.degrees(s['psi']-g.PSI0-(s['phi']-g.PHI0))),('HoldPawl',lambda s:math.degrees(s['hpsi']-g.HPSI0))]:
        with (m/(name+'_profile.csv')).open('x',newline='') as f:csv.writer(f).writerows(zip(np.linspace(0,12,6001),[fun(s) for s in rows]))
    async with Client() as c:
        assert (await c.call('nx_status'))['part']==run['assembly'];await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup');await c.call('nx_motion_analysis kinematics')
        groups={'Link0':'GROUND','Crank':'CRANK','Coupler':'COUPLER','Rocker':'ROCKER','Wheel':'WHEEL','DrivePawl':'DRIVE_PAWL','HoldPawl':'HOLD_PAWL'}
        for link,component in groups.items():await c.call(f'nx_motion_link {link} {int(link=="Link0")} {component}')
        for j in run['plan']['joints']:await c.call(f'nx_motion_joint {j["name"]} Revolute {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
        report['free']=await c.call('nx_motion_solution Free articulation 12 720');await c.call('nx_motion_driver InputDrive Input revolute 90')
        report['profiles']=[]
        for name,joint in [('Wheel','WheelAxis'),('DrivePawl','DrivePawlAxis'),('HoldPawl','HoldPawlAxis')]:report['profiles'].append(await c.call(f'nx_research_profile_driver Ideal{name} {joint} {m/(name+"_profile.csv")} akima'))
        report['driven']=await c.call('nx_motion_solution Cycle normal 12 720');report['precision']=await c.call('nx_motion_tighten Cycle 1e-9');report['coupler_mass_precision']=await c.call('nx_motion_precise_mass Coupler 1')
        for name,link,point in [('RInput','Crank',p['A']+[28.2]),('RRocker','Rocker',p['C']+[28.2]),('RCouplerA','Coupler',p['A']+[28.2]),('RCouplerC','Coupler',p['C']+[28.2]),('RWheel','Wheel',[55,0,24]),('RDriveTip','DrivePawl',p['drive_tip']+[24]),('RHoldTip','HoldPawl',p['hold_tip']+[24])]:await c.call('nx_motion_marker '+name+' '+link+' '+' '.join(map(str,point)))
        report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        native=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[l.strip() for l in native.splitlines() if any(k in l for k in ['Number of Degrees','Number of Drivers','Warning','Run aborted','Total elapsed'])];save();assert 'Run aborted' not in native
        dofs=re.findall(r'^Number of Degrees of Freedom\s*=\s*(-?\d+)',native,re.M);assert dofs and int(dofs[-1])==0
        await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results R');save()
        data={v['marker']:np.array([a['values'] for a in v['coordinates']]).T for v in report['markers']['markers']};assert all(len(v)==721 for v in data.values())
        a=data['RInput'][:,:2];rocker=data['RRocker'][:,:2];wheel=np.degrees(np.unwrap(np.arctan2(data['RWheel'][:,1],data['RWheel'][:,0]-40)));phi=np.degrees(np.unwrap(np.arctan2(rocker[:,1],rocker[:,0]-40)));theta=np.degrees(np.unwrap(np.arctan2(a[:,1],a[:,0])));t=np.linspace(0,12,721)
        # Separate verification path: measured rigid-link lengths, native loop closure,
        # signed native rocker increments and tooth count, never g.state()/profile lookup.
        closure=max(np.max(np.linalg.norm(data['RInput']-data['RCouplerA'],axis=1)),np.max(np.linalg.norm(data['RRocker']-data['RCouplerC'],axis=1)))
        lengths=dict(crank=float(np.max(np.abs(np.linalg.norm(a,axis=1)-g.R))),rocker=float(np.max(np.abs(np.linalg.norm(rocker-[40,0],axis=1)-35))),coupler=float(np.max(np.abs(np.linalg.norm(rocker-a,axis=1)-40))))
        delta=np.diff(phi);advancing=delta>1e-5;returning=delta<-1e-5
        cv=dict(input_error_deg=float(np.max(np.abs(theta-(g.INITIAL+90*t)))),link_length_errors_mm=lengths,loop_closure_mm=float(closure),cycle_indexes_deg=[float(wheel[(k+1)*240]-wheel[k*240]) for k in range(3)],forward_wheel_rocker_increment_error_deg=float(np.max(np.abs(np.diff(wheel)[advancing]-delta[advancing]))),dwell_increment_error_deg=float(np.max(np.abs(np.diff(wheel)[returning]))),largest_reverse_increment_deg=float(max(0,-min(np.diff(wheel)))),physical_lower_pair_mobility=4,native_prescribed_mobility=int(dofs[-1]),force_validation=False)
        # Exclude at most the one sample interval straddling a rocker extremum from derivative matching.
        robust=advancing & (delta>.001);cv['robust_forward_increment_error_deg']=float(np.max(np.abs(np.diff(wheel)[robust]-delta[robust])))
        report['cross_validation']=cv;save();assert cv['input_error_deg']<.01 and max(lengths.values())<.001 and closure<.001 and max(abs(v-20) for v in cv['cycle_indexes_deg'])<.01 and cv['robust_forward_increment_error_deg']<.01 and cv['dwell_increment_error_deg']<.01 and cv['largest_reverse_increment_deg']<.01
        with (out/'native-trajectory.csv').open('w',newline='') as f:
            w=csv.writer(f);w.writerow(['time_s','input_deg','wheel_deg','rocker_deg','drive_tip_x','drive_tip_y','hold_tip_x','hold_tip_y']);w.writerows(zip(t,theta,wheel,phi,data['RDriveTip'][:,0],data['RDriveTip'][:,1],data['RHoldTip'][:,0],data['RHoldTip'][:,1]))
        report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[];allowed=[['WHEEL','DRIVE_PAWL'],['WHEEL','HOLD_PAWL']]
        try:
            for i in range(721):
                await c.call(f'nx_motion_frame Cycle {i}');v=await check(c,i,allowed);report['frames'].append(v);save();assert v['passed'],'Positive volume / unexpected contact in actual NX'
        finally:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part')
        report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True,analysis_type='ideal_kinematics',allowed_contact_pairs=allowed,verified_scope=run['plan']['construction']);ptr.write_text(json.dumps(run,indent=2))

if __name__=='__main__':asyncio.run(main(sys.argv[1]))
