from os import environ
from pathlib import Path
import NXOpen, NXOpen.UF, json, traceback
s=NXOpen.Session.GetSession(); uf=NXOpen.UF.UFSession.GetUFSession()
result={}
try:
    p, status=s.Parts.OpenBaseDisplay(str(Path(environ["NX_AI_LAB_ROOT"]) / 'models/acceptance_plate.prt'))
    status.Dispose()
    result['before']=[{'name':f.GetFeatureName(),'errors':list(f.GetFeatureErrorMessages())} for f in p.Features]
    uf.Modl.UpdateAllFeatures()
    mark=s.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible,'Validation regeneration')
    result['update_errors']=s.UpdateManager.DoUpdate(mark)
    result['failed_features']=[str(t) for t in uf.Modl.AskFeatFailList()]
    result['after']=[{'name':f.GetFeatureName(),'errors':list(f.GetFeatureErrorMessages())} for f in p.Features]
    p.Save(NXOpen.BasePart.SaveComponents.FalseValue,NXOpen.BasePart.CloseAfterSave.FalseValue)
    result['ok']=True
except Exception as e:
    result['ok']=False;result['error']=str(e);result['traceback']=traceback.format_exc()
with open(str(Path(environ["NX_AI_LAB_ROOT"]) / 'logs/acceptance-regeneration.json'),'w') as f:json.dump(result,f,indent=2)
