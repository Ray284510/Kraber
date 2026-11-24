import asyncio,json,sys
from phase4_common import Run,Client,LOG
from mechanism_agent.completion_fourbar import design

async def main(kind):
    plan=design(kind)
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run('completion_'+kind,plan);p=plan['initial_points'];O,A,C,D=[p[k] for k in ['O','A','C','D']];refs={}
        async def datum(part,name,xy,z):
            refs.setdefault(part,{})[name]=await c.call('nx_joint_reference '+' '.join(map(str,[name,*xy,z,0,0,1])))
        async def finish(name,color):await run.finish(c,name,color);run.save('joint-references.json',refs)
        await c.call('nx_create_part '+str(run.model/'ground.prt'));await c.call('nx_application modeling')
        body=await run.shape(c,'base',[D[0]/2,55,0],[0,0,1],D[0]+40,4,kind='box',height=170)
        body=await run.cylinder(c,'lower_bearing',O,4,18,14,body);body=await run.cylinder(c,'lower_bore',O,0,6.4,18,body,'subtract')
        body=await run.shape(c,'frame_column',[D[0],125,4],[0,0,1],18,59.6,body,kind='box',height=18)
        body=await run.shape(c,'upper_bridge',[D[0],62.5,63.6],[0,0,1],18,4,body,kind='box',height=125)
        body=await run.cylinder(c,'upper_bearing',D,49.6,18,14,body);body=await run.cylinder(c,'upper_bore',D,49.6,6.4,18,body,'subtract')
        await datum('ground','J0',O,18.2);await datum('ground','J3',D,49.4);await finish('ground',103)
        for name,a,b,z,color,j1,z1,j2,z2 in [('input',O,A,18.4,186,'J0',18.2,'J1',27.6),('coupler',A,C,30.8,36,'J1',27.6,'J2',40),('output',D,C,43.2,211,'J3',49.4,'J2',40)]:
            await run.bar(c,name,a,b,z,width=18);await datum(name,j1,a,z1);await datum(name,j2,b,z2);await finish(name,color)
        for name,xy,z,end,head_z in [('pin_o',O,0,24.3,-2),('pin_d',D,43.3,67.6,67.6),('pin_a',A,18.4,39.1,16.4),('pin_c',C,30.6,51.5,28.6)]:
            await c.call('nx_create_part '+str(run.model/(name+'.prt')));body=await run.cylinder(c,'shaft',xy,z,6,end-z);body=await run.cylinder(c,'head',xy,head_z,9,2,body);await finish(name,192)
        for name,xy,z in [('collar_a',A,37),('collar_c',C,49.4)]:
            await c.call('nx_create_part '+str(run.model/(name+'.prt')));body=await run.cylinder(c,'collar',xy,z,9,2);body=await run.cylinder(c,'collar_bore',xy,z,6.1,2,body,'subtract');await finish(name,192)
        await run.add_all(c);await c.call('nx_constraint fix BaseFixed GROUND component -')
        actual={name:await c.call('nx_component_datums '+name.upper()) for name in refs};run.save('native-datums.json',actual)
        for name,items in refs.items():
            points={x['name'] for x in actual[name]['prototype_points'] if x['occurrence_available']};curves={x['name'] for x in actual[name]['prototype_curves'] if x['occurrence_available']}
            for row in items.values():assert row['point_name'] in points and row['curve_name'] in curves
        for name,a,b in [('J0','input','ground'),('J1','coupler','input'),('J2','coupler','output'),('J3','output','ground')]:
            for kind,key,prefix in [('touch','point_name','point:'),('align','curve_name','curve:')]:await c.call(f'nx_constraint {kind} {name}_{kind} {a.upper()} {prefix+refs[a][name][key]} {b.upper()} {prefix+refs[b][name][key]} 0 same')
        for child,parent in [('PIN_O','GROUND'),('PIN_D','GROUND'),('PIN_A','INPUT'),('COLLAR_A','INPUT'),('PIN_C','OUTPUT'),('COLLAR_C','OUTPUT')]:await c.call(f'nx_constraint bond {child}_rigid {child} component {parent} component 0 same')
        state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0 and all(x['dof_result']=='Success' for x in state['components'])
        inter=await c.call('nx_interference');run.save('static-interference.json',inter);await c.call('nx_save_part');await run.export(c,'assembly')
        owner={name:link for link,names in [('ground',['GROUND','PIN_O','PIN_D']),('input',['INPUT','PIN_A','COLLAR_A']),('output',['OUTPUT','PIN_C','COLLAR_C']),('coupler',['COUPLER'])] for name in names}
        assert not any(x['nx_result']==1 or (x['nx_result']==3 and owner[x['a']]!=owner[x['b']]) for x in inter['pairs'])
        run.meta['build_passed']=True;run.pointer()
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
