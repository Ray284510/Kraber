private static bool StepTemplateAttempted;
private static string StepTemplateExperiment(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_STEP_TEMPLATE_TRIAL");
 var modes=new[]{"official","template_control_0p01","prepare_test","template_test_0p001","ap242_export","ap242_native_import"};
 IvBox2609Need(args.Length==1&&!StepTemplateAttempted&&modes.Contains(mode),"Isolated STEP template experiment required");
 string root=LabPath(@"models\phase4_step_template_fin_20261112");
 string logs=LabPath(@"logs\phase5\step-template-fin-20261112");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 string official=@"@NX_INSTALL_ROOT@\STEP214UG\step214ugnullnx120_mm.prt";
 string seed=LabPath(@"models\phase4_phase5_staged_20261108_212227\complex_probe_copy.prt");
 string name=mode=="prepare_test"?"template_test_0p001":mode=="ap242_export"?"ap242_source":mode;
 string path=mode=="official"?official:mode=="ap242_native_import"?Path.Combine(root,"ap242_exchange.step"):Path.Combine(root,name+".prt");
 IvBox2609Need(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Exact fixture required");
 string officialHash=IvBox2609Hash(official),hash=IvBox2609Hash(path);
 IvBox2609Need(officialHash==File.ReadAllText(Path.Combine(logs,"official-expected-sha256.txt")).Trim(),"Official template hash changed");
 if(mode=="prepare_test"||mode=="template_control_0p01")IvBox2609Need(hash==officialHash,"Unchanged official copy required");
 if(mode=="ap242_export")IvBox2609Need(hash==IvBox2609Hash(seed),"Independent copy of original geometry required");
 var session=Session.GetSession();
 if(mode=="ap242_native_import") {
  IvBox2609Need(!session.Parts.Cast<BasePart>().Any(),"New part AP242 import requires empty session");
  var input=Path.Combine(root,"ap242_exchange.step");
  var target=Path.Combine(root,"ap242_import.prt");
  IvBox2609Need(File.Exists(input)&&!File.Exists(target)&&(File.GetAttributes(input)&FileAttributes.ReparsePoint)==0,
   "Verified STEP input and new output required");
  string inputHash=IvBox2609Hash(input);
  IvBox2609Need(inputHash==File.ReadAllText(Path.Combine(logs,"ap242-expected-step-sha256.txt")).Trim(),
   "AP242 input hash changed");
  StepTemplateAttempted=true;
  var importer=session.DexManager.CreateStep242Importer();
  try {
   importer.SettingsFile=@"@NX_INSTALL_ROOT@\TRANSLATORS\step242\step242ug.def";
   importer.InputFile=input;
   importer.OutputFile=target;
   importer.ImportTo=Step242Importer.ImportToOption.NewPart;
   importer.ImportToTeamcenter=false;
   importer.FileOpenFlag=false;
   importer.ProcessHoldFlag=true;
   importer.ObjectTypes.Solids=true;
   importer.ObjectTypes.Surfaces=true;
   importer.Commit();
  } finally { importer.Destroy(); }
  bool outputExists=File.Exists(target);
  return Json(new{ok=outputExists&&inputHash==IvBox2609Hash(input)&&officialHash==IvBox2609Hash(official),
   mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,input_sha256=inputHash,output=target,
   output_sha256=outputExists?IvBox2609Hash(target):null,official_sha256=officialHash,save_called=false});
 }
 var uf=NXOpen.UF.UFSession.GetUFSession();
 if(mode=="official") {
  IvBox2609Need(!session.Parts.Cast<BasePart>().Any(),"Official read-only query requires empty session");
  PartLoadStatus load;
  session.Parts.OpenBaseDisplay(path,out load);
  load.Dispose();
 }
 var part=session.Parts.Work as Part;
 IvBox2609Need(part!=null&&part.IsFullyLoaded&&String.Equals(part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&
   session.Parts.Display==part&&session.Parts.Cast<BasePart>().Count()==1&&part.PartUnits==BasePart.Units.Millimeters,
   "Only the specified millimeter work/display fixture may be open");
 StepTemplateAttempted=true;
 int before=Convert.ToInt32(uf.Part.IsModified(part.Tag));
 double tolerance,angle;
 uf.Modl.AskDistanceTolerance(out tolerance);uf.Modl.AskAngleTolerance(out angle);
 var inventory=IvBox2609Inventory(part);
 IvBox2609Need(before==0&&(mode=="ap242_export"?part.Bodies.ToArray().Length==1:part.Bodies.ToArray().Length==0),
  "Saved model state and expected body count required");
 double original=tolerance;
 int modifiedAfterSet=0;
 string output=null,settings=null;
 double? splineTol=null;
 if(mode=="prepare_test") {
  IvBox2609Need(Math.Abs(tolerance-.01)<1e-12,"Expected control tolerance before one-factor change");
  double value=.001;
  uf.Modl.SetDistanceTolerance(ref value);
  uf.Modl.AskDistanceTolerance(out tolerance);
  modifiedAfterSet=Convert.ToInt32(uf.Part.IsModified(part.Tag));
  IvBox2609Need(Math.Abs(tolerance-.001)<1e-12&&inventory.SequenceEqual(IvBox2609Inventory(part)),"Tolerance set/readback or inventory mismatch");
  using(var save=part.Save(BasePart.SaveComponents.False,BasePart.CloseAfterSave.False)) {}
 } else if(mode=="ap242_export") {
  output=Path.Combine(root,"ap242_exchange.step");
  settings=@"@NX_INSTALL_ROOT@\TRANSLATORS\step242\ugstep242.def";
  IvBox2609Need(!File.Exists(output)&&File.Exists(settings),"New STEP output and installed settings required");
  var creator=session.DexManager.CreateStepCreator();
  try {
   creator.SettingsFile=settings;
   creator.ExportAs=StepCreator.ExportAsOption.Ap242;
   creator.InputFile=path;
   creator.OutputFile=output;
   creator.ObjectTypes.Solids=true;
   creator.ObjectTypes.Surfaces=true;
   splineTol=creator.BsplineTol;
   IvBox2609Need(creator.ExportAs==StepCreator.ExportAsOption.Ap242,"AP242 protocol readback failed");
   creator.Commit();
  } finally { creator.Destroy(); }
 }
 int modified=Convert.ToInt32(uf.Part.IsModified(part.Tag));
 double angleAfter;
 uf.Modl.AskAngleTolerance(out angleAfter);
 uf.Modl.AskDistanceTolerance(out tolerance);
 string after=IvBox2609Hash(path);
 bool clean=officialHash==IvBox2609Hash(official)&&modified==0&&angle==angleAfter&&inventory.SequenceEqual(IvBox2609Inventory(part));
 clean=clean&&(mode=="prepare_test"?(Math.Abs(tolerance-.001)<1e-12&&after!=hash):after==hash);
 return Json(new{ok=clean,mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,path=path,
  units=part.PartUnits.ToString(),distance_before_mm=original,distance_after_mm=tolerance,angle_before_deg=angle,
  angle_after_deg=angleAfter,modified_before=before,modified_after_set=modifiedAfterSet,modified_after=modified,
  before_sha256=hash,after_sha256=after,official_sha256=officialHash,output=output,settings=settings,
  export_bspline_tol=splineTol,save_called=mode=="prepare_test"});
}
