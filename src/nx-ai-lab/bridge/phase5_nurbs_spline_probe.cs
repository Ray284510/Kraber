// Phase5 isolated spline lifecycle probe. No surface creation or save from this command.
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110\nurbs-spline-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_nurbs_spline_probe(uint part,int mode,string path);
private static bool NurbsSplineProbeAttempted;
private static string NurbsSplineProbe(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_nurbs_fatal_recovery_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_NURBS_RECOVERY");
 IvBox2609Need(args.Length==1&&!NurbsSplineProbeAttempted&&(mode=="1"||mode=="2"||mode=="4"),"Fresh dedicated recovery process required");
 string model=Path.Combine(root,"spline_probe_"+mode+".prt");
 string output=Path.Combine(logs,"spline-"+mode+"-native.log");
 IvBox2609Need(!File.Exists(model)&&!File.Exists(output)&&_part!=null&&_part.IsFullyLoaded&&
   _part.PartUnits==BasePart.Units.Millimeters&&String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact unsaved blank fixture required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
    !_part.Bodies.ToArray().Any()&&!_part.Curves.ToArray().Any(),"Expected sole blank part");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 int dirty=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 var before=IvBox2609Inventory(_part);
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 using(var file=new FileStream(output+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  writer.WriteLine("pid="+pid+" nx=12.0.2.9 part="+model+" part_tag="+_part.Tag+" objects="+before.Length+" modified="+dirty+" persisted=0");
  writer.Flush();file.Flush(true);
 }
 NurbsSplineProbeAttempted=true;
 int rc=nx12_nurbs_spline_probe(Convert.ToUInt32(_part.Tag),Int32.Parse(mode),output);
 if(rc!=0)return Json(new{ok=false,pid=pid,rc=rc,quarantine=true,save_permitted=false,native_log=output});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&!File.Exists(model),pid=pid,rc=rc,mode=mode,
   objects_before=before.Length,objects_after=after.Length,modified_before=dirty,
   modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),prt_exists=File.Exists(model),
   save_permitted=false,native_log=output});
}
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110\nurbs-spline-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_prepare_trim_fixture(uint part,uint holes,string path);
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110\nurbs-spline-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_finish_trim_fixture(uint part,string path);
private static int TrimRecoveryState;
private static string TrimRecovery(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_nurbs_fatal_recovery_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_NURBS_RECOVERY");
 bool prepare=args[0]=="nx_nurbs_trim_recovery_prepare";
 IvBox2609Need(args.Length==1&&(mode=="trim0"||mode=="trim1"||mode=="trim2")&&
    (prepare?TrimRecoveryState==0:TrimRecoveryState==1),"Dedicated staged fixture process required");
 int holes=mode[4]-'0';
 string model=Path.Combine(root,mode+".prt");
 string output=Path.Combine(logs,mode+(prepare?"-prepare.log":"-surface.log"));
 IvBox2609Need(!File.Exists(model)&&!File.Exists(output)&&_part!=null&&_part.IsFullyLoaded&&
   _part.PartUnits==BasePart.Units.Millimeters&&String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact new unsaved fixture required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
  !_part.Bodies.ToArray().Any(),"Expected sole new work/display part");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 uint part=Convert.ToUInt32(_part.Tag);
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 TrimRecoveryState=-1;
 if(prepare) {
  IvBox2609Need(!_part.Curves.ToArray().Any(),"Empty part required");
  int rc=nx12_prepare_trim_fixture(part,(uint)holes,output);
  if(rc==0)TrimRecoveryState=1;
  return Json(new{ok=rc==0,rc=rc,pid=pid,holes=holes,quarantine=rc!=0,save_permitted=false,native_log=output});
 }
 int result=nx12_finish_trim_fixture(part,output);
 if(result!=0)return Json(new{ok=false,rc=result,pid=pid,quarantine=true,save_permitted=false,native_log=output});
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();
 var edges=face.GetEdges();
 IvBox2609Need(!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&edges.Length==4*(1+holes)&&
     !_part.Curves.ToArray().Any(),"Unexpected sheet, edge, or temporary curve count");
 int faults;int[] types;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out types,out faulty);
 IvBox2609Need(faults==0,"Fixture consistency failure");
 var witness=new List<object>();
 var witnessParameters=new[]{new[]{.12,.12},new[]{.29,.35},new[]{.685,.645},new[]{.01,.01}};
 int[] expect={1,holes>=1?2:1,holes>=2?2:1,2};
 for(int i=0;i<expect.Length;++i) {
  double u=witnessParameters[i][0],v=witnessParameters[i][1];var state=new int[1];
  uf.Modl.AskUvPointsContainment(1,ref u,ref v,face.Tag,state);
  witness.Add(new{u=u,v=v,status=state[0],expected=expect[i]});
  IvBox2609Need(state[0]==expect[i],"Native UV containment disagrees with fixture construction");
 }
 TrimRecoveryState=2;
 return Json(new{ok=true,rc=result,pid=pid,holes=holes,face=face.Tag.ToString(),
    edge_count=edges.Length,witness=witness,body_consistency_faults=faults,save_permitted=true,native_log=output});
}
private static bool TrimReadAttempted;
private static string TrimRecoveryRead(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_nurbs_fatal_recovery_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\nurbs-fatal-recovery-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_NURBS_RECOVERY");
 IvBox2609Need(args.Length==1&&!TrimReadAttempted&&(mode=="read0"||mode=="read1"||mode=="read2"),"Fresh read process required");
 int holes=mode[4]-'0';
 string original=Path.Combine(root,"trim"+holes+".prt");
 string model=Path.Combine(root,mode+".prt");
 string output=Path.Combine(logs,mode+"-ask2d.log");
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".brep")&&_part!=null&&_part.IsFullyLoaded&&
   _part.PartUnits==BasePart.Units.Millimeters&&String.Equals(_part.FullPath,model,StringComparison.OrdinalIgnoreCase),"Exact independent copy required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(model)&FileAttributes.ReparsePoint)==0&&(File.GetAttributes(original)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Expected sole work/display part");
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(model);
 int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(hash==IvBox2609Hash(original)&&modified==0,"Unmodified byte-identical fixture required");
 var body=_part.Bodies.ToArray().Single();var face=body.GetFaces().Single();var edges=face.GetEdges();
 IvBox2609Need(!body.IsSolidBody&&face.SolidFaceType.ToString()=="Parametric"&&edges.Length==4*(1+holes),"Unexpected cold sheet topology");
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
 TrimReadAttempted=true;
 int brep=nx12_brep_topology_once(tag,output+".brep");
 if(brep!=0)return Json(new{ok=false,rc=brep,pid=pid,quarantine=true,save_permitted=false});
 IvBox2609Need(before.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"UF_BREP changed cold fixture");
 int rc=nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false});
 var after=IvBox2609Inventory(_part);
 return Json(new{ok=before.SequenceEqual(after)&&hash==IvBox2609Hash(model),pid=pid,rc=rc,face=face.Tag.ToString(),
  holes=holes,edges=geometry,objects_before=before.Length,objects_after=after.Length,modified_before=modified,
  modified_after=Convert.ToInt32(uf.Part.IsModified(_part.Tag)),sha256=hash,body_consistency_faults=faults,save_permitted=false});
}
