import asyncio,json,sys
from pathlib import Path
from phase4_common import Client,LOG
async def main(kind,spacing):
    r=json.loads((LOG/f'current-completion_{kind}.json').read_text());m=Path(r['models']);out=Path(r['logs'])
    async with Client() as c:
        assert (await c.call('nx_status'))['part']==str(m/'mechanism.sim')
        before=await c.call('nx_motion_higher_pairs');change=await c.tool('nx_motion_refine_point_curve_sampling',{'name':'ActualSharpContact','spacing_mm':float(spacing)});after=await c.call('nx_motion_higher_pairs')
        (out/f'sampling-refinement-{spacing}.json').write_text(json.dumps({'before':before,'change':change,'after':after},indent=2));await c.call('nx_save_part');await c.call('nx_close_part 0')
if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
