"""Cloud-interpreted specification -> genuine MCP planning -> parameterized NX build.

The request/assumptions are supplied in a separate file, never embedded here.
Unsupported realization routes stop explicitly; they never fall back to a feeder.
"""
import asyncio,json,re,sys
from pathlib import Path
from phase4_common import Client,ROOT
from mechanism_agent.synthesized_feeder import plan_from_selection
from phase4_feeder_build import main as build_feeder

async def main(spec_path,family):
    if not re.fullmatch(r'completion_[a-z0-9_]+',family):raise ValueError('Dedicated completion family required')
    spec=json.loads(Path(spec_path).read_text(encoding='utf-8-sig'))
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part')
        proposal=await c.tool('nx_mechanism_synthesize',{'specification':spec})
        selection=await c.tool('nx_mechanism_realize',{'specification':spec})
    if selection['selected']=='inline_slider_crank':
        plan=plan_from_selection(selection);await build_feeder(plan['parameters']['crank_radius'],family,plan,selection)
    elif selection['selected']=='disk_cam':
        from completion_synthesized_cam_build import main as build_cam
        await build_cam(selection,family)
    elif selection['selected']=='scotch_yoke':
        from completion_yoke_build import main as build_yoke
        await build_yoke(selection,family)
    else:raise RuntimeError('Selected '+str(selection['selected'])+' requires its own realization adapter; no silent mechanism substitution')
if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
