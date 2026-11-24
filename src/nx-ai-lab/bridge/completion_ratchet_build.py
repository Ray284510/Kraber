import asyncio,math,sys
from phase4_common import Run,Client
from mechanism_agent.completion_ratchet import design

from completion_datum_references import joint_references

async def main(family='completion_ratchet'):
    assert family=='completion_ratchet' or family.startswith(('finalization_ratchet_','research_ratchet_'))
    if family.startswith('research_ratchet_'):
        from mechanism_agent.research_ratchet_geometry import design as research_design
        plan,profiles=research_design()
    else:plan,profiles=design()
    p=plan['initial_points'];O,A,C,D=[p[k] for k in ['O','A','C','D']];H=p['hold_pivot']
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');plan['family']=family;run=Run(family,plan)
        for name,points in profiles.items():(run.model/(name+'.csv')).write_text('\n'.join(','.join(f'{v:.12f}' for v in a) for a in points))
        async def new(n):await c.call('nx_create_part '+str(run.model/(n+'.prt')));await c.call('nx_application modeling')
        async def datum(n,point):await c.call('nx_joint_reference '+n+' '+' '.join(map(str,[*point,0,0,1])))
        async def finish(n,color):run.save(n+'-density.json',await c.call('nx_body_density 7800'));await run.finish(c,n,color)
        await new('ground');body=await run.shape(c,'base',[35,5,0],[0,0,1],110,5,kind='box',height=100)
        for xy in [O,D]:
            body=await run.cylinder(c,'bearing',xy,5,18,4.6,body);body=await run.cylinder(c,'bore',xy,0,6.4,9.6,body,'subtract')
        body=await run.cylinder(c,'hold_support',H,5,10,15,body);body=await run.cylinder(c,'hold_pin',H,20,6,10.4,body);body=await run.cylinder(c,'hold_cap',H,28.2,10,2,body)
        for n,pt in [('O',O+[10]),('D',D+[10]),('Wheel',D+[20.4]),('H',H+[24])]:await datum(n,pt)
        await finish('ground',103)
        await new('crank');body=await run.cylinder(c,'hub',O,10,14,6);body=await run.cylinder(c,'end',A,10,10,6,body);body=await run.cylinder(c,'shaft',O,0,6,10,body);body=await run.cylinder(c,'pin',A,16,6,20.8,body);body=await run.cylinder(c,'cap',A,34.6,10,2,body);await datum('O',O+[10]);await datum('A',A+[28.2]);await finish('crank',186)
        await new('rocker');body=await run.cylinder(c,'hub',D,10,10,6);delta=[C[i]-D[i] for i in range(2)];body=await run.shape(c,'arm',D+[13],[*delta,0],6,math.hypot(*delta),body);body=await run.cylinder(c,'end',C,10,10,6,body);body=await run.cylinder(c,'bore',D,10,6.4,6,body,'subtract');body=await run.cylinder(c,'pawl_seat',C,16,10,4,body);body=await run.cylinder(c,'pin',C,20,6,16.8,body);body=await run.cylinder(c,'cap',C,34.6,10,2,body)
        for n,pt in [('D',D+[10]),('C',C+[28.2]),('Pawl',C+[24])]:await datum(n,pt)
        await finish('rocker',36)
        await run.bar(c,'coupler',A,C,28.4,width=14,thickness=6,bore=6.4);await datum('A',A+[28.2]);await datum('C',C+[28.2]);await finish('coupler',14)
        await new('wheel');body=(await c.call(f'nx_profile_solid RatchetTeeth {run.model/"wheel.csv"} 20.4 7.2 polyline'))['body_id'];body=await run.cylinder(c,'shaft',D,0,6,20.4,body);await datum('Wheel',D+[20.4]);await finish('wheel',211)
        for n,xy,key in [('drive_pawl',C,'Pawl'),('hold_pawl',H,'H')]:
            z=plan['parameters']['pawl_bottom_mm'];thickness=plan['parameters']['pawl_thickness_mm']
            tip=p['drive_tip' if n=='drive_pawl' else 'hold_tip'];dx,dy=tip[0]-xy[0],tip[1]-xy[1];length=math.hypot(dx,dy);normal=[-dy/length,dx/length]
            corners=[[point[i]+sign*radius*normal[i] for i in range(2)] for point,radius,sign in [(xy,5,1),(tip,1.5,1),(tip,1.5,-1),(xy,5,-1)]]
            web=run.model/(n+'_web.csv');web.write_text('\n'.join(','.join(f'{v:.12f}' for v in row) for row in corners))
            await new(n)
            if family.startswith('research_ratchet_'):
                elbow=p['drive_elbow' if n=='drive_pawl' else 'hold_elbow']
                body=await run.cylinder(c,'pawl_pivot_end',xy,z,10,thickness)
                for a,b in [(xy,elbow),(elbow,tip)]:
                    delta=[b[i]-a[i] for i in range(2)]
                    body=await run.shape(c,'hook_web',a+[z+thickness/2],[*delta,0],3,math.hypot(*delta),body,kind='box',height=thickness)
                    body=await run.cylinder(c,'hook_round_end',b,z,3,thickness,body)
            else:
                body=(await c.call(f'nx_profile_solid PawlWeb {web} {z} {thickness} polyline'))['body_id']
                body=await run.cylinder(c,'pawl_pivot_end',xy,z,10,thickness,body);body=await run.cylinder(c,'pawl_tip_end',tip,z,3,thickness,body)
            body=await run.cylinder(c,'bore',xy,z,6.4,thickness,body,'subtract');await datum(key,xy+[24]);run.save(n+'-actual-faces.json',await c.call('nx_list_faces '+body));await finish(n,192)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -')
        for name,a,b,key in [('Input','CRANK','GROUND','O'),('CrankPin','COUPLER','CRANK','A'),('RockerPin','COUPLER','ROCKER','C'),('RockerAxis','ROCKER','GROUND','D'),('WheelAxis','WHEEL','GROUND','Wheel'),('DrivePawl','DRIVE_PAWL','ROCKER','Pawl'),('HoldPawl','HOLD_PAWL','GROUND','H')]:
            first=await joint_references(c,a,key);second=await joint_references(c,b,key)
            for typ,ref in [('touch','point'),('align','axis')]:await c.call(f'nx_constraint {typ} {name}_{typ} {a} {first[ref]} {b} {second[ref]} 0 same')
        state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0;inter=await c.call('nx_interference');run.save('static-interference.json',inter);assert all(x['nx_result']!=1 for x in inter['pairs']);await c.call('nx_save_part');await run.export(c,'assembly');run.meta.update(build_passed=True,analysis_type='dynamics');run.pointer()

if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
