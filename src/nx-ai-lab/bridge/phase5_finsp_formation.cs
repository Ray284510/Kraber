private static bool FinspFormationAttempted;
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\finsp-formation-20261112\finsp-formation-probe.dll",
 CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl,CharSet=System.Runtime.InteropServices.CharSet.Ansi,ExactSpelling=true)]
private static extern int nx12_finsp_formation(uint body,uint first,uint second,string path);

private static string FinspFormationProbe(string[] args) {
 IvBox2609Need(args.Length==1&&!FinspFormationAttempted&&Environment.GetEnvironmentVariable("NX_FINSP_FORMATION_TRIAL")=="formation",
  "Isolated FINSP representation experiment required");
 string folder=LabPath(@"models\phase4_finsp_formation_20261112");
 string logs=LabPath(@"logs\phase5\finsp-formation-20261112");
 string path=Path.Combine(folder,"formation.prt");
 string original=LabPath(@"models\phase4_phase5_staged_20261108_212227\complex_probe_copy.prt");
 for(DirectoryInfo d=new DirectoryInfo(folder);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)&&
  (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Exact fixture required");
 string hash=IvBox2609Hash(path);
 var session=Session.GetSession();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part&&
  hash=="85e82314c0d86f75cae625b153029f599b71383d87d00aad73c54152c35b3f05"&&hash==IvBox2609Hash(original)&&
  Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Unmodified saved source copy required");
 var inventory=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 var faces=body.GetFaces();
 IvBox2609Need(body.IsSolidBody&&faces.Length==4&&body.GetEdges().Length==4,"Unexpected fixture");
 var offset=faces.Single(f=>f.SolidFaceType.ToString()=="Offset");
 var planes=new Dictionary<string,Face>();
 var edges=new Dictionary<string,Edge>();
 foreach(var edge in offset.GetEdges()) {
  var plane=edge.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Planar");
  var box=new double[6];uf.Modl.AskBoundingBox(plane.Tag,box);
  double x=(box[0]+box[3])/2;
  IvBox2609Need(Math.Abs(x)<.001||Math.Abs(x-180)<.001,"Unrecognized planar support");
  string role=Math.Abs(x)<.001?"xmin":"xmax";
  planes.Add(role,plane);edges.Add(role,edge);
 }
 IvBox2609Need(edges.Count==2&&planes.Count==2,"Expected two Offset edge uses");
 FinspFormationAttempted=true;
 int rc=nx12_finsp_formation(Convert.ToUInt32(body.Tag),Convert.ToUInt32(edges["xmin"].Tag),
  Convert.ToUInt32(edges["xmax"].Tag),Path.Combine(logs,"reverse_brep-native.log"));
 if(rc!=0)return Json(new{ok=false,rc=rc,quarantine=true,save_permitted=false});
 int count=FinspFormationWitnesses(uf,offset,planes,edges,logs);
 bool unchanged=hash==IvBox2609Hash(path)&&inventory.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=unchanged,pid=System.Diagnostics.Process.GetCurrentProcess().Id,rc=rc,sha256=hash,
  objects_before=inventory.Length,objects_after=IvBox2609Inventory(_part).Length,modified=0,witnesses=count,
  offset=offset.Tag.ToString(),plane_xmin=planes["xmin"].Tag.ToString(),plane_xmax=planes["xmax"].Tag.ToString(),
  edge_xmin=edges["xmin"].Tag.ToString(),edge_xmax=edges["xmax"].Tag.ToString()});
}

private static int FinspFormationWitnesses(NXOpen.UF.UFSession uf,Face offset,Dictionary<string,Face> planes,
 Dictionary<string,Edge> edges,string logs) {
 var inputs=File.ReadAllLines(Path.Combine(logs,"physical-requests.tsv"));
 IvBox2609Need(inputs.Length>=20&&inputs.Length<=600,"Bounded physical witness set required");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<string,IntPtr>();
 try {
  foreach(var edge in edges) {
   IntPtr h;uf.Eval.Initialize(edge.Value.Tag,out h);handles.Add(edge.Key,h);
  }
  using(var file=new FileStream(Path.Combine(logs,"physical-native.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
  using(var writer=new StreamWriter(file)) {
   foreach(string line in inputs) {
    var row=line.Split('\t');
    IvBox2609Need(row.Length==10&&(row[1]=="F"||row[1]=="P")&&(row[3]=="plane"||row[3]=="offset"),"Malformed witness");
    string role=row[2];var face=row[3]=="offset"?offset:planes[role];
    var numbers=row.Skip(4).Select(x=>Double.Parse(x,culture)).ToArray();
    var h=handles[role];var limit=new double[2];uf.Eval.AskLimits(h,limit);
    double t=numbers[0];
    IvBox2609Need(t>=limit[0]-1e-12&&t<=limit[1]+1e-12,"Edge parameter out of range");
    var anchor=new double[3];var anchorD=new double[3];
    uf.Eval.Evaluate(h,1,t,anchor,anchorD);
    var uv=new[]{numbers[1],numbers[2]};var inputPoint=numbers.Skip(3).ToArray();
    var nativePoint=new double[3];var du=new double[3];var dv=new double[3];var normal=new double[3];
    if(row[1]=="P")uf.Modl.AskFaceParm2(face.Tag,inputPoint,uv,nativePoint);
    else uf.Modl.AskFaceProps(face.Tag,uv,nativePoint,du,dv,new double[3],new double[3],normal,new double[2]);
    var edgeUv=new double[2];var support=new double[3];
    uf.Modl.AskFaceParm2(face.Tag,anchor,edgeUv,support);
    var closest=new double[3];double closestT;
    uf.Eval.EvaluateClosestPoint(h,nativePoint,out closestT,closest);
    int containment;uf.Modl.AskPointContainment(nativePoint,face.Tag,out containment);
    writer.WriteLine(Json(new{id=row[0],kind=row[1],role=role,face=face.Tag.ToString(),edge=edges[role].Tag.ToString(),
     anchor_t=t,anchor=anchor,anchor_d=anchorD,uv=uv,point=nativePoint,du=du,dv=dv,normal=normal,
     edge_uv=edgeUv,edge_support_point=support,nearest_edge_t=closestT,nearest_edge_point=closest,containment=containment}));
   }
   writer.Flush();file.Flush(true);
  }
 } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
 return inputs.Length;
}
