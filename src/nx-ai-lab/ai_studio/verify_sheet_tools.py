"""Actual two-sheet sew and thickening experiment, separate from shell test."""
import asyncio
import json
import shutil
from .nx_worker import ROOT,Client


def valid(state):
    assert state['bodies'] and all(b['consistency_fault_count']==0 for b in state['bodies'])
    assert all(not f['errors'] for f in state['features'])


async def main():
    model=ROOT/'models/phase4_phase5_sheet_20261108_2046'
    out=ROOT/'logs/phase5/sheet_20261108_2046'
    model.mkdir();out.mkdir(exist_ok=True)
    a=model/'front.csv';b=model/'back.csv'
    a.write_text('0,0,0,24,16\n100,0,0,18,12\n')
    b.write_text('100,0,0,18,12\n200,0,0,12,8\n')
    part=model/'sewn_thickened.prt';calls=[]
    async with Client() as c:
        async def call(cmd):
            r=await c.call(cmd,True);calls.append({'command':cmd,'actual':r})
            (out/'actual-calls.json').write_text(json.dumps(calls,indent=2))
            assert r.get('ok'),r
            return r
        state=await call('nx_session_parts')
        assert state['all_modified_flags_known'] and not state['any_modified']
        assert all(Path(p['path']).parent==ROOT/'models/phase4_phase5_surface_20261108_2041_repair1' for p in state['parts'])
        if state['parts']:await call('nx_close_part 0')
        await call('nx_create_part '+str(part))
        first=await call('nx_surface_loft front '+str(a)+' sheet')
        second=await call('nx_surface_loft back '+str(b)+' sheet')
        before=await call('nx_surface_state');valid(before)
        assert len(before['bodies'])==2 and not any(x['solid'] for x in before['bodies'])
        await call('nx_save_part');shutil.copy2(part,model/'two_sheets_before_sew.prt')
        sew=await call('nx_surface_sew '+first['body_id']+','+second['body_id'])
        sewn=await call('nx_surface_state');valid(sewn)
        assert not sew['solid']
        await call('nx_save_part');shutil.copy2(part,model/'sewn_before_thicken.prt')
        thick=await call('nx_surface_thicken '+sew['body_id']+' 2')
        after=await call('nx_surface_state');valid(after)
        assert len([x for x in after['bodies'] if x['solid']])==1
        await call('nx_fit_view');await call('nx_save_part');await call('nx_close_part 0')
        await call('nx_open_part '+str(part));reopened=await call('nx_surface_state');valid(reopened)
        assert len(reopened['features'])==len(after['features'])
        assert len([x for x in reopened['bodies'] if x['solid']])==1
        await call('nx_fit_view');await call('nx_save_part')
        (out/'acceptance.json').write_text(json.dumps({'passed':True,'provider':'codex','paid_requests':0,
            'before':before,'sew':sew,'sewn':sewn,'thick':thick,'after':after,'reopened':reopened,
            'prt':str(part),'scope':'Two matching elliptical conical sheets, native sewing and 2 mm thickening; no arbitrary surface or full prop claim.'},indent=2))
        print(json.dumps({'passed':True,'prt':str(part)}))


from pathlib import Path
if __name__=='__main__':asyncio.run(main())
