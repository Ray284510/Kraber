private static bool StepDriftAttempted;
private static string StepDriftCapture(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_STEP_DRIFT_TRIAL");
 bool template=mode!=null&&mode.StartsWith("template_capture_",StringComparison.Ordinal);
 if(template)mode=mode.Substring("template_capture_".Length);
 var valid=template?new[]{"import_control","import_test","ap242_import"}:
  new[]{"original_tol","default","approx_001","approx_00005","sew_on"};
 IvBox2609Need(args.Length==1&&!StepDriftAttempted&&!String.IsNullOrEmpty(mode),"Isolated STEP capture required");
 bool cold=mode.EndsWith("_cold",StringComparison.Ordinal);
 string name=cold?mode.Substring(0,mode.Length-5):mode;
 IvBox2609Need(valid.Contains(name),"Unexpected STEP fixture");
 string root=LabPath(template?@"models\phase4_step_template_fin_20261112":@"models\phase4_step_import_drift_20261112");
 string logs=LabPath(template?@"logs\phase5\step-template-fin-20261112":@"logs\phase5\step-import-drift-20261112");
 string path=Path.Combine(root,name+".prt");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path),"Exact saved fixture required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display required");
 string hash=IvBox2609Hash(path);
 IvBox2609Need(hash==File.ReadAllText(Path.Combine(logs,name+"-expected-sha256.txt")).Trim()&&
  Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Fixture provenance or dirty state");
 var inventory=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 var faces=body.GetFaces();var edges=body.GetEdges();
 IvBox2609Need(body.IsSolidBody&&faces.Length==4&&edges.Length==4,"Expected four-face/four-edge solid");
 int faults;int[] faultCodes;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out faultCodes,out faulty);
 double modelTolerance;uf.Modl.AskDistanceTolerance(out modelTolerance);
 StepDriftAttempted=true;
 string output=Path.Combine(logs,mode+"-native.jsonl");
 using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  Action<object> emit=x=>writer.WriteLine(Json(x));
  emit(new{kind="solid",modeling_tolerance_mm=modelTolerance,consistency_faults=faults,fault_codes=faultCodes});
  foreach(var f in faces) {
   var uv=new double[4];int us,vs;double up,vp;
   uf.Modl.AskFaceUvMinmax(f.Tag,uv);uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
   var features=new List<object>();int faceFeatureError=0;
   if(name=="original_tol") {
    try {
     Tag[] tags;uf.Modl.AskFaceFeats(f.Tag,out tags);
     foreach(var feature in tags) {
      try {
       bool exists;double tolerance;
       uf.Modl.AskFeatTolerance(feature,out exists,out tolerance);
       var feat=_part.Features.ToArray().FirstOrDefault(x=>x.Tag==feature);
       features.Add(new{tag=feature.ToString(),name=feat==null?null:feat.Name,feature_type=feat==null?null:feat.FeatureType,
        exists=exists,tolerance_mm=exists?(double?)tolerance:null});
      } catch(NXException ex) {features.Add(new{tag=feature.ToString(),error=ex.ErrorCode});}
     }
    } catch(NXException ex) {faceFeatureError=ex.ErrorCode;}
   }
   int type,sense;double rad,rad2;var point=new double[3];var axis=new double[3];var box=new double[6];
   uf.Modl.AskFaceData(f.Tag,out type,point,axis,box,out rad,out rad2,out sense);
   emit(new{kind="face",tag=f.Tag.ToString(),type=f.SolidFaceType.ToString(),uv=uv,sense=sense,box=box,
    u_status=us,u_period=up,v_status=vs,v_period=vp,features=features,face_feature_error=faceFeatureError,
    edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray()});
  }
  string[] requests=name=="original_tol"?new string[0]:File.ReadAllLines(Path.Combine(logs,name=="ap242_import"?"ap242-samples.tsv":"step-samples.tsv"));
  IvBox2609Need(requests.Length<=4000,"Bounded source witness set");
  foreach(var edge in edges) {
   double tolerance;uf.Modl.AskEdgeTolerance(edge.Tag,out tolerance);
   var adjacent=edge.GetFaces();
   if(name=="original_tol") {
    emit(new{kind="edge",tag=edge.Tag.ToString(),tolerance_mm=tolerance,faces=adjacent.Select(x=>x.Tag.ToString()).ToArray()});
    continue;
   }
   IntPtr evaluator=IntPtr.Zero;
   try {
    uf.Eval.Initialize(edge.Tag,out evaluator);var limits=new double[2];uf.Eval.AskLimits(evaluator,limits);
    bool spline;uf.Eval.IsSpline(evaluator,out spline);object splineData=null;
    if(spline) {
     NXOpen.UF.UFEval.Spline data;uf.Eval.AskSpline(evaluator,out data);
     int nk,np;double[] knots,poles;
     uf.Eval.AskSplineKnots(evaluator,out nk,out knots);uf.Eval.AskSplineControlPts(evaluator,out np,out poles);
     IvBox2609Need(knots.Length==nk&&poles.Length==4*np,"Native edge spline array size");
     splineData=new{order=data.order,knots=knots,poles=poles,rational=data.is_rational,periodic=data.is_periodic};
    }
    emit(new{kind="edge",tag=edge.Tag.ToString(),tolerance_mm=tolerance,limits=limits,spline=splineData,
      faces=adjacent.Select(x=>x.Tag.ToString()).ToArray()});
    for(int i=0;i<=512;++i) {
     double parameter=limits[0]+(limits[1]-limits[0])*i/512.0;
     var point=new double[3];var derivatives=new double[6];
     uf.Eval.Evaluate(evaluator,2,parameter,point,derivatives);
     var supports=new List<object>();
     foreach(var face in adjacent) {
      var uv=new double[2];var q=new double[3];
      uf.Modl.AskFaceParm2(face.Tag,point,uv,q);
      int state;uf.Modl.AskPointContainment(q,face.Tag,out state);
      supports.Add(new{face=face.Tag.ToString(),uv=uv,point=q,trim_status=state});
     }
     emit(new{kind="edge_sample",tag=edge.Tag.ToString(),i=i,t=parameter,point=point,derivatives=derivatives,supports=supports});
    }
    for(int i=0;i<requests.Length;++i) {
     var xyz=requests[i].Split('\t').Select(x=>Double.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
     IvBox2609Need(xyz.Length==3,"Expected XYZ witness");
     double parameter;var q=new double[3];var derivatives=new double[6];
     uf.Eval.EvaluateClosestPoint(evaluator,xyz,out parameter,q);
     uf.Eval.Evaluate(evaluator,2,parameter,q,derivatives);
     emit(new{kind="file_projection",tag=edge.Tag.ToString(),i=i,t=parameter,point=q,derivatives=derivatives});
    }
   } finally {if(evaluator!=IntPtr.Zero)uf.Eval.Free(evaluator);}
  }
  writer.Flush();file.Flush(true);
 }
 int? brepCode=null;
 if(name!="original_tol"&&!cold) {
  int rc=nx12_offset_fin_geometry(Convert.ToUInt32(body.Tag),1,Path.Combine(logs,mode+"-brep.log"));
  brepCode=rc;
  if(rc!=0)return Json(new{ok=false,rc=rc,quarantine=true,save_permitted=false});
 }
 bool clean=hash==IvBox2609Hash(path)&&inventory.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=clean,sha256=hash,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
  objects=inventory.Length,consistency_faults=faults,brep_code=brepCode,modified=0});
}
