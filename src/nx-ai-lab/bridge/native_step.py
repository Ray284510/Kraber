"""NX 12's own STEP translator; explicit output avoids broken online import."""
from lab_paths import lab_path
import datetime,json,os,subprocess,uuid
from pathlib import Path
ROOT=lab_path('')


def import_step(command,call):
    if not os.environ.get('UGII_BASE_DIR'):
        raise ValueError('UGII_BASE_DIR must point to the NX installation')
    NX=Path(os.environ['UGII_BASE_DIR']).resolve(strict=True)
    args=command.split()
    if len(args)!=3:raise ValueError('nx_import_step INPUT_STEP OUTPUT_PRT')
    src,out=(Path(p).resolve() for p in args[1:])
    if any(p.is_relative_to(ROOT/'models/phase4_p13_smoke_e80a1f33') for p in (src,out)):
        raise PermissionError('External STEP conversion cannot access the fixed Smoke fixture')
    for p in (src,out):
        if not p.is_relative_to((ROOT/'models').resolve()):raise ValueError('Only experimental model paths allowed')
    if src.suffix.lower() not in ('.step','.stp') or not src.is_file():raise ValueError('STEP input not found')
    if out.suffix.lower()!='.prt' or out.exists():raise ValueError('New PRT output required; never overwrite')
    status=call('nx_status')
    if not status.get('ok') or status.get('part'):raise ValueError('Close experimental work part before STEP import')
    stamp=datetime.datetime.now().strftime('%Y%m%d_%H%M%S')+'_'+uuid.uuid4().hex[:8]
    logs=ROOT/'logs'/('step_import_'+stamp);logs.mkdir()
    settings=(NX/'STEP214UG/step214ug.def').read_text().replace('CNV_MODE = On-Line','CNV_MODE = Off-Line').replace('ASSEMBLY_MAP = Off','ASSEMBLY_MAP = On')
    for name in ('step214ugnullnx120_mm.prt','step214ugnullnx120_in.prt'):settings=settings.replace(name,str(NX/'STEP214UG'/name))
    defs=logs/'import.def';defs.write_text(settings)
    env=dict(os.environ)
    env.update(UGII_BASE_DIR=str(NX),UGII_ROOT_DIR=str(NX/'UGII'),UGII_TMP_DIR=str(ROOT/'tmp'),TEMP=str(ROOT/'tmp'),TMP=str(ROOT/'tmp'),STEP214UG_DIR=str(NX/'STEP214UG')+'\\',ROSE=str(NX/'STEP214UG')+'\\',ROSE_DB=str(NX/'STEP214UG')+'\\')
    env['PATH']=str(NX/'NXBIN')+';'+env['PATH']
    argv=[str(NX/'NXBIN/step214ug.exe'),str(src),'o='+str(out),'d='+str(defs),'l='+str(logs/'translator.log')]
    with (logs/'console.txt').open('wb') as console:
        p=subprocess.Popen(argv,env=env,cwd=str(ROOT/'tmp'),stdout=console,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW)
        try:rc=p.wait(timeout=45)
        except subprocess.TimeoutExpired:
            p.kill();p.wait();rc=-1
    (logs/'execution.json').write_text(json.dumps({'argv':argv,'exit_code':rc,'output_exists':out.exists()},indent=2))
    if rc!=0 or not out.is_file():return {'ok':False,'translator_exit_code':rc,'logs':str(logs),'retry_safe':False}
    opened=call('nx_open_part '+str(out))
    if not opened.get('ok'):return opened
    result=call('nx_inspect');result['step_translator_logs']=str(logs);result['step_source']=str(src)
    return result
