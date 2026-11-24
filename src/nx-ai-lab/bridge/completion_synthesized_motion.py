"""Dispatch a synthesized model to its matching native motion verification."""
import asyncio,json,sys
from phase4_common import LOG

async def main(family):
    run=json.loads((LOG/f'current-{family}.json').read_text(encoding='utf-8'))
    selection=run['plan']['selection'];kind=selection['selected']
    if kind=='inline_slider_crank':
        from phase4_feeder_motion import main as motion
        await motion(family)
    elif kind=='disk_cam':
        from phase4_cam_groove_motion import main as motion
        from mechanism_agent.synthesized_cam import law_from_parameters
        await motion(family,law_from_parameters(selection['selected_parameters']))
    elif kind=='scotch_yoke':
        from completion_yoke_motion import main as motion
        await motion(family)
    else:raise ValueError('No verified motion adapter for '+str(kind))

if __name__=='__main__':asyncio.run(main(sys.argv[1]))
