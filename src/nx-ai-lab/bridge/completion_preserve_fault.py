"""Preserve the active experimental run and consume one persistent repair slot."""
import asyncio,json,shutil,sys,datetime
from pathlib import Path
from phase4_common import Client,ROOT,LOG
from mechanism_agent.repair import RepairBudget

async def main(family,fault,diagnosis,change,evidence,save_mode='save'):
    run=json.loads((LOG/f'current-{family}.json').read_text(encoding='utf-8'));m=Path(run['models']);out=Path(run['logs']);evidence=Path(evidence)
    assert evidence.is_file()
    async with Client() as c:
        state=await c.call('nx_status');active=Path(state['part']);assert active.parent.resolve()==m.resolve()
        if active.suffix.lower()=='.sim':await c.call('nx_motion_frame Cycle finish',True)
        assert save_mode in ('save','known-save-failure')
        if save_mode=='save':await c.call('nx_save_part')
        snap=out/('before_'+fault+'_'+datetime.datetime.now().strftime('%H%M%S'));shutil.copytree(m,snap)
        if save_mode=='known-save-failure':(snap/'SAVE_FAILURE_NOTE.json').write_text(json.dumps({'mode':save_mode,'preserved':'Last existing on-disk checkpoints; unsavable in-memory changes are documented by actual error and screenshots, not claimed serialized','active_part':str(active)}))
        failure_copy=snap/('actual-failure.log' if evidence.suffix=='.log' else 'actual-failure.json');shutil.copy2(evidence,failure_copy)
        budget=RepairBudget(ROOT/'logs/phase4_completion/repairs',fault);i=budget.begin(diagnosis,change,snap/active.name,failure_copy)
        (ROOT/f'logs/phase4_completion/repair-{fault}-{i}.json').write_text(json.dumps({'attempt':i,'before_run':run,'snapshot':str(snap)},indent=2))
        await c.call('nx_close_part 0');print('Preserved fault, repair attempt',i)
if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
