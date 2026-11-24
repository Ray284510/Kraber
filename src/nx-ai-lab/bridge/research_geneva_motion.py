"""Two-cycle IDEAL Geneva: native profile solve checked by separate geometric invariants."""
import asyncio,csv,json,math,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG
from completion_contact_check import check
from mechanism_agent.completion_geneva import wheel_angle


async def main(family):
    assert family.startswith('research_geneva_')
    pointer=LOG/f'current-{family}.json';run=json.loads(pointer.read_text());m=Path(run['models']);out=Path(run['logs']);p=run['plan']['parameters'];report={'scope':'Ideal time-synchronized geometric motion, not unilateral contact dynamics. Verification uses slot collinearity/tangency and cycle differences, not wheel_angle().'}
    def save():(out/'motion-results.json').write_text(json.dumps(report,indent=2))
    times=np.linspace(0,8,4001)
    with (m/'output_profile.csv').open('w',newline='') as f:
        csv.writer(f).writerows((t,math.degrees(wheel_angle(-math.pi+math.pi*t/2,6))-30) for t in times)
    async with Client() as c:
        assert run['build_passed'] and (await c.call('nx_status'))['part']==run['assembly']
        await c.call('nx_motion_new '+str(m/'mechanism.sim'));await c.call('nx_motion_setup');await c.call('nx_motion_analysis kinematics')
        for name,component in [('Link0','GROUND'),('InputLink','INPUT'),('OutputLink','OUTPUT')]:await c.call(f'nx_motion_link {name} {int(name=="Link0")} {component}')
        for j in run['plan']['joints']:await c.call(f'nx_motion_joint {j["name"]} Revolute {j["first"]} {j["second"]} '+' '.join(map(str,j['origin']+j['axis'])))
        report['free']=await c.call('nx_motion_solution Free articulation 8 480');await c.call('nx_motion_driver InputDrive Input revolute 90')
        report['ideal_output_profile']=await c.call(f'nx_research_profile_driver IdealGeometry Output {m/"output_profile.csv"} akima');report['driven']=await c.call('nx_motion_solution Cycle normal 8 480');report['precision']=await c.call('nx_motion_tighten Cycle 1e-9')
        await c.call('nx_motion_marker GInput InputLink -30 0 24');await c.call('nx_motion_marker GOutput OutputLink '+f'{60+15*math.cos(math.pi/6)} {15*math.sin(math.pi/6)} 24')
        report['audit']=await c.call('nx_motion_audit');await c.call('nx_save_part');save();report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        text=(m/'mechanism-cycle.minf').read_text(errors='replace');report['native_summary']=[s.strip() for s in text.splitlines() if any(k in s for k in ['Number of Degrees','Number of Drivers','Warning','Run aborted','Total elapsed'])];assert 'Run aborted' not in text
        await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results G');save()
        data={v['marker']:np.array([a['values'] for a in v['coordinates']]).T for v in report['markers']['markers']};assert all(len(v)==481 for v in data.values())
        pin=data['GInput'];wheel=data['GOutput'];qa=np.unwrap(np.arctan2(pin[:,1],pin[:,0]));qa-=2*math.pi if qa[0]>0 else 0;qb=np.unwrap(np.arctan2(wheel[:,1],wheel[:,0]-60));times=np.linspace(0,8,481)
        # Independent geometry: tangent entry requires |pin-centre angle|=acos(r/c).
        # Inside engagement the radial slot must be collinear with pin-output-centre.
        phase=(qa+math.pi)%(2*math.pi)-math.pi;engaged=np.abs(phase)<math.acos(30/60)-1e-6
        vec=pin[:,:2]-[60,0];az=np.arctan2(vec[:,1],vec[:,0]);delta=(az-qb+math.pi/6)%(math.pi/3)-math.pi/6
        cross=np.abs(np.sin(delta)*np.linalg.norm(vec,axis=1));dwells=[]
        for a,b in [(0,79),(161,319),(401,480)]:dwells.append(float(np.degrees(np.ptp(qb[a:b+1]))))
        indexes=[float(np.degrees(qb[0]-qb[240])),float(np.degrees(qb[240]-qb[480]))]
        cv={'input_error_deg':float(np.max(np.abs(np.degrees(qa)-(-180+90*times)))),'engaged_slot_collinearity_mm':float(max(cross[engaged])),'dwell_angle_ranges_deg':dwells,'cycle_indexes_deg':indexes,'total_index_deg':float(np.degrees(qb[0]-qb[-1])),'native_driven_gruebler':report['driven']['gruebler_dof'],'contact_force_verified':False}
        report['cross_validation']=cv;save();assert cv['input_error_deg']<.01 and cv['engaged_slot_collinearity_mm']<.01 and max(dwells)<.01 and max(abs(v-60) for v in indexes)<.01 and cv['native_driven_gruebler']==0
        with (out/'native-trajectory.csv').open('w',newline='') as f:
            w=csv.writer(f);w.writerow(['time_s','input_deg','output_deg','engaged','slot_collinearity_mm']);w.writerows(zip(times,np.degrees(qa),np.degrees(qb),engaged,cross))
        report['collision_setup']=await c.call('nx_motion_collision_setup');report['frames']=[]
        try:
            for i in range(481):
                await c.call(f'nx_motion_frame Cycle {i}');result=await check(c,i,[['INPUT','OUTPUT']]);report['frames'].append(result);save();assert result['passed'],'Actual positive penetration or unexpected contact'
        finally:await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part')
        report['simulation_passed']=True;save();run.update(simulation=str(m/'mechanism.sim'),solution='Cycle',motion_passed=True,analysis_type='ideal_kinematics',allowed_contact_pairs=[['INPUT','OUTPUT']],verified_scope=run['plan']['construction']);pointer.write_text(json.dumps(run,indent=2))


if __name__=='__main__':asyncio.run(main(sys.argv[1]))
