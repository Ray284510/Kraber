private static bool TrimOffsetAttempted;
private static string FinspTrimOffsetAudit(string[] args) {
 string mode=Environment.GetEnvironmentVariable("NX_FINSP_TRIM_OFFSET_TRIAL");
 IvBox2609Need(args.Length==1&&!TrimOffsetAttempted&&(mode=="offset_ask2d"||mode=="filter"),"Isolated trim witness process required");
 string folder=LabPath(@"models\phase4_finsp_trim_offset_20261112");
 string logs=LabPath(@"logs\phase5\finsp-trim-offset-20261112");
 string fixture=Path.Combine(folder,mode+".prt");
 for(var directory=new DirectoryInfo(folder);directory!=null;directory=directory.Parent)
  IvBox2609Need((directory.Attributes&FileAttributes.ReparsePoint)==0,"Reparse fixture directory");
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,fixture,StringComparison.OrdinalIgnoreCase)&&File.Exists(fixture)&&
  (File.GetAttributes(fixture)&FileAttributes.ReparsePoint)==0,"Exact millimeter fixture required");
 var s=Session.GetSession();
 var uf=NXOpen.UF.UFSession.GetUFSession();
 string hash=IvBox2609Hash(fixture);
 int dirty=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 IvBox2609Need(s.Parts.Cast<BasePart>().Count()==1&&s.Parts.Work==_part&&s.Parts.Display==_part&&dirty==0&&
  hash=="85e82314c0d86f75cae625b153029f599b71383d87d00aad73c54152c35b3f05","Unchanged disposable fixture required");
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody&&body.GetFaces().Length==4&&body.GetEdges().Length==4,"Unexpected fixture geometry");
 var face=body.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Offset");
 var edges=new Dictionary<string,Edge>();
 var planes=new Dictionary<string,Face>();
 foreach(var e in face.GetEdges()) {
  var plane=e.GetFaces().Single(f=>f.SolidFaceType.ToString()=="Planar");
  var box=new double[6];uf.Modl.AskBoundingBox(plane.Tag,box);
  double x=(box[0]+box[3])/2;
  IvBox2609Need(Math.Abs(x)<.001||Math.Abs(x-180)<.001,"Unexpected offset end plane");
  string role=Math.Abs(x)<.001?"xmin":"xmax";
  edges.Add(role,e);planes.Add(role,plane);
 }
 IvBox2609Need(edges.Count==2&&planes.Count==2,"Expected two Offset Edge uses");
 var before=IvBox2609Inventory(_part);
 int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 var bounds=new double[4];uf.Modl.AskFaceUvMinmax(face.Tag,bounds);
 int u,v;double up,vp;uf.Modl.AskFacePeriodicity(face.Tag,out u,out up,out v,out vp);
 TrimOffsetAttempted=true;
 int count=0;
 if(mode=="offset_ask2d") {
  string raw=Path.Combine(logs,"offset-ask2d-native.log");
  IvBox2609Need(!File.Exists(raw),"Native Ask2d receipt already exists");
  using(var f=new FileStream(raw+".preflight",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
  using(var w=new StreamWriter(f)) {
   w.WriteLine(Json(new{pid=pid,path=fixture,sha256=hash,face=face.Tag.ToString(),kind="Offset",
    dirty=dirty,object_count=before.Length,uv=bounds,u_status=u,u_period=up,v_status=v,v_period=vp}));
   w.Flush();f.Flush(true);
  }
  int rc=nx12_ask2d_sheet_once(Convert.ToUInt32(face.Tag),raw);
  if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,quarantine=true,save_permitted=false,post_nx_queries_permitted=false});
 } else {
  count=FinspTrimWitnesses(uf,face,planes,edges,logs);
 }
 var after=IvBox2609Inventory(_part);
 string afterHash=IvBox2609Hash(fixture);
 int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 return Json(new{ok=before.SequenceEqual(after)&&hash==afterHash&&(mode=="offset_ask2d"||modified==0),
  rc=0,pid=pid,mode=mode,face=face.Tag.ToString(),edge_xmin=edges["xmin"].Tag.ToString(),edge_xmax=edges["xmax"].Tag.ToString(),
  object_count_before=before.Length,object_count_after=after.Length,modified_before=dirty,modified_after=modified,
  sha256_before=hash,sha256_after=afterHash,uv=bounds,u_period=up,v_period=vp,witnesses=count,save_permitted=false});
}

private static int FinspTrimWitnesses(NXOpen.UF.UFSession uf,Face offset,Dictionary<string,Face> planes,
 Dictionary<string,Edge> edges,string logs) {
 var requests=File.ReadAllLines(Path.Combine(logs,"trim-requests.tsv"));
 IvBox2609Need(requests.Length>0&&requests.Length<=1800,"Bounded trim candidate set required");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<string,IntPtr>();
 try {
  foreach(var e in edges) {
   IntPtr h;uf.Eval.Initialize(e.Value.Tag,out h);handles.Add(e.Key,h);
  }
  using(var f=new FileStream(Path.Combine(logs,"trim-native.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
  using(var w=new StreamWriter(f)) {
   foreach(string line in requests) {
    string[] a=line.Split('\t');
    IvBox2609Need(a.Length==6&&(a[2]=="offset"||a[2]=="plane"),"Malformed trim witness");
    var face=a[2]=="offset"?offset:planes[a[1]];
    var edge=edges[a[1]];
    var xyz=a.Skip(3).Select(x=>Double.Parse(x,culture)).ToArray();
    var uv=new double[2];var support=new double[3];
    uf.Modl.AskFaceParm2(face.Tag,xyz,uv,support);
    double u=uv[0],v=uv[1];var uvState=new int[1];
    uf.Modl.AskUvPointsContainment(1,ref u,ref v,face.Tag,uvState);
    int rawState,supportState,edgeState;
    uf.Modl.AskPointContainment(xyz,face.Tag,out rawState);
    uf.Modl.AskPointContainment(support,face.Tag,out supportState);
    uf.Modl.AskPointContainment(xyz,edge.Tag,out edgeState);
    double t;var edgePoint=new double[3];var d=new double[3];var h=handles[a[1]];
    uf.Eval.EvaluateClosestPoint(h,xyz,out t,edgePoint);uf.Eval.Evaluate(h,1,t,edgePoint,d);
    w.WriteLine(Json(new{id=a[0],role=a[1],face_role=a[2],face=face.Tag.ToString(),edge=edge.Tag.ToString(),
      xyz=xyz,uv=uv,support_point=support,raw_face_status=rawState,support_face_status=supportState,uv_status=uvState[0],
      direct_edge_status=edgeState,nearest_edge_t=t,nearest_edge_xyz=edgePoint,nearest_edge_d=d}));
   }
   w.Flush();f.Flush(true);
  }
 } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
 return requests.Length;
}
