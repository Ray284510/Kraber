private const string PeriodicHoleRoot=@"@NX_LAB_ROOT@\models\phase4_periodic_hole_20261110";
private const string PeriodicHoleLogs=@"@NX_LAB_ROOT@\logs\phase5\periodic-hole-20261110";
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\periodic-hole-20261110\periodic-hole-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_periodic_hole_prepare(uint part,int count,string path);
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\periodic-hole-20261110\periodic-hole-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_periodic_hole_surface(uint part,string path);
private static int PeriodicHolePhase;
private static void PeriodicHoleGuard(string mode,string model,bool exists) {
 IvBox2609Need(Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL")==mode&&File.Exists(model)==exists&&
    _part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
    String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact isolated periodic fixture required");
 for(DirectoryInfo d=new DirectoryInfo(PeriodicHoleRoot);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 if(exists)IvBox2609Need((File.GetAttributes(model)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display part required");
}
private static string PeriodicHoleBuild(string[] args) {
 bool prepare=args[0]=="nx_periodic_hole_prepare";
 IvBox2609Need(args.Length==1&&PeriodicHolePhase==(prepare?0:1),"Staged one-shot fixture required");
 bool two=Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL")=="double";
 string stem=two?"periodic_double":"periodic_split";
 string model=Path.Combine(PeriodicHoleRoot,stem+".prt");
 string output=Path.Combine(PeriodicHoleLogs,(two?"double":"split")+(prepare?"-prepare.log":"-surface.log"));
 PeriodicHoleGuard(two?"double":"split",model,false);
 IvBox2609Need(!File.Exists(output)&&!_part.Bodies.ToArray().Any()&&
     _part.Curves.ToArray().Length==(prepare?0:two?16:12),"Expected fixture inventory before native call");
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 uint part=Convert.ToUInt32(_part.Tag);
 PeriodicHolePhase=-1;
 int rc=prepare?nx12_periodic_hole_prepare(part,two?16:12,output):nx12_periodic_hole_surface(part,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false,native_log=output});
 if(prepare) {
  PeriodicHolePhase=1;
  return Json(new{ok=true,rc=rc,pid=pid,native_log=output,save_permitted=false});
 }
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 int faults;int[] types;Tag[] bad;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out types,out bad);
 int us,vs;double up,vp;double[] bounds=new double[4];
 uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
 uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
 var witness=PeriodicHoleWitness(uf,face,two);
 bool ok=!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&faults==0&&
     us==1&&Math.Abs(up-1)<1e-10&&vs==0&&_part.Curves.ToArray().Length==0&&witness.Item2;
 PeriodicHolePhase=2;
 return Json(new{ok=ok,rc=rc,pid=pid,native_log=output,save_permitted=ok,face=face.Tag.ToString(),
    body_consistency_faults=faults,u_status=us,u_period=up,v_status=vs,v_period=vp,uv_bounds=bounds,
    edges=face.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),witness=witness.Item1});
}
private static Tuple<List<object>,bool> PeriodicHoleWitness(NXOpen.UF.UFSession uf,Face face,bool two) {
 var sample=new[]{new[]{.04,.50,2.0},new[]{.96,.50,2.0},new[]{.5,.50,two?2.0:1.0},new[]{.28,.50,1.0},
    new[]{.04,.25,1.0},new[]{.96,.75,1.0},new[]{.5,0.0,3.0},new[]{.5,1.0,3.0}};
 bool ok=true;var rows=new List<object>();
 foreach(var row in sample) {
  double u=row[0],v=row[1];var state=new int[1];
  uf.Modl.AskUvPointsContainment(1,ref u,ref v,face.Tag,state);
  rows.Add(new{u=u,v=v,status=state[0],expected=(int)row[2]});
  ok=ok&&state[0]==(int)row[2];
 }
 return Tuple.Create(rows,ok);
}


private static bool PeriodicReadAttempted;
private static string PeriodicHoleCold(string[] args) {
 IvBox2609Need(args.Length==1&&!PeriodicReadAttempted,"Fresh isolated read required");
 bool two=Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL")=="double-read";
 string original=Path.Combine(PeriodicHoleRoot,two?"periodic_double.prt":"periodic_split.prt");
 string model=Path.Combine(PeriodicHoleRoot,two?"periodic_double_read.prt":"periodic_split_read.prt");
 string output=Path.Combine(PeriodicHoleLogs,two?"double-cold-ask2d.log":"split-cold-ask2d.log");
 PeriodicHoleGuard(two?"double-read":"read",model,true);
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".brep"),"Prior runtime evidence exists");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(model);
 int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(hash==IvBox2609Hash(original)&&modified==0,"Unmodified byte-identical fixture required");
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();var edges=face.GetEdges();
 IvBox2609Need(!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&edges.Length>2&&edges.Length<=32,"Unexpected cold sheet topology");
 int us,vs;double up,vp;double[] bounds=new double[4];
 uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
 uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
 var witness=PeriodicHoleWitness(uf,face,two);
 IvBox2609Need(us==1&&Math.Abs(up-1)<1e-10&&vs==0&&witness.Item2,"Cold periodicity or witness mismatch");
 var before=IvBox2609Inventory(_part);
 var geometry=new List<object>();
 foreach(var edge in edges) {
  IntPtr h=IntPtr.Zero;
  try {
   uf.Eval.Initialize(edge.Tag,out h);var lim=new double[2];uf.Eval.AskLimits(h,lim);
   var points=new List<object>();
   for(int i=0;i<=16;++i) {
    double t=lim[0]+(lim[1]-lim[0])*i/16.0;var p=new double[3];var d=new double[3];
    uf.Eval.Evaluate(h,1,t,p,d);points.Add(new{parameter=t,point=p,tangent=d});
   }
   bool spline;uf.Eval.IsSpline(h,out spline);
   object data=null;
   if(spline) {
    NXOpen.UF.UFEval.Spline d;uf.Eval.AskSpline(h,out d);
    int nk,np;double[] knots,poles;
    uf.Eval.AskSplineKnots(h,out nk,out knots);uf.Eval.AskSplineControlPts(h,out np,out poles);
    IvBox2609Need(knots.Length==nk&&poles.Length==np*4&&nk==d.num_knots&&np==d.num_control,"Bad spline buffer");
    data=new{knots=knots,poles_flat=poles,order=d.order,rational=d.is_rational};
   }
   geometry.Add(new{tag=edge.Tag.ToString(),limits=lim,type=edge.SolidEdgeType.ToString(),spline=data,faces=edge.GetFaces().Select(f=>f.Tag.ToString()).ToArray(),samples=points});
  } finally {if(h!=IntPtr.Zero)uf.Eval.Free(h);}
 }
 int faults;int[] types;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out types,out faulty);
 IvBox2609Need(faults==0,"Cold fixture consistency failure");
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 uint tag=Convert.ToUInt32(face.Tag);
 using(var file=new FileStream(output+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  writer.WriteLine("pid="+pid+" nx=12.0.2.9 part="+model+" face="+tag+" objects="+before.Length+" modified="+modified+" sha256="+hash+" tolerance_mm=0.001");
  writer.WriteLine(Json(new{face=tag,edges=geometry}));
  writer.Flush();file.Flush(true);
 }
 PeriodicReadAttempted=true;
 int brep=nx12_brep_topology_once(tag,output+".brep");
 if(brep!=0)return Json(new{ok=false,rc=brep,pid=pid,quarantine=true,save_permitted=false});
 IvBox2609Need(before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"UF_BREP changed cold fixture");
 int rc=nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&hash==IvBox2609Hash(model),pid=pid,rc=rc,face=face.Tag.ToString(),
  u_period=up,u_status=us,v_status=vs,uv_bounds=bounds,witness=witness.Item1,edges=geometry,objects_before=before.Length,objects_after=after.Length,modified_before=modified,
  modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),sha256=hash,body_consistency_faults=faults,save_permitted=false});
}
private static bool PeriodicComplexAttempted;
private static string PeriodicComplexRead(string[] args) {
 IvBox2609Need(args.Length==1&&!PeriodicComplexAttempted,"Fresh complex probe process required");
 var mode=Environment.GetEnvironmentVariable("NX_PERIODIC_HOLE_TRIAL");
 var match=System.Text.RegularExpressions.Regex.Match(mode??"","^cx_(original|step)_(inventory|[0-7])$");
 IvBox2609Need(match.Success,"Exact complex copy mode required");
 string model=Path.Combine(PeriodicHoleRoot,mode+".prt");
 string source=LabPath(@"models\phase4_phase5_staged_20261108_212227\"+
    (match.Groups[1].Value=="original"?"complex_probe_copy.prt":"complex_offline_attempt3_readback.prt"));
 PeriodicHoleGuard(mode,model,true);
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(model);
 int dirty=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(dirty==0&&hash==IvBox2609Hash(source),"Unmodified byte-identical complex copy required");
 var before=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"One native solid required");
 int faults;int[] codes;Tag[] faultTags;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out codes,out faultTags);
 IvBox2609Need(faults==0,"Complex model consistency failure");
 var faces=body.GetFaces().OrderBy(f=>Convert.ToUInt32(f.Tag)).ToArray();
 var faceRows=new List<object>();
 var edgeRows=new List<object>();
 var edges=body.GetEdges();
 foreach(var face in faces) {
  int us,vs;double up,vp;var bounds=new double[4];
  uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
  uf.Modl.AskFacePeriodicity(face.Tag,out us,out up,out vs,out vp);
  faceRows.Add(new{tag=face.Tag.ToString(),type=face.SolidFaceType.ToString(),uv=bounds,u_period=up,
      u_status=us,v_period=vp,v_status=vs,edges=face.GetEdges().Select(e=>e.Tag.ToString()).ToArray()});
 }
 foreach(var edge in edges)edgeRows.Add(new{tag=edge.Tag.ToString(),type=edge.SolidEdgeType.ToString(),
      faces=edge.GetFaces().Select(f=>f.Tag.ToString()).ToArray()});
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 if(match.Groups[2].Value=="inventory") {
  bool clean=before.SequenceEqual(IvBox2609Inventory(_part))&&hash==IvBox2609Hash(model)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
  return Json(new{ok=clean,pid=pid,faces=faceRows,edges=edgeRows,sha256=hash,body_consistency_faults=faults});
 }
 int index=Int32.Parse(match.Groups[2].Value);
 IvBox2609Need(index<faces.Length,"Face index outside native inventory");
 var selected=faces[index];string kind=selected.SolidFaceType.ToString();
 string strategy;
 if(kind=="Planar") {
  IvBox2609Need(selected.GetEdges().Length>0,"Planar trim edges required");
  strategy="planar trim; raw returned chart and rings, no periodic closure assumption";
 } else if(kind=="Parametric") {
  int u,v;double up,vp;
  uf.Modl.AskFacePeriodicity(selected.Tag,out u,out up,out v,out vp);
  IvBox2609Need(u==0||u==1,"Unsupported parametric U periodicity class");
  IvBox2609Need(v==0||v==1,"Unsupported parametric V periodicity class");
  strategy="parametric trim; record period, raw splines and native graph; no inferred domain";
 } else {
  return Json(new{ok=false,pid=pid,face=selected.Tag.ToString(),kind=kind,unsupported=true,save_permitted=false});
 }
 string output=Path.Combine(PeriodicHoleLogs,mode+"-ask2d.log");
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".brep"),"Prior native evidence exists");
 var tag=Convert.ToUInt32(selected.Tag);
 using(var file=new FileStream(output+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  writer.WriteLine(Json(new{pid=pid,model=model,sha256=hash,face=tag,type=kind,objects=before.Length,dirty=dirty,strategy=strategy,faces=faceRows,edges=edgeRows}));
  writer.Flush();file.Flush(true);
 }
 PeriodicComplexAttempted=true;
 int b=nx12_brep_topology_once(tag,output+".brep");
 if(b!=0)return Json(new{ok=false,pid=pid,rc=b,face=tag,kind=kind,quarantine=true,save_permitted=false});
 IvBox2609Need(before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"BRep changed model");
 int rc=nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,pid=pid,rc=rc,face=tag,kind=kind,quarantine=true,save_permitted=false});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&hash==IvBox2609Hash(model),pid=pid,rc=rc,face=tag,kind=kind,strategy=strategy,
    objects_before=before.Length,objects_after=after.Length,modified_before=dirty,modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),
    sha256=hash,native_log=output,save_permitted=false});
}
