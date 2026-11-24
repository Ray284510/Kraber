import asyncio,sys
from phase4_common import Run,Client
from completion_helpers import datum,finish_assembly

from mechanism_agent.completion_planetary import design

async def main(kind):
    plan,profiles=design(kind);refs={}
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run('completion_'+kind,plan)
        for name,pts in profiles.items():(run.model/(name+'.csv')).write_text('\n'.join(','.join(f'{v:.12f}' for v in p) for p in pts))
        async def new(n):await c.call('nx_create_part '+str(run.model/(n+'.prt')))
        async def finish(n,color):await run.finish(c,n,color);run.save('joint-references.json',refs)
        await new('ground');await c.call('nx_application modeling');body=await run.cylinder(c,'base',[0,0],0,150,5)
        body=await run.cylinder(c,'housing',[0,0],5,150,24,body);body=await run.cylinder(c,'housing_clearance',[0,0],5,136.4,24,body,'subtract');body=await run.cylinder(c,'carrier_bore',[0,0],0,12.4,5,body,'subtract')
        for key in ['CarrierBase','RingBase']:await datum(c,refs,'ground',key,[0,0,24])
        await finish('ground',103)
        await new('ring');body=await run.cylinder(c,'ring_blank',[0,0],20,136,8);await c.call(f'nx_profile_boolean InternalTeeth {run.model/"ring_void.csv"} 20 8 subtract {body} polyline');await datum(c,refs,'ring','RingBase',[0,0,24]);await finish('ring',211)
        await new('carrier');body=await run.cylinder(c,'carrier_plate',[0,0],10,106,6);body=await run.cylinder(c,'bearing_hub',[0,0],0,12,10,body);body=await run.cylinder(c,'sun_bore',[0,0],0,8.4,16,body,'subtract')
        for xy in plan['planet_centers']:body=await run.cylinder(c,'planet_pin',xy,16,6,12,body)
        for key in ['CarrierBase','SunBase']:await datum(c,refs,'carrier',key,[0,0,24])
        for i,xy in enumerate(plan['planet_centers'],1):await datum(c,refs,'carrier',f'PlanetBase{i}',xy+[24])
        await finish('carrier',36)
        await new('sun');body=(await c.call(f'nx_profile_solid SunTeeth {run.model/"sun.csv"} 20 8 polyline'))['body_id'];body=await run.cylinder(c,'sun_shaft',[0,0],0,8,20,body);await datum(c,refs,'sun','SunBase',[0,0,24]);await finish('sun',186)
        for i,xy in enumerate(plan['planet_centers'],1):
            name=f'planet{i}';await new(name);body=(await c.call(f'nx_profile_solid PlanetTeeth {run.model/(name+".csv")} 20 8 polyline'))['body_id'];body=await run.cylinder(c,'bore',xy,20,6.4,8,body,'subtract');await datum(c,refs,name,f'PlanetBase{i}',xy+[24]);await finish(name,14+20*i)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -')
        hinges=[('RingBase','ring','ground'),('CarrierBase','carrier','ground'),('SunBase','sun','carrier')]+[(f'PlanetBase{i}',f'planet{i}','carrier') for i in range(1,4)]
        for key,a,b in hinges:
            for constraint,field,prefix in [('touch','point_name','point:'),('align','curve_name','curve:')]:await c.call(f'nx_constraint {constraint} {key}_{constraint} {a.upper()} {prefix+refs[a][key][field]} {b.upper()} {prefix+refs[b][key][field]} 0 same')
        # Suppress the grounded member's hinge before bonding it, avoiding redundant assembly constraints.
        grounded_key='CarrierBase' if plan['fixed_member']=='CARRIER' else 'SunBase'
        for suffix in ['touch','align']:await c.call(f'nx_constraint_suppress {grounded_key}_{suffix} 1')
        await c.call(f'nx_constraint bond FixedMember {plan["fixed_member"]} component GROUND component 0 same')
        owners={n.upper():('Link0' if n=='ground' or n.upper()==plan['fixed_member'] else n.title()) for n in run.parts};await finish_assembly(run,c,owners)
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
