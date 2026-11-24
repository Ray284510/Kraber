import asyncio,sys
from phase4_common import Run,Client
from completion_helpers import datum,hinges_and_slider,finish_assembly,rectangle

from mechanism_agent.completion_gears import design

async def main(kind):
    plan,profiles=design(kind);refs={}
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run('completion_'+kind,plan)
        for name,pts in profiles.items():(run.model/(name+'.csv')).write_text('\n'.join(','.join(f'{v:.12f}' for v in p) for p in pts))
        async def new(name):await c.call('nx_create_part '+str(run.model/(name+'.prt')))
        async def finish(name,color):await run.finish(c,name,color);run.save('joint-references.json',refs)
        async def profile(name,z,body=None):
            if body:r=await c.call(f'nx_profile_boolean {name} {run.model/(name+".csv")} {z} 8 unite {body} polyline')
            else:r=await c.call(f'nx_profile_solid {name} {run.model/(name+".csv")} {z} 8 polyline')
            return r['body_id']
        await new('ground');await c.call('nx_application modeling')
        if kind=='multistage_gears':
            body=await run.shape(c,'base',[67.5,0,0],[0,0,1],185,5,kind='box',height=110)
            for name,xy in plan['centers'].items():
                top=39.6 if name=='output' else 19.6
                body=await run.cylinder(c,'bearing',xy,5,18,top-5,body);body=await run.cylinder(c,'bore',xy,0,8.4,top,body,'subtract')
        elif kind=='internal_gears_direct':
            body=await run.shape(c,'base',[5,0,0],[0,0,1],170,5,kind='box',height=150)
            body=await run.cylinder(c,'ring_bearing',[0,0],5,18,4.6,body);body=await run.cylinder(c,'ring_bore',[0,0],0,8.4,9.6,body,'subtract')
            body=await run.shape(c,'post',[78,0,5],[0,0,1],10,43.2,body,kind='box',height=18)
            body=await run.shape(c,'roof',[60,0,44.2],[0,0,1],46,4,body,kind='box',height=18)
            body=await run.cylinder(c,'pinion_bore',[45,0],44.2,8.4,4,body,'subtract')
        else:
            body=await run.shape(c,'base',[50,0,0],[0,0,1],330,5,kind='box',height=80)
            body=await run.cylinder(c,'bearing',[0,15],5,18,18.6,body);body=await run.cylinder(c,'bore',[0,15],0,8.4,23.6,body,'subtract')
            body=await run.shape(c,'guide',[50,-18,5],[0,0,1],320,15.2,body,kind='box',height=24)
            body=await run.shape(c,'broad_channel',[50,-18,11.8],[0,0,1],320,6.4,body,'subtract',kind='box',height=20.4)
            body=await run.shape(c,'neck_channel',[50,-18,18.2],[0,0,1],320,2,body,'subtract',kind='box',height=6.4)
        for j in plan['joints']:
            if j['type']=='Revolute':await datum(c,refs,'ground',j['name'],j['origin'])
        await finish('ground',103)
        if kind=='multistage_gears':
            for name,color in [('input',186),('compound',36),('output',211)]:
                await new(name);xy=plan['centers'][name];z=40 if name=='output' else 20
                body=await profile('compound_low' if name=='compound' else name,z)
                body=await run.cylinder(c,'shaft',xy,0,8,z,body)
                if name=='compound':
                    body=await run.cylinder(c,'compound_shaft',xy,28,8,12,body);body=await profile('compound_high',40,body)
                j=next(j for j in plan['joints'] if j['first'].lower()==name);await datum(c,refs,name,j['name'],j['origin']);await finish(name,color)
        elif kind=='internal_gears_direct':
            await new('ring');body=await run.cylinder(c,'web',[0,0],10,136,4);body=await run.cylinder(c,'ring_blank',[0,0],14,136,14,body)
            body=(await c.call(f'nx_profile_boolean RingTeeth {run.model/"ring_void.csv"} 14 14 subtract {body} polyline'))['body_id'];body=await run.cylinder(c,'shaft',[0,0],0,8,10,body)
            await datum(c,refs,'ring','RingAxis',[0,0,24]);await finish('ring',186)
            await new('pinion');body=await profile('pinion',20);body=await run.cylinder(c,'shaft',[45,0],28,8,20.2,body);await datum(c,refs,'pinion','PinionAxis',[45,0,24]);await finish('pinion',211)
        else:
            await new('pinion');body=await profile('pinion',24);body=await run.cylinder(c,'shaft',[0,15],0,8,24,body);await datum(c,refs,'pinion','PinionAxis',[0,15,24]);await finish('pinion',186)
            await new('rack');body=await profile('rack',24)
            body=await rectangle(run,c,'rack_bridge',[0,-12],[1,0],-101,101,12,22,2,body)
            body=await rectangle(run,c,'rack_stem',[0,-18],[1,0],-101,101,4,18,4,body)
            body=await rectangle(run,c,'rack_pad',[0,-18],[1,0],-101,101,20,12,6,body);await finish('rack',36)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -')
        hinges=[(j['name'],j['first'].lower(),'ground') for j in plan['joints'] if j['type']=='Revolute']
        if kind=='rack_pinion':await hinges_and_slider(run,c,refs,hinges,('rack','ground',[1,0],-8,-7.8,12,11.8))
        else:
            for key,a,b in hinges:
                for constraint,field,prefix in [('touch','point_name','point:'),('align','curve_name','curve:')]:await c.call(f'nx_constraint {constraint} {key}_{constraint} {a.upper()} {prefix+refs[a][key][field]} GROUND {prefix+refs[b][key][field]} 0 same')
        owners={'GROUND':'Link0',**{name.upper():name.title() for name in plan['centers']}}
        await finish_assembly(run,c,owners)
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
