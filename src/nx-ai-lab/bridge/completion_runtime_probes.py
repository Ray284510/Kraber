"""Read-only native capability and actual MCP synthesis probes; not model acceptance."""
import asyncio,json
from phase4_common import Client,ROOT

async def main():
    out=ROOT/'logs/phase4_completion/runtime-capability-probes.json';r={}
    async with Client() as c:
        r['status']=await c.call('nx_status');assert r['status'].get('part','').startswith(str(ROOT/'models/phase4_completion_'))
        r['contact_api']=await c.tool('nx_motion_contact_capabilities',{})
        r['actual_mass_state']=await c.tool('nx_motion_mass_state',{})
        r['synthesis_cases']=[]
        for law,dwell in [('quintic',.25),('simple_harmonic',0)]:
            s={'source_text':'Read-only planning probe: a small lift with specified law, unrelated to the held-out task.','interpretation':[{'field':'output_motion','reason':'A lifting carriage moves out and back'},{'field':'motion_law','reason':'Explicitly supplied for this protocol probe'}],'input_motion':'continuous_rotation','output_motion':'reciprocating_translation','stroke_mm':8.,'period_s':2.,'max_pressure_angle_deg':30.,'motion_law':law,'dwell_fraction':dwell,'objectives':['moving_links','footprint_area_mm2'],'assumptions':['Illustrative 8 mm and 2 s; no NX building or acceptance from this request']}
            r['synthesis_cases'].append(await c.tool('nx_mechanism_synthesize',{'specification':s}))
        assert [x['selected'] for x in r['synthesis_cases']]==['disk_cam','scotch_yoke']
    out.write_text(json.dumps(r,indent=2),encoding='utf-8');print(out)
if __name__=='__main__':asyncio.run(main())
