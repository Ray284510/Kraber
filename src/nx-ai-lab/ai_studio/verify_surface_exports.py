"""Verify the actual STEP approximation repair without changing the original part."""
import asyncio
import json
import argparse
from .nx_worker import ROOT,Client
from .checkpoint_demo import valid_part


async def main(suffix):
    assert suffix.replace('_','').isalnum()
    model=ROOT/'models/phase4_phase5_surface_20261108_2041_repair1'
    out=ROOT/'logs/phase5/surface_20261108_2041_repair1'
    part=model/'loft_shell.prt';step=model/('loft_shell_'+suffix+'.step')
    assert not step.exists()
    calls=[]
    async with Client() as c:
        async def call(cmd):
            a=await c.call(cmd,True);calls.append({'command':cmd,'actual':a})
            (out/('export-'+suffix+'-calls.json')).write_text(json.dumps(calls,indent=2))
            assert a.get('ok'),a
            return a
        session=await call('nx_session_parts');assert not session['parts']
        await call('nx_open_part '+str(part))
        native=await call('nx_inspect');valid_part(native)
        reference=await call('nx_accurate_volume 0.9999999999')
        exported=await call('nx_export_step_precise '+str(step))
        assert 'END-ISO-10303-21;' in step.read_text(errors='replace')[-100:]
        translator_log=step.with_suffix('.log').read_text(errors='replace')
        assert '1.000000e-05 mm' in translator_log,'Requested export precision did not take effect'
        await call('nx_close_part 0')
        await call('nx_import_step '+str(step)+' '+str(model/('step_'+suffix+'_readback.prt')))
        imported=await call('nx_inspect');valid_part(imported)
        readback=await call('nx_accurate_volume 0.9999999999')
        delta=abs(reference['volume_mm3']-readback['volume_mm3'])
        assert len(imported['bodies'])==1 and delta<0.1,delta
        await call('nx_close_part 0');await call('nx_open_part '+str(part))
        await call('nx_fit_view');await call('nx_save_part')
        (out/('export-'+suffix+'-acceptance.json')).write_text(json.dumps({
            'passed':True,'provider':'codex','paid_requests':0,'original_native':native,
            'export':exported,'imported':imported,'reference_measurement':reference,
            'readback_measurement':readback,'volume_delta_mm3':delta,
            'acceptance_threshold_mm3':0.1,'native_session':session,'effective_export_tolerance_mm':0.00001,
            'limits':['Numerical volume comparison plus native body consistency, not pointwise surface deviation analysis.']},indent=2))
        print(json.dumps({'passed':True,'volume_delta_mm3':delta}))


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('suffix')
    asyncio.run(main(parser.parse_args().suffix))
