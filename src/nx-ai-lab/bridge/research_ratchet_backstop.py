"""Bounded actual-solid virtual reverse probe, NOT a loaded stop/contact solver test."""
import asyncio,json,math,csv,shutil,sys
import numpy as np
from pathlib import Path
from phase4_common import Client,LOG

async def main(family):
    r=json.loads((LOG/f'current-{family}.json').read_text());assert r['motion_passed'];source=Path(r['models']);m=source/'reverse_guard';m.mkdir();out=Path(r['logs']);report={'scope':'Virtual geometric obstruction with the holding pawl clamped in its measured dwell seat. Deliberately prescribed reverse motion creates positive intersection to establish a stop surface; not a successful unilateral reaction-force solve. Main accepted trajectory must have zero penetration.'}
    def save():(out/'native-backstop-probe.json').write_text(json.dumps(report,indent=2))
    for name in ['ground','wheel','hold_pawl']:shutil.copy2(source/(name+'.prt'),m/(name+'.prt'))
    # Linear diagnostic reversal has no velocity/acceleration acceptance claim.
    with (m/'reverse_profile.csv').open('x',newline='') as f:csv.writer(f).writerows([[0,0],[.2,-.2],[.4,0],[.6,.2],[.61,.21]])
    async with Client() as c:
        status=await c.call('nx_status');assert status['part'] in [r['simulation'],None,'']
        if not status['part']:await c.call('nx_open_part '+r['simulation']);await c.call('nx_motion_setup')
        if 'hold_face_deg' in r['plan']['parameters']:
            from research_ratchet_normals import query_normals
            report['native_geometry_moment_signs']=await query_normals(c,r)
            save();assert report['native_geometry_moment_signs']['passed']
        await c.call('nx_save_part');await c.call('nx_close_part 0')
        await c.call('nx_create_part '+str(m/'guard_assembly.prt'))
        for name in ['ground','wheel','hold_pawl']:await c.call(f'nx_add_component {m/(name+".prt")} {name.upper()} 0 0 0')
        report['initial']=await c.call('nx_interference');assert all(p['nx_result']==2 for p in report['initial']['pairs']);await c.call('nx_save_part');await c.call('nx_motion_new '+str(m/'backstop_probe.sim'));await c.call('nx_motion_setup')
        await c.call('nx_motion_link Link0 1 GROUND');await c.call('nx_motion_link HeldPawl 1 HOLD_PAWL');await c.call('nx_motion_link Wheel 0 WHEEL');await c.call('nx_motion_joint WheelAxis Revolute Wheel Link0 40 0 20.4 0 0 1');await c.call(f'nx_research_profile_driver ReverseProbe WheelAxis {m/"reverse_profile.csv"} linear');await c.call('nx_motion_solution Probe normal .6 36');await c.call('nx_motion_marker BWheel Wheel 55 0 24');await c.call('nx_save_part');report['solve']=await c.call('nx_motion_solve Probe');await c.call('nx_motion_frame Probe 0');report['markers']=await c.call('nx_motion_marker_results B');await c.call('nx_motion_collision_setup');report['checks']=[];save()
        for frame in [0,6,12,24,36]:
            await c.call(f'nx_motion_frame Probe {frame}');raw=await c.call(f'nx_motion_collision_check {frame}');vol=await c.call(f'nx_motion_collision_volumes {frame}');report['checks'].append(dict(frame=frame,raw=raw,volumes=vol));save()
        await c.call('nx_motion_frame Probe finish');await c.call('nx_save_part');await c.call('nx_close_part 0')
    marker=report['markers']['markers'][0];data=np.array([x['values'] for x in marker['coordinates']]).T;angles=np.degrees(np.arctan2(data[:,1],data[:,0]-40));report['measured_probe_angles_deg']={str(i):float(angles[i]) for i in [0,12,24,36]};assert abs(angles[12]+.2)<.0001 and abs(angles[36]-.2)<.0001
    by={x['frame']:x for x in report['checks']};assert by[0]['raw']['interference_count']==0 and by[24]['raw']['interference_count']==0 and by[36]['raw']['interference_count']==0
    v=[x for x in by[12]['volumes']['intersections'] if set([x['a'],x['b']])==set(['WHEEL','HOLD_PAWL'])];assert len(v)==1 and v[0]['volume_mm3']>0 and v[0]['body_count']>0
    report.update(passed=True,reverse_engagement_deg=.2,positive_blocking_volume_mm3=v[0]['volume_mm3'],model=str(m),loaded_contact_validation=False);save();print(json.dumps(report,indent=2))

if __name__=='__main__':asyncio.run(main(sys.argv[1]))
