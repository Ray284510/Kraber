import asyncio,json,sys
from pathlib import Path
from phase4_common import Client,LOG
async def main(family):
    r=json.loads((LOG/f'current-{family}.json').read_text());out=Path(r['logs']);rows=[];positive=[];errors=[]
    async with Client() as c:
        assert (await c.call('nx_status'))['part']==str(Path(r['models'])/'mechanism.sim')
        await c.call('nx_motion_frame Cycle 0');await c.call('nx_motion_collision_setup')
        for i in range(r['plan']['drive']['steps']+1):
            await c.call(f'nx_motion_frame Cycle {i}');raw=await c.call(f'nx_motion_collision_check {i}');v=await c.call(f'nx_motion_collision_volumes {i}') if raw['interference_count'] else {'intersections':[]}
            rows.append({'frame':i,'raw':raw,'volumes':v})
            for a in v['intersections']:
                if a.get('volume_mm3',0)>0:positive.append({'frame':i,**a})
                if 'error' in a:errors.append({'frame':i,**a})
            (out/'contact-diagnostic-all-frames.json').write_text(json.dumps({'diagnostic_only_not_acceptance':True,'frames':rows,'positive_intersections':positive,'errors':errors},indent=2))
        await c.call('nx_motion_frame Cycle finish');print('Positive volumes:',len(positive),'query errors:',len(errors))
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
