"""Diagnose existing partial native results; never grant full acceptance."""
import asyncio,json,sys
from pathlib import Path
from phase4_common import Client,LOG
from completion_contact_check import check


async def main(family,prefix):
    run=json.loads((LOG/f'current-{family}.json').read_text());root=Path(run['models']);out=Path(run['logs'])/'partial-contact-diagnosis.json'
    assert not out.exists()
    report={'scope':'Existing partial native solve diagnosis only; full acceptance is false','complete_acceptance':False,'frames':[]}
    def save():out.write_text(json.dumps(report,indent=2))
    async with Client() as client:
        assert (await client.call('nx_status'))['part']==str(root/'mechanism.sim')
        await client.call('nx_motion_setup');await client.call('nx_application motion');await client.call('nx_motion_frame Cycle 0')
        report['markers']=await client.call('nx_motion_marker_results '+prefix);save()
        counts={len(c['values']) for marker in report['markers']['markers'] for c in marker['coordinates']}
        assert len(counts)==1;count=counts.pop();assert 0<count<run['plan']['drive']['steps']+1
        report['actual_partial_sample_count']=count;report['collision_setup']=await client.call('nx_motion_collision_setup');save()
        allowed=[['WHEEL','DRIVE_PAWL'],['WHEEL','HOLD_PAWL']] if family=='completion_ratchet' or family.startswith('finalization_ratchet_') else [['INPUT','OUTPUT']]
        try:
            for frame in range(count):
                await client.call(f'nx_motion_frame Cycle {frame}');result=await check(client,frame,allowed);report['frames'].append(result);save()
                if not result['passed']:break
        finally:
            await client.call('nx_motion_frame Cycle finish');await client.call('nx_save_part')
    print({'actual_partial_samples':count,'checked_frames':len(report['frames']),'complete_acceptance':False})


if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
