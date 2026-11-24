"""Real five-part NX demonstration of durable recovery, not the surfacing deliverable."""
import argparse,asyncio,json,shutil,sys
from pathlib import Path
from .nx_worker import Worker,ROOT,Client

def write(path,value):path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')

def valid_part(result):
    assert result.get('ok') and len(result['bodies'])==1,result
    assert result['bodies'][0]['solid'] and result['bodies'][0]['volume_mm3']>0
    assert result['bodies'][0]['consistency_fault_count']==0
    assert result['failed_feature_count']==0 and all(not f['errors'] for f in result['features'])
    assert all(s['degrees_of_freedom']==0 for s in result['sketches'])

async def main(interrupt=False):
    location=json.loads((ROOT/'logs/phase5/current-task.json').read_text());out=Path(location['folder'])
    spec=json.loads((out/'task_spec.json').read_text(encoding='utf-8'));models=Path(spec['models'])
    if (out/'acceptance.json').exists():
        print(json.dumps({'already_finished':True,'no_nx_commands_sent':True,'evidence':str(out/'acceptance.json')}))
        return
    async with Client() as bootstrap:
        session=await bootstrap.tool('nx_session_parts',{})
        if session['parts'] and not all(Path(p['path']).parent==models for p in session['parts']):
            expected=ROOT/'models/phase4_migration_new_plate_20261108_164914/mounting_plate.prt'
            assert len(session['parts'])==1 and Path(session['parts'][0]['path'])==expected and not session['any_modified'],'Unrelated or unsaved user model; stop'
            closed=await bootstrap.call('nx_close_part 0');write(out/'prior-saved-lab-part-close.json',{'session':session,'result':closed,'operation_id':'bootstrap.close_saved_migration_plate'})
    async with Worker(out) as w:
        w.recover_receipts()
        session=await w.observe('nx_session_parts');w.store.switch_provider('codex',session)
        write(out/('resume-session.json' if not interrupt else 'initial-session.json'),session)
        if not interrupt:
            inspect=await w.observe('nx_inspect') if session['parts'] else None
            if inspect:write(out/'actual-resume-inspection.json',inspect)
        recipes={
            'base':'nx_shape base_outline box 0 0 0 0 0 1 100 60 8 create -',
            'pedestal':'nx_shape locating_spigot cylinder 0 0 0 0 0 1 12 1 8 create -',
            'orb':'nx_sphere decorative_orb 0 0 46 36 create -',
            'left_fin':'nx_shape left_ornament box -35 0 8 0 0 1 10 6 40 create -',
            'right_pylon':'nx_shape right_ornament cylinder 35 0 8 0 0 1 8 1 40 create -'}
        part_results={}
        for name in spec['parts']:
            done=w.store.db.execute("SELECT state FROM operations WHERE id=?",(name+'.close',)).fetchone()
            if done and done['state']=='succeeded':continue
            path=models/(name+'.prt')
            await w.execute(name+'.create','nx_create_part '+str(path))
            shape=await w.execute(name+'.shape',recipes[name],interrupt_after_receipt=interrupt and name=='base')
            # Use the real creation receipt's bridge ID, not a display name from ListBodies.
            original_session=json.loads((out/'initial-session.json').read_text())
            assert session['process_id']==original_session['process_id'],'NX restarted: reconcile body references before resuming this demonstration'
            if shape.get('receipt_kind')!='fresh_dispatch' or not shape.get('handle_binding'):
                raise RuntimeError('Fresh bound body receipt required to continue this part')
            body=shape['body_id']
            if name=='base':await w.execute(name+'.bore.repair1',f'nx_hole {body} 0 0 12.4 8 0',source_receipt=shape)
            if name=='pedestal':await w.execute(name+'.shoulder',f'nx_shape pedestal_shoulder cylinder 0 0 8 0 0 1 24 1 20 create {body}',source_receipt=shape)
            await w.execute(name+'.view','nx_fit_view')
            actual=await w.observe('nx_inspect');valid_part(actual);part_results[name]=actual;write(out/(name+'-inspection.json'),actual)
            await w.execute(name+'.save','nx_save_part')
            await w.execute(name+'.close','nx_close_part 0')
            w.store.checkpoint('Saved and inspected '+name,[str(models/(n+'.prt')) for n in spec['parts'] if (models/(n+'.prt')).exists()])
        for name in spec['parts']:
            await w.execute(name+'.verify_open','nx_open_part '+str(models/(name+'.prt')))
            actual=await w.observe('nx_inspect');valid_part(actual);write(out/(name+'-reopened.json'),actual)
            before=json.loads((out/(name+'-inspection.json')).read_text());assert abs(actual['bodies'][0]['volume_mm3']-before['bodies'][0]['volume_mm3'])<1e-6
            await w.execute(name+'.verify_close','nx_close_part 0')
        assembly=models/'display_fixture.prt'
        await w.execute('assembly.create','nx_create_part '+str(assembly))
        for name in spec['parts']:await w.execute('assembly.add.'+name,f'nx_add_component {models/(name+".prt")} {name.upper()} 0 0 0')
        for name in ['base','orb','left_fin','right_pylon']:await w.execute('assembly.fix.'+name,f'nx_constraint fix fix_{name} {name.upper()} component -')
        gbase=await w.observe('nx_component_geometry BASE');gped=await w.observe('nx_component_geometry PEDESTAL')
        axisbase=next(f['reference'] for f in gbase['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-6.2)<1e-5)
        axisped=next(f['reference'] for f in gped['faces'] if f['kind']=='Cylindrical' and abs(f['radius_mm']-6)<1e-5)
        top=next(f['reference'] for f in gbase['faces'] if f['kind']=='Planar' and f['direction'][2]>.9 and abs(f['point_mm'][2]-8)<1e-6)
        bottom=next(f['reference'] for f in gped['faces'] if f['kind']=='Planar' and f['direction'][2]<-.9 and abs(f['point_mm'][2]-8)<1e-6)
        await w.execute('assembly.axis',f'nx_constraint axis locating_axis PEDESTAL {axisped} BASE {axisbase} 0 same')
        await w.execute('assembly.seat',f'nx_constraint distance seat PEDESTAL {bottom} BASE {top} 0 opposite')
        await w.execute('assembly.before_fault.save','nx_save_part');shutil.copy2(assembly,models/'before_constraint_fault.prt')
        failure=await w.execute('fault.conflicting_seat',f'nx_constraint distance conflicting_seat PEDESTAL {bottom} BASE {top} 2 opposite',allow_failure=True)
        failed=await w.observe('nx_assembly_state');assert failed['unsatisfied_constraint_count']>0
        await w.execute('fault.snapshot.save','nx_save_part');shutil.copy2(assembly,models/'failed_constraint_state.prt')
        fixed=await w.execute('repair.seat_value','nx_constraint_value conflicting_seat 0')
        repaired=await w.observe('nx_assembly_state');assert repaired['unsatisfied_constraint_count']==0
        write(out/'actual-constraint-repair.json',{'fault_injection':True,'native_failure':failure,'failed_state':failed,'repair':fixed,'repaired_state':repaired,'rounds':1,'max_rounds':3,'reason':'Conflicting native axial distance requirements, corrected dimension from 2 to 0 mm; no component translation.'})
        for constraint in ['conflicting_seat','seat','locating_axis']:
            await w.execute('assembly.suppress.'+constraint,'nx_constraint_suppress '+constraint+' 1')
        await w.execute('assembly.fix.pedestal','nx_constraint fix fix_pedestal PEDESTAL component -')
        state=await w.observe('nx_assembly_state');assert state['unsatisfied_constraint_count']==0 and state['all_prototypes_fully_loaded']
        assert len(state['components'])==5
        assert all(c['translation_dof']==0 and c['rotation_dof']==0 and c['dof_result']=='Success' for c in state['components'])
        collision=await w.observe('nx_interference');assert len(collision['pairs'])==10 and all(p['nx_result'] in (2,3) for p in collision['pairs']),collision
        await w.execute('assembly.view','nx_fit_view');await w.execute('assembly.save','nx_save_part')
        step=models/'display_fixture.step';await w.execute('assembly.export_step','nx_export_step '+str(step))
        for _ in range(90):
            if step.exists() and 'END-ISO-10303-21;' in step.read_text(errors='replace')[-200:]:break
            await asyncio.sleep(1)
        else:raise RuntimeError('STEP not complete')
        await w.execute('assembly.close','nx_close_part 0');await w.execute('assembly.reopen','nx_open_part '+str(assembly))
        reopened=await w.observe('nx_assembly_state');assert len(reopened['components'])==5 and reopened['unsatisfied_constraint_count']==0
        await w.execute('assembly.final_view','nx_fit_view')
        checkpoint=w.store.checkpoint('Five-part NX assembly, real constraint repair and controlled interruption recovery complete',[str(assembly),str(step)])
        duplicates=w.store.db.execute("SELECT COUNT(*) FROM events WHERE kind='duplicate_not_executed'").fetchone()[0]
        reconciled=w.store.db.execute("SELECT COUNT(*) FROM events WHERE kind='receipt_reconciliation'").fetchone()[0]
        assert duplicates>=2 and reconciled==1 and not checkpoint['unresolved']
        write(out/'acceptance.json',{'real_nx_checkpoint_recovery_passed':True,'provider':'codex_fallback','dsh_long_task_verified':False,
              'api_generation_requests_this_task':0,'five_parts_reopened':True,'assembly':state,'reopened':reopened,'collision':collision,
              'deduplicated_operations':duplicates,'receipt_reconciliations':reconciled,'surface_or_printing_acceptance':False,'models':str(models)})
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--interrupt',action='store_true');args=p.parse_args();asyncio.run(main(args.interrupt))
