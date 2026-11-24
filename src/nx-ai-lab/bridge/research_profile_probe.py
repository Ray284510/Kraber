"""Small NX12 profile-driver unit/interpolation/persistence probe, not mechanism acceptance."""
import asyncio, datetime, json, math, sys
from pathlib import Path
from phase4_client import ROOT, Client


async def main(number='1', interpolation='linear'):
    assert number in ('1','2','3') and interpolation in ('linear','akima')
    info=json.loads((ROOT/'logs/contact_research/current.json').read_text())
    out=Path(info['logs'])/('profile-probe-'+number);out.mkdir()
    model=ROOT/'models'/('phase4_research_profile_probe_'+datetime.datetime.now().strftime('%Y%m%d_%H%M%S'));model.mkdir()
    report={'models':str(model),'scope':'Small interface probe only; no Geneva or ratchet acceptance','passed':False}
    def save(): (out/'result.json').write_text(json.dumps(report,indent=2))
    save()
    async with Client() as c:
        assert not (await c.call('nx_status'))['part']
        for name,cmd in [('ground','nx_shape Base cylinder 0 0 0 0 0 1 20 1 5 create -'),('arm','nx_shape Arm box 10 0 6 0 0 1 24 4 3 create -')]:
            await c.call('nx_create_part '+str(model/(name+'.prt')));await c.call(cmd);report[name]=await c.call('nx_inspect');save();await c.call('nx_save_part');await c.call('nx_close_part 0')
        await c.call('nx_create_part '+str(model/'assembly.prt'))
        for name in ['ground','arm']:await c.call(f'nx_add_component {model/(name+".prt")} {name.upper()} 0 0 0')
        await c.call('nx_save_part');await c.call('nx_motion_new '+str(model/'probe.sim'));await c.call('nx_motion_setup');await c.call('nx_motion_analysis kinematics')
        await c.call('nx_motion_link Fixed 1 GROUND');await c.call('nx_motion_link Arm 0 ARM');await c.call('nx_motion_joint Rotation Revolute Arm Fixed 0 0 6 0 0 1')
        (model/'profile.csv').write_text('\n'.join(f'{i/10},{i*3}' for i in range(11)))
        report['profile']=await c.call('nx_research_profile_driver Test Rotation '+str(model/'profile.csv')+' '+interpolation,True);save()
        if not report['profile']['ok']:await c.call('nx_save_part');raise RuntimeError('Actual native profile creation failed; preserve probe')
        await c.call('nx_motion_marker P Arm 20 0 7');report['solution']=await c.call('nx_motion_solution Cycle normal 1 20');await c.call('nx_motion_tighten Cycle 1e-9');report['solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['solve']['ok']
        await c.call('nx_motion_frame Cycle 0');report['markers']=await c.call('nx_motion_marker_results P');save()
        coords=report['markers']['markers'][0]['coordinates'];x,y=[v['values'] for v in coords[:2]];angles=[math.degrees(math.atan2(b,a)) for a,b in zip(x,y)];report['max_error_deg']=max(abs(v-1.5*i) for i,v in enumerate(angles));assert len(angles)==21 and report['max_error_deg']<1e-4
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');await c.call('nx_close_part 0');await c.call('nx_open_part '+str(model/'probe.sim'));await c.call('nx_motion_setup');report['reopened_solve']=await c.call('nx_motion_solve Cycle',True);save();assert report['reopened_solve']['ok']
        await c.call('nx_motion_frame Cycle 20');report['reopened_markers']=await c.call('nx_motion_marker_results P')
        coords=report['reopened_markers']['markers'][0]['coordinates'];x,y=[v['values'] for v in coords[:2]];report['reopened_max_error_deg']=max(abs(math.degrees(math.atan2(b,a))-1.5*i) for i,(a,b) in enumerate(zip(x,y)));assert len(x)==21 and report['reopened_max_error_deg']<1e-4
        await c.call('nx_motion_frame Cycle finish');await c.call('nx_save_part');await c.call('nx_close_part 0');report['passed']=True;save()


if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
