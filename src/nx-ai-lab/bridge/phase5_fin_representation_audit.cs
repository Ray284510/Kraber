private static bool FinRepresentationAttempted;
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\uf-brep-fin-20261112\fin-representation-probe.dll",
 CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl,CharSet=System.Runtime.InteropServices.CharSet.Ansi,ExactSpelling=true)]
private static extern int nx12_fin_representation_geometry(uint body,int variant,int edgeMode,string path);

[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\uf-brep-fin-matrix-20261112\fin-matrix-probe.dll",
 CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl,CharSet=System.Runtime.InteropServices.CharSet.Ansi,ExactSpelling=true)]
private static extern int nx12_fin_edge3d_supplement(uint body,int variant,string path);

private static string FinRepresentationAudit(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_FIN_REP_TRIAL");
 var modes=new[]{"b","c","b_cold","c_cold","intersection","intersection_peak","a","d"};
 IvBox2609Need(args.Length==1&&!FinRepresentationAttempted&&modes.Contains(mode),"Isolated Fin representation audit required");
 bool supplement=mode=="a"||mode=="d";
 string folder=LabPath(supplement?@"models\phase4_uf_brep_fin_matrix_20261112":@"models\phase4_uf_brep_fin_20261112");
 string logs=LabPath(supplement?@"logs\phase5\uf-brep-fin-matrix-20261112":@"logs\phase5\uf-brep-fin-20261112");
 string path=Path.Combine(folder,(mode.StartsWith("intersection",StringComparison.Ordinal)?mode:mode.Substring(0,1))+".prt");
 string seed=LabPath(@"models\phase4_phase5_staged_20261108_212227\complex_probe_copy.prt");
 for(DirectoryInfo d=new DirectoryInfo(folder);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)&&
  (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Exact millimeter fixture required");
 var session=Session.GetSession();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(path);
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
  hash==IvBox2609Hash(seed)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Saved unmodified original copy required");
 var body=_part.Bodies.ToArray().Single();
 var faces=body.GetFaces();var edges=body.GetEdges();
 IvBox2609Need(body.IsSolidBody&&faces.Length==4&&edges.Length==4,"Unexpected four-face solid");
 var inventory=IvBox2609Inventory(_part);
 int faults;int[] faultCodes;Tag[] faulty;
 uf.Modl.AskBodyConsistency(body.Tag,out faults,out faultCodes,out faulty);
 FinRepresentationAttempted=true;
 if(mode.StartsWith("intersection",StringComparison.Ordinal)) {
  int samples=VerifyFinIntersectionWitnesses(uf,faces,edges,logs,mode);
  bool unchanged=hash==IvBox2609Hash(path)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&inventory.SequenceEqual(IvBox2609Inventory(_part));
  return Json(new{ok=unchanged&&faults==0,mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
   sha256=hash,consistency_faults=faults,samples=samples,modified=0});
 }
 int? fin=null;
 int edgeCode;
 string edgeOutput=Path.Combine(logs,mode+"_edge3d-native.log");
 if(supplement) {
  edgeCode=nx12_fin_edge3d_supplement(Convert.ToUInt32(body.Tag),mode=="a"?0:3,edgeOutput);
 } else {
  int variant=mode[0]=='b'?1:2;
  fin=nx12_fin_representation_geometry(Convert.ToUInt32(body.Tag),variant,0,Path.Combine(logs,mode+"_brep-native.log"));
  if(fin!=0)return Json(new{ok=false,mode=mode,fin_rc=fin,quarantine=true,save_permitted=false});
  edgeCode=nx12_fin_representation_geometry(Convert.ToUInt32(body.Tag),variant,1,edgeOutput);
 }
 if(edgeCode!=0)return Json(new{ok=false,mode=mode,fin_rc=fin,edge_rc=edgeCode,quarantine=true,save_permitted=false});
 string witnesses=Path.Combine(logs,mode+"-native.jsonl");
 using(var file=new FileStream(witnesses,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  foreach(var face in faces) {
   int u,v;double up,vp;var uv=new double[4];
   uf.Modl.AskFaceUvMinmax(face.Tag,uv);uf.Modl.AskFacePeriodicity(face.Tag,out u,out up,out v,out vp);
   writer.WriteLine(Json(new{kind="face",tag=face.Tag.ToString(),face_type=face.SolidFaceType.ToString(),uv=uv,
    u_status=u,u_period=up,v_status=v,v_period=vp,edges=face.GetEdges().Select(x=>x.Tag.ToString()).ToArray()}));
  }
  foreach(var edge in edges) {
   double tolerance;
   uf.Modl.AskEdgeTolerance(edge.Tag,out tolerance);
   IntPtr handle;
   uf.Eval.Initialize(edge.Tag,out handle);
   try {
    var limits=new double[2];uf.Eval.AskLimits(handle,limits);
    writer.WriteLine(Json(new{kind="edge",tag=edge.Tag.ToString(),edge_tolerance_mm=tolerance,range=limits,
     faces=edge.GetFaces().Select(x=>x.Tag.ToString()).ToArray()}));
    for(int i=0;i<=64;++i) {
     double t=limits[0]+(limits[1]-limits[0])*i/64.0;
     var xyz=new double[3];var d=new double[3];
     uf.Eval.Evaluate(handle,1,t,xyz,d);
     writer.WriteLine(Json(new{kind="UF_EVAL",tag=edge.Tag.ToString(),t=t,point=xyz,first_derivative=d}));
    }
   } finally {uf.Eval.Free(handle);}
  }
  writer.Flush();file.Flush(true);
 }
 bool clean=hash==IvBox2609Hash(path)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&inventory.SequenceEqual(IvBox2609Inventory(_part));
 return Json(new{ok=clean&&faults==0,mode=mode,pid=System.Diagnostics.Process.GetCurrentProcess().Id,
  sha256=hash,fin_rc=fin,edge_rc=edgeCode,consistency_faults=faults,objects=inventory.Length,modified=0});
}

private static int VerifyFinIntersectionWitnesses(NXOpen.UF.UFSession uf,Face[] faces,Edge[] edges,string logs,string mode) {
 var input=File.ReadAllLines(Path.Combine(logs,mode+"-requests.tsv"));
 IvBox2609Need(input.Length>=8&&input.Length<=68,"Bounded intersection witnesses required");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 using(var file=new FileStream(Path.Combine(logs,mode+"-native.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  foreach(string line in input) {
   string[] a=line.Split('\t');
   IvBox2609Need(a.Length==4,"Malformed intersection witness");
   var face=faces.Single(f=>f.Tag.ToString()==a[0]);var edge=edges.Single(e=>e.Tag.ToString()==a[1]);
   IvBox2609Need(edge.GetFaces().Any(f=>f.Tag==face.Tag),"Witness edge/face use mismatch");
   var uv=new[]{Double.Parse(a[2],culture),Double.Parse(a[3],culture)};
   var box=new double[4];uf.Modl.AskFaceUvMinmax(face.Tag,box);
   IvBox2609Need(uv[0]>=box[0]-1e-9&&uv[0]<=box[1]+1e-9&&uv[1]>=box[2]-1e-9&&uv[1]<=box[3]+1e-9,
     "Intersection witness outside native parameter box");
   var p=new double[3];var du=new double[3];var dv=new double[3];var normal=new double[3];
   uf.Modl.AskFaceProps(face.Tag,uv,p,du,dv,new double[3],new double[3],normal,new double[2]);
   int containment;uf.Modl.AskPointContainment(p,face.Tag,out containment);
   IntPtr h;
   uf.Eval.Initialize(edge.Tag,out h);
   try {
    double t;var point=new double[3];var d=new double[3];
    uf.Eval.EvaluateClosestPoint(h,p,out t,point);uf.Eval.Evaluate(h,1,t,point,d);
    writer.WriteLine(Json(new{face=a[0],edge=a[1],uv=uv,point=p,du=du,dv=dv,normal=normal,
      trim_status=containment,edge_t=t,edge_point=point,edge_derivative=d}));
   } finally {uf.Eval.Free(h);}
  }
  writer.Flush();file.Flush(true);
 }
 return input.Length;
}
