"""One explicitly budgeted retry of the real contact probe, retaining earlier results."""
import asyncio,json,sys
from pathlib import Path
import numpy as np
from phase4_common import Client,LOG,ROOT
from mechanism_agent.repair import RepairBudget

async def main(attempt,model='midplane',step=None):
    attempt=int(attempt);ptr=LOG/'current-completion_contact_probe.json';run=json.loads(ptr.read_text());m=Path(run['models']);out=Path(run['logs']);target=out/f'contact-probe-repair-{attempt}.json';assert not target.exists();budget=RepairBudget(ROOT/'logs/phase4_completion/repairs','native-contact-energy');assert (budget.directory/f'attempt-{attempt}.json').exists();report={}
    def save():target.write_text(json.dumps(report,indent=2))
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');await c.call('nx_open_part '+run['simulation']);await c.call('nx_motion_setup');await c.call('nx_motion_analysis dynamics');report['before_contact']=await c.call('nx_motion_contact_state');report['edit']=await c.call(f'nx_motion_contact_model PushContact {model}');report['after_contact']=await c.call('nx_motion_contact_state');await c.call('nx_save_part');save()
        if step:report['refined_integrator']=await c.call(f'nx_motion_integrator_refine Contact 1e-9 {step}');await c.call('nx_save_part');save()
        report['solve']=await c.call('nx_motion_solve Contact',True);save();assert report['solve']['ok']
        await c.call('nx_application motion');await asyncio.sleep(.5);await c.call('nx_view iso .8');await c.call('nx_motion_frame Contact 0');report['markers']=await c.call('nx_motion_marker_results P');save();data={r['marker']:np.array([v['values'] for v in r['coordinates']]).T for r in report['markers']['markers']};a,b=data['PInput'],data['POutput'];speed=float(np.mean(np.diff(b[-21:,0]))/.01);text=(m/'contact_probe-contact.minf').read_text(errors='replace');warnings=[line.strip() for line in text.splitlines() if any(w in line.lower() for w in ['warning','not apply force','cannot be closed','error'])];bad=any(w in text.lower() for w in ['not apply force','cannot be closed']);report['native_warnings']=warnings;report['observations']={'native_final_output_speed_mm_s':speed,'passive_speed_upper_bound_mm_s':2,'output_travel_mm':float(b[-1,0]-b[0,0]),'precontact_drift_mm':float(max(abs(b[:95,0]-16))),'sampled_max_overlap_mm':float(max(0,np.max(a[:,0]+10-b[:,0]))),'sampling_scope':'201 output samples at 0.01 s; does not bound unrecorded contact-event penetration','scope':'Passive constant-speed wall impact capability check, not zero-penetration mechanism acceptance'};report['physics_check_passed']=1<=speed<=2.01 and not bad and report['observations']['precontact_drift_mm']<.001;save();await c.call('nx_motion_frame Contact finish');await c.call('nx_save_part');budget.finish(attempt,{'observations':report['observations'],'native_warnings':warnings},run['simulation'],target,report['physics_check_passed']);print(json.dumps({'attempt':attempt,'physics_check_passed':report['physics_check_passed'],'observations':report['observations']}))

if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
