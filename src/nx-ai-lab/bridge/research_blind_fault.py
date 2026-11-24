"""Explicit controlled real assembly-fault injection and evidence-driven repair.
Not a claim that the untouched planner spontaneously generated this fault.
"""
import asyncio,json,sys,shutil,hashlib,subprocess
from pathlib import Path
from phase4_common import Client,ROOT,LOG
from mechanism_agent.repair import RepairBudget

async def main(family):
    run=json.loads((LOG/f'current-{family}.json').read_text());m=Path(run['models']);out=Path(run['logs']);b=Path(json.loads((ROOT/'logs/phase4_blind/current.json').read_text())['logs']);snap=m/'controlled_fault';snap.mkdir();report={'scope':'User-authorized controlled misassembly injection; actual NX geometry/solver evidence, not an unforced design failure','injected_parameter':'GuideHeight','injected_value':'6.0'}
    def save():(out/'controlled-fault-repair.json').write_text(json.dumps(report,indent=2))
    def snapshot(name):
        target=snap/name;target.mkdir()
        for p in m.glob('*.prt'):shutil.copy2(p,target/p.name)
        return target
    original_parts={n:hashlib.sha256((m/(n+'.prt')).read_bytes()).hexdigest() for n in run['part_names']}
    async with Client() as c:
        assert (await c.call('nx_status'))['part']==run['assembly'];report['baseline']=await c.call('nx_assembly_state');baseline={r['name']:r for r in report['baseline']['constraints']};assert baseline['GuideHeight']['expression'] is not None
        await c.call('nx_save_part');await c.call('nx_close_part 0');report['before_model']=str(snapshot('before')/'assembly.prt');await c.call('nx_open_part '+run['assembly']);await c.call('nx_view iso .75')
        recording=out/'controlled_fault_recording';proc=subprocess.Popen([sys.executable,str(ROOT/'bridge/record_phase2.py'),str(recording),'8'],creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(100):
                if (recording/'ready.json').exists():break
                await asyncio.sleep(.1)
            else:raise RuntimeError('Recorder not ready')
            await asyncio.sleep(2);report['injection_result']=await c.call('nx_constraint_value GuideHeight 6.0',True);report['failure_state']=await c.call('nx_assembly_state');report['failure_interference']=await c.call('nx_interference');save()
            actual={r['name']:r for r in report['failure_state']['constraints']};mismatch=[name for name,row in actual.items() if row['expression']!=baseline[name]['expression']]
            collisions=[r for r in report['failure_interference']['pairs'] if r['nx_result']==1]
            assert collisions and mismatch==['GuideHeight'],'Injected error not established by actual geometry and constraint state'
            report['diagnosis']={'mismatched_native_constraint':mismatch,'native_intersecting_pairs':collisions,'cause':'Guide height actual value differs from recorded design state; the output assembly occupies other solids. Correct the erroneous distance constraint, without changing part geometry or collision tolerance.'};await asyncio.sleep(5.2);await c.call('nx_save_part');await c.call('nx_close_part 0');failed=snapshot('failure');report['failure_model']=str(failed/'assembly.prt');save()
            budget=RepairBudget(b/'repairs','controlled-guide-height',limit=3);attempt=budget.begin(report['diagnosis']['cause'],'Restore the independently recorded original native distance expression and verify actual geometry, constraints and unchanged prototypes.',failed/'assembly.prt',out/'controlled-fault-repair.json')
            await c.call('nx_open_part '+run['assembly']);name=mismatch[0];report['repair']=await c.call(f'nx_constraint_value {name} '+baseline[name]['expression']);report['repaired_state']=await c.call('nx_assembly_state');report['repaired_interference']=await c.call('nx_interference');save();assert report['repaired_state']['unsatisfied_constraint_count']==0 and all(r['nx_result']!=1 for r in report['repaired_interference']['pairs'])
            for row in report['repaired_state']['constraints']:assert row['expression']==baseline[row['name']]['expression']
            await c.call('nx_save_part');await asyncio.sleep(5.2);await c.call('nx_close_part 0');fixed=snapshot('repaired');report['repaired_model']=str(fixed/'assembly.prt');report['prototype_sha256_unchanged']={n:hashlib.sha256((m/(n+'.prt')).read_bytes()).hexdigest()==h for n,h in original_parts.items()};assert all(report['prototype_sha256_unchanged'].values());report.update(passed=True,attempt=attempt,recording=str(recording));save();budget.finish(attempt,'Actual NX intersecting pairs detected; incorrect distance expression restored. All constraints solved, interference clear, every part prototype SHA256 unchanged.',fixed/'assembly.prt',out/'controlled-fault-repair.json',True);await c.call('nx_open_part '+run['assembly'])
        finally:(recording/'stop').touch();proc.wait(timeout=15)

if __name__=='__main__':asyncio.run(main(sys.argv[1]))
