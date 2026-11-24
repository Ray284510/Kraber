using System;
using System.IO;
using NXOpen;
using NXOpen.UF;
public class RegenerateAcceptance {
 public static void Main(string[] args) {
  using(var log=new StreamWriter(Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("NX_AI_LAB_ROOT")), @"logs\acceptance-regeneration-csharp.txt"))) {
   try {
    var s=Session.GetSession(); PartLoadStatus status;
    var p=(Part)s.Parts.OpenBaseDisplay(Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("NX_AI_LAB_ROOT")), @"models\acceptance_plate.prt"),out status);status.Dispose();
    UFSession.GetUFSession().Modl.UpdateAllFeatures();log.WriteLine("UF UpdateAllFeatures returned successfully");
    var mark=s.SetUndoMark(Session.MarkVisibility.Invisible,"Regenerate acceptance");
    int errors=s.UpdateManager.DoUpdate(mark);log.WriteLine("DoUpdate error count="+errors);
    Tag[] failed; UFSession.GetUFSession().Modl.AskFeatFailList(out failed);log.WriteLine("Failed feature count="+failed.Length);
    foreach(NXOpen.Features.Feature f in p.Features) {log.WriteLine(f.GetFeatureName()+" errors="+String.Join(";",f.GetFeatureErrorMessages()));}
    var saved=p.Save(BasePart.SaveComponents.False,BasePart.CloseAfterSave.False);saved.Dispose();
    log.WriteLine("SAVED");
   } catch(Exception e) {log.WriteLine(e.ToString());}
  }
 }
}

