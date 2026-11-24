private const string ComplexStepRoot=@"@NX_LAB_ROOT@\models\phase4_complex_step_20261110";
private const string ComplexStepLogs=@"@NX_LAB_ROOT@\logs\phase5\complex-step-20261110";
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\complex-step-20261110\offset-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_offset_fin_geometry(uint face,int refined,string path);
private static bool ComplexStepAttempted;
private static string ComplexStepCapture(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_COMPLEX_STEP_TRIAL");
 IvBox2609Need(args.Length==1&&!ComplexStepAttempted&&(mode=="offset"||mode=="offset_cold"||
     mode=="original"||mode=="step"||mode=="original_compare"||mode=="step_compare"||mode=="original_brep"||mode=="step_brep"),"Dedicated one-shot complex probe required");
 string file=Path.Combine(ComplexStepRoot,mode+".prt");
 bool step=mode.StartsWith("step",StringComparison.Ordinal);
 string source=LabPath(@"models\phase4_phase5_staged_20261108_212227\"+(step?"complex_offline_attempt3_readback.prt":"complex_probe_copy.prt"));
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
    String.Equals(_part.FullPath,file,StringComparison.OrdinalIgnoreCase)&&File.Exists(file),"Exact independent saved copy required");
 for(DirectoryInfo d=new DirectoryInfo(ComplexStepRoot);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse ancestor");
 IvBox2609Need((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display required");
 string hash=IvBox2609Hash(file);
 IvBox2609Need(hash==IvBox2609Hash(source)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Dirty part or hash mismatch");
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"Solid required");
 var before=IvBox2609Inventory(_part);
 var faces=body.GetFaces();
 var edges=body.GetEdges();
 IvBox2609Need(faces.Length==4&&edges.Length==4,"Unexpected complex fixture");
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 ComplexStepAttempted=true;
 if(mode.EndsWith("_brep",StringComparison.Ordinal)) {
  var output=Path.Combine(ComplexStepLogs,mode+"-native.log");
  var edgeTolerances=new List<object>();
  foreach(var edge in edges) {
   double tolerance;uf.Modl.AskEdgeTolerance(edge.Tag,out tolerance);
   edgeTolerances.Add(new{tag=edge.Tag.ToString(),tolerance_mm=tolerance});
  }
  int rc=nx12_offset_fin_geometry(Convert.ToUInt32(body.Tag),1,output);
  if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false});
  bool clean=before.SequenceEqual(IvBox2609Inventory(_part))&&hash==IvBox2609Hash(file)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
  return Json(new{ok=clean,pid=pid,sha256=hash,objects=before.Length,modified=0,edge_tolerances=edgeTolerances,requested_brep_tolerance_mm=.00001});
 }
 if(mode.StartsWith("offset",StringComparison.Ordinal)) {
  var f=faces.Single(x=>x.SolidFaceType.ToString()=="Offset");
  uint tag=Convert.ToUInt32(f.Tag);
  var bounds=new double[4];int u,v;double pu,pv;
  uf.Modl.AskFaceUvMinmax(f.Tag,bounds);uf.Modl.AskFacePeriodicity(f.Tag,out u,out pu,out v,out pv);
  var output=Path.Combine(ComplexStepLogs,mode+"-native.log");
  int rc=nx12_offset_fin_geometry(tag,0,output);
  if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,face=tag,quarantine=true,save_permitted=false});
  bool clean=before.SequenceEqual(IvBox2609Inventory(_part))&&hash==IvBox2609Hash(file)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
  return Json(new{ok=clean,pid=pid,face=tag,kind="Offset",edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),
    uv=bounds,u_status=u,u_period=pu,v_status=v,v_period=pv,sha256=hash,objects=before.Length,modified=0});
 }
 string dataFile=Path.Combine(ComplexStepLogs,mode+"-native.jsonl");
 using(var stream=new FileStream(dataFile,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(stream)) {
  Action<object> emit=x=>writer.WriteLine(Json(x));
  if(mode.EndsWith("_compare",StringComparison.Ordinal))ComplexStepCompare(uf,faces,edges,mode,emit);
  else {
   foreach(var f in faces) {
    double[] uv=new double[4];int us,vs;double up,vp;
    uf.Modl.AskFaceUvMinmax(f.Tag,uv);uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
    int type,sense;double rad,rad2;var point=new double[3];var axis=new double[3];var box=new double[6];
    uf.Modl.AskFaceData(f.Tag,out type,point,axis,box,out rad,out rad2,out sense);
    emit(new{kind="face",tag=f.Tag.ToString(),type=f.SolidFaceType.ToString(),uv=uv,u_status=us,u_period=up,v_status=vs,v_period=vp,sense=sense,edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray()});
    for(int i=0;i<16;++i)for(int j=0;j<10;++j) {
     double u=uv[0]+(uv[1]-uv[0])*(i+.5)/16.0,v=uv[2]+(uv[3]-uv[2])*(j+.5)/10.0;
     var state=new int[1];uf.Modl.AskUvPointsContainment(1,ref u,ref v,f.Tag,state);
     if(state[0]!=1)continue;
     var p=new double[3];var n=new double[3];
     uf.Modl.AskFaceProps(f.Tag,new[]{u,v},p,new double[3],new double[3],new double[3],new double[3],n,new double[2]);
     emit(new{kind="face_sample",tag=f.Tag.ToString(),uv=new[]{u,v},point=p,normal=n});
    }
   }
   foreach(var e in edges) {
    IntPtr h=IntPtr.Zero;
    try {
     uf.Eval.Initialize(e.Tag,out h);var lim=new double[2];uf.Eval.AskLimits(h,lim);
     bool spline;uf.Eval.IsSpline(h,out spline);object splineData=null;
     if(spline) {
      NXOpen.UF.UFEval.Spline sd;uf.Eval.AskSpline(h,out sd);
      int nk,np;double[] knots,poles;
      uf.Eval.AskSplineKnots(h,out nk,out knots);uf.Eval.AskSplineControlPts(h,out np,out poles);
      IvBox2609Need(knots.Length==nk&&poles.Length==np*4&&nk==sd.num_knots&&np==sd.num_control,"Edge spline arrays");
      splineData=new{order=sd.order,knots=knots,poles=poles,rational=sd.is_rational,periodic=sd.is_periodic};
     }
     emit(new{kind="edge",tag=e.Tag.ToString(),type=e.SolidEdgeType.ToString(),limits=lim,spline=splineData,faces=e.GetFaces().Select(f=>f.Tag.ToString()).ToArray()});
     for(int i=0;i<=128;++i) {
      double t=lim[0]+(lim[1]-lim[0])*i/128.0;var p=new double[3];var d=new double[3];
      uf.Eval.Evaluate(h,1,t,p,d);
      emit(new{kind="edge_sample",tag=e.Tag.ToString(),i=i,t=t,point=p,tangent=d});
     }
    } finally {if(h!=IntPtr.Zero)uf.Eval.Free(h);}
   }
  }
  writer.Flush();stream.Flush(true);
 }
 bool unchanged=hash==IvBox2609Hash(file)&&before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=unchanged,pid=pid,sha256=hash,faces=faces.Length,edges=edges.Length,objects=before.Length,modified=0,data=dataFile});
}
private static void ComplexStepCompare(NXOpen.UF.UFSession uf,Face[] faces,Edge[] edges,string mode,Action<object> emit) {
 var input=Path.Combine(ComplexStepLogs,mode+"-points.tsv");
 var lines=File.ReadAllLines(input);
 IvBox2609Need(lines.Length>0&&lines.Length<=1500,"Comparison sample bounds");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<Tag,IntPtr>();
 try {
  foreach(var e in edges){IntPtr h;uf.Eval.Initialize(e.Tag,out h);handles.Add(e.Tag,h);}
  foreach(string line in lines) {
   var a=line.Split('\t');
   IvBox2609Need(a.Length==10&&(a[0]=="face"||a[0]=="edge"),"Malformed comparison input");
   var p=new double[]{Double.Parse(a[3],culture),Double.Parse(a[4],culture),Double.Parse(a[5],culture)};
   if(a[0]=="edge") {
    foreach(var e in edges) {
     var h=handles[e.Tag];double t;var q=new double[3];var d=new double[3];
     uf.Eval.EvaluateClosestPoint(h,p,out t,q);
     uf.Eval.Evaluate(h,1,t,q,d);
     emit(new{kind="edge",source=a[1],sample=a[2],target=e.Tag.ToString(),parameter=t,point=q,tangent=d});
    }
   } else {
    foreach(var f in faces) {
     var uv=new double[2];var q=new double[3];var n=new double[3];var bounds=new double[4];
     uf.Modl.AskFaceParm2(f.Tag,p,uv,q);uf.Modl.AskFaceUvMinmax(f.Tag,bounds);
     int status;
     uf.Modl.AskPointContainment(q,f.Tag,out status);
     bool domain=uv[0]>=bounds[0]-1e-10&&uv[0]<=bounds[1]+1e-10&&uv[1]>=bounds[2]-1e-10&&uv[1]<=bounds[3]+1e-10;
     if(domain) {
      uv[0]=Math.Min(bounds[1],Math.Max(bounds[0],uv[0]));uv[1]=Math.Min(bounds[3],Math.Max(bounds[2],uv[1]));
      uf.Modl.AskFaceProps(f.Tag,uv,q,new double[3],new double[3],new double[3],new double[3],n,new double[2]);
     }
     emit(new{kind="face",source=a[1],sample=a[2],target=f.Tag.ToString(),uv=uv,point=q,normal=n,trim_status=status,in_param_range=domain});
    }
   }
  }
 } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
}
