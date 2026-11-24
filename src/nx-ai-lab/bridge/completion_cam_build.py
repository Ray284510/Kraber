import asyncio,json,sys
import numpy as np
from phase4_common import Run,Client
from completion_helpers import rectangle,datum
from mechanism_agent.completion_cam import design,P,R,E

async def main(kind):
    p=design(kind);osc='oscillating' in kind;roller='roller' in kind;point=np.array(p['initial']['point']);refs={}

    async with Client() as c:
        assert not (await c.call('nx_status')).get('part');run=Run('completion_'+kind,p)
        from completion_cam_law import main as write_law
        write_law(kind)
        async def new(n):await c.call('nx_create_part '+str(run.model/(n+'.prt')))
        async def finish(n,color):await run.finish(c,n,color)
        await new('ground');await c.call('nx_application modeling');body=await rectangle(run,c,'base',[0,0],[1,0],-70,65,150,0,4,offset=20)
        body=await run.cylinder(c,'cam_bearing',[0,0],4,18,15.6,body);body=await run.cylinder(c,'cam_bore',[0,0],0,8.4,19.6,body,'subtract')
        await datum(c,refs,'ground','CamAxis',[0,0,24])
        if osc:
            body=await run.cylinder(c,'follower_bearing',P,4,18,26.4,body);body=await run.cylinder(c,'follower_bore',P,0,6.4,30.4,body,'subtract');await datum(c,refs,'ground','FollowerJoint',[*P,33.8])
        else:
            for x in [-8.2,8.2]:body=await rectangle(run,c,'guide_post',[x,70],[1,0],-3,3,30,4,32.6,body)
            # Finite side rails plus a floor and roof capture the follower in Z.
            body=await rectangle(run,c,'guide_floor',[0,70],[1,0],-5.2,5.2,30,28.4,2,body)
            body=await rectangle(run,c,'guide_roof',[0,70],[1,0],-11.2,11.2,30,36.6,2,body)
        await finish('ground',103)
        await new('cam');body=await run.cylinder(c,'exact_eccentric_disk',[E,0],20,2*R,8);body=await run.cylinder(c,'shaft_bore',[0,0],20,8.4,8,body,'subtract');await datum(c,refs,'cam','CamAxis',[0,0,24]);await finish('cam',186)
        await new('camshaft');body=await run.cylinder(c,'shaft',[0,0],-.2,8,28.2);body=await run.cylinder(c,'head',[0,0],-2.2,12,2,body);await finish('camshaft',192)
        if osc:
            await new('follower');u=(point-P)/np.linalg.norm(point-P)
            body=await run.cylinder(c,'pivot_eye',P,30.8,12,6);body=await rectangle(run,c,'arm_web',P,u,0,45,12,30.8,6,body);body=await run.cylinder(c,'solid_tip_eye',point,30.8,12,6,body)
            body=await run.cylinder(c,'ground_pivot_bore',P,30.8,6.4,6,body,'subtract')
        else:
            await new('follower');body=await rectangle(run,c,'slide',[0,70],[1,0],-5,5,40,30.8,5.6)
            body=await rectangle(run,c,'stem',point,[0,1],0,70-point[1],6,30.8,5.6,body)
        if roller:
            body=await run.cylinder(c,'roller_pin',point,20.2,4,10.6,body)
        else:
            # Actual sharp wedge, not an abstract marker. Narrow flanks remain outside the convex disk.
            normal=np.array(p['initial']['normal']);tangent=np.array([-normal[1],normal[0]])
            points=[point,point+12*normal+3*tangent,point+12*normal-3*tangent];file=run.model/'actual_tip.csv';file.write_text('\n'.join(','.join(map(str,q)) for q in points))
            res=await c.call(f'nx_profile_boolean ActualSharpTip {file} 20 16.4 unite {body} polyline');body=res['body_id']
        if osc:await datum(c,refs,'follower','FollowerJoint',[*P,33.8])
        if roller:await datum(c,refs,'follower','RollerAxis',[*point,24])
        await finish('follower',36)
        if roller:
            await new('roller');body=await run.cylinder(c,'roller',point,20,12,8);body=await run.cylinder(c,'pin_bore',point,20,4.4,8,body,'subtract');await datum(c,refs,'roller','RollerAxis',[*point,24]);await finish('roller',211)
        if osc:
            await new('follower_shaft');body=await run.cylinder(c,'shaft',P,-.2,6,36.9);body=await run.cylinder(c,'head',P,-2.2,9,2,body);await finish('follower_shaft',192)
        await run.add_all(c);await c.call('nx_constraint fix FixedGround GROUND component -')
        pairs=[('CamAxis','cam','ground')]+([('FollowerJoint','follower','ground')] if osc else [])+([('RollerAxis','roller','follower')] if roller else [])
        for key,a,b in pairs:
            for typ,field,prefix in [('touch','point_name','point:'),('align','curve_name','curve:')]:await c.call(f'nx_constraint {typ} {key}_{typ} {a.upper()} {prefix+refs[a][key][field]} {b.upper()} {prefix+refs[b][key][field]} 0 same')
        if not osc:
            fg=await c.call('nx_component_geometry FOLLOWER');gg=await c.call('nx_component_geometry GROUND')
            def plane(g,axis,sign,coordinate):
                face=min((f for f in g['faces'] if f['kind']=='Planar' and f['direction'][axis]*sign>.99),key=lambda f:abs(f['point_mm'][axis]-coordinate));assert abs(face['point_mm'][axis]-coordinate)<.001;return face['reference']
            await c.call(f'nx_constraint distance GuideSide FOLLOWER {plane(fg,0,1,5)} GROUND {plane(gg,0,-1,5.2)} 0.2 opposite')
            await c.call(f'nx_constraint distance GuideFloor FOLLOWER {plane(fg,2,-1,30.8)} GROUND {plane(gg,2,1,30.4)} 0.4 opposite')
        await c.call('nx_constraint bond CamShaftBond CAMSHAFT component CAM component 0 same')
        if osc:await c.call('nx_constraint bond PivotShaftBond FOLLOWER_SHAFT component GROUND component 0 same')
        state=await c.call('nx_assembly_state');run.save('assembly-state.json',state);assert state['unsatisfied_constraint_count']==0
        run.save('static-interference.json',await c.call('nx_interference'))
        await c.call('nx_save_part');await run.export(c,'assembly');run.meta.update(build_passed=True,contact_validation_pending=True);run.pointer()
if __name__=='__main__':asyncio.run(main(sys.argv[1]))
