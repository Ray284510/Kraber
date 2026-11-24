"""Actual NX12 surface proof through MCP. No paid generation, no synthetic geometry."""
import asyncio
import json
from pathlib import Path
import shutil
import argparse
from .nx_worker import ROOT, Client
from .checkpoint_demo import valid_part


async def main(suffix):
    assert suffix.replace('_','').isalnum()
    model=ROOT/('models/phase4_phase5_surface_'+suffix)
    out=ROOT/('logs/phase5/surface_'+suffix)
    model.mkdir();out.mkdir(exist_ok=True)
    csv=model/'sections.csv'
    csv.write_text('0,0,0,18,12\n35,0,0,26,16\n90,0,0,23,14\n150,0,0,16,10\n180,0,0,12,8\n')
    part=model/'loft_shell.prt';step=model/'loft_shell.step'
    calls=[]
    async with Client() as c:
        async def call(command):
            actual=await c.call(command,True)
            calls.append({'command':command,'actual':actual})
            (out/'actual-calls.json').write_text(json.dumps(calls,indent=2))
            assert actual.get('ok'),actual
            return actual
        state=await call('nx_session_parts');assert not state['parts']
        await call('nx_create_part '+str(part))
        loft=await call('nx_surface_loft fairing '+str(csv)+' solid')
        before=await call('nx_inspect');valid_part(before)
        assert len(before['bodies'])==1 and before['bodies'][0]['volume_mm3']>10000
        await call('nx_fit_view');await call('nx_save_part')
        shutil.copy2(part,model/'loft_before_shell.prt')
        shell=await call('nx_surface_shell '+loft['body_id']+' 2 both')
        after=await call('nx_inspect');valid_part(after)
        assert 0<after['bodies'][0]['volume_mm3']<before['bodies'][0]['volume_mm3']
        await call('nx_fit_view');await call('nx_save_part')
        await call('nx_export_step '+str(step))
        await call('nx_close_part 0');await call('nx_open_part '+str(part))
        reopened=await call('nx_inspect');valid_part(reopened)
        assert len(reopened['features'])==len(after['features'])
        assert abs(reopened['bodies'][0]['volume_mm3']-after['bodies'][0]['volume_mm3'])<0.01
        for _ in range(30):
            if step.exists() and 'END-ISO-10303-21;' in step.read_text(errors='replace')[-100:]:break
            await asyncio.sleep(1)
        assert step.exists() and 'END-ISO-10303-21;' in step.read_text(errors='replace')[-100:]
        await call('nx_close_part 0')
        await call('nx_import_step '+str(step)+' '+str(model/'step_readback.prt'))
        imported=await call('nx_inspect');valid_part(imported)
        assert len(imported['bodies'])==1
        assert abs(imported['bodies'][0]['volume_mm3']-after['bodies'][0]['volume_mm3'])<0.1
        await call('nx_save_part');await call('nx_close_part 0');await call('nx_open_part '+str(part))
        await call('nx_fit_view');await call('nx_save_part')
        evidence={'passed':True,'provider':'official_codex_fallback','paid_requests':0,
            'native_session':state,'loft':loft,'shell':shell,'before':before,'after':after,
            'prt_reopened':reopened,'step_imported':imported,'prt':str(part),'step':str(step),
            'limits':['Only ellipse section loft and two-ended 2 mm shell validated.',
                'Not a finished prop, printability validation, or DSH modeling acceptance.']}
        (out/'acceptance.json').write_text(json.dumps(evidence,indent=2))
        print(json.dumps({'passed':True,'evidence':str(out/'acceptance.json')}))


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('suffix')
    asyncio.run(main(parser.parse_args().suffix))
