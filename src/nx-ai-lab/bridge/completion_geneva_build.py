import asyncio,math,sys
from phase4_common import Run,Client
from mechanism_agent.completion_geneva import design

from completion_datum_references import joint_references

async def main(family='completion_geneva'):
    assert family=='completion_geneva' or family.startswith(('finalization_geneva_','research_geneva_'))
    plan,profiles=design();p=plan['parameters'];center=p['center_distance_mm'];r=p['crank_radius_mm'];outer=p['wheel_outer_radius_mm']
    if family.startswith('research_geneva_'):
        plan['drive'].update(duration_s=8,steps=480)
        plan['repair_limit']=2
        plan['construction']='Research route D: existing real six-slot Geneva geometry, ideal nominal indexed motion using geometry-derived output profile synchronized with the constant input clock. No unilateral contact force model, no contact-force or load validation. Two full cycles; all physical body pairs retain zero-positive-volume gate.'
        plan['tolerances'].update(independent_collinearity_error_mm=.01,cycle_index_error_deg=.01,dwell_angle_range_deg=.01)
    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');plan['family']=family;run=Run(family,plan)
        for name,points in profiles.items():(run.model/(name+'.csv')).write_text('\n'.join(','.join(f'{x:.12f}' for x in v) for v in points))
        async def new(n):await c.call('nx_create_part '+str(run.model/(n+'.prt')));await c.call('nx_application modeling')
        async def finish(n,color):run.save(n+'-density.json',await c.call('nx_body_density 7800'));await run.finish(c,n,color)
        await new('ground');left=-r-15;right=center+outer+10;body=await run.shape(c,'base',[(left+right)/2,0,0],[0,0,1],right-left,5,kind='box',height=2*outer+20)
        for x,top in [(0,13.6),(center,20.)]:
            body=await run.cylinder(c,'bearing',[x,0],5,18,top-5,body);body=await run.cylinder(c,'bore',[x,0],0,6.4,top,body,'subtract')
        for name,x,z in [('Input',0,14),('Output',center,20.4)]:await c.call(f'nx_joint_reference {name} {x} 0 {z} 0 0 1')
        await finish('ground',103)
        await new('input');body=await run.cylinder(c,'crank_plate',[0,0],14,2*(r+4),6);body=await run.cylinder(c,'shaft',[0,0],0,6,14,body)
        locking=await run.cylinder(c,'locking_disk',[0,0],19.8,2*p['locking_disk_radius_mm'],8.2)
        locking=await run.cylinder(c,'circular_relief',[-r,0],19.8,2*p['circular_relief_radius_mm'],8.2,locking,'subtract')
        body=(await c.call(f'nx_unite {body} {locking}'))['body_id']
        body=await run.cylinder(c,'pin',[-r,0],19.8,2*p['pin_radius_mm'],10.6,body);body=await run.cylinder(c,'pin_head',[-r,0],30.2,10,2,body);await c.call('nx_joint_reference Input 0 0 14 0 0 1');await finish('input',186)
        await new('output');body=await run.cylinder(c,'wheel_blank',[center,0],20.4,2*outer,7.2)
        half_width=p['pin_radius_mm']+p['radial_clearance_mm']
        for i in range(p['slots']):
            angle=(2*i+1)*math.pi/p['slots'];axis=[math.cos(angle),math.sin(angle),0];start=[center+(center-r)*axis[0],(center-r)*axis[1]]
            body=await run.cylinder(c,'slot_root',start,20.4,2*half_width,7.2,body,'subtract')
            body=await run.shape(c,'radial_slot',start+[24],axis,2*half_width,outer+5-(center-r),body,'subtract',kind='box',height=7.2)
            arc_angle=angle+math.pi/p['slots'];arc_center=[center+center*math.cos(arc_angle),center*math.sin(arc_angle)]
            body=await run.cylinder(c,'locking_arc',arc_center,20.4,2*(p['locking_disk_radius_mm']+p['radial_clearance_mm']),7.2,body,'subtract')
        body=await run.cylinder(c,'shaft',[center,0],0,6,20.4,body);await c.call(f'nx_joint_reference Output {center} 0 20.4 0 0 1');await finish('output',211)
        await run.add_all(c);await c.call('nx_constraint fix GroundFixed GROUND component -')
        for n in ['Input','Output']:
            first=await joint_references(c,n.upper(),n);second=await joint_references(c,'GROUND',n)
            for typ,key in [('touch','point'),('align','axis')]:await c.call(f'nx_constraint {typ} {n}_{typ} {n.upper()} {first[key]} GROUND {second[key]} 0 same')
        state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0;inter=await c.call('nx_interference');run.save('static-interference.json',inter);assert all(x['nx_result']!=1 for x in inter['pairs']);await c.call('nx_save_part');await run.export(c,'assembly');run.meta.update(build_passed=True,analysis_type='dynamics');run.pointer()

if __name__=='__main__':asyncio.run(main(*sys.argv[1:]))
