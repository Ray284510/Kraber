private static bool FinGeometryAttempted;
private static string FinGeometryProbe(string[] args) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_fin_geometry_20261112";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\fin-geometry-20261112";
 string mode=Environment.GetEnvironmentVariable("NX_FIN_GEOMETRY_TRIAL");
 IvBox2609Need(args.Length==1&&!FinGeometryAttempted&&(mode=="original"||mode=="step"||mode=="original_cold"||mode=="step_cold"||mode=="offset_stencil"||mode=="step_file_probe"),"Isolated probe required");
 bool step=mode.StartsWith("step",StringComparison.Ordinal);
 string family=step?"step":"original";
 string path=Path.Combine(root,mode+".prt");
 string source=LabPath(@"models\phase4_phase5_staged_20261108_212227\"+(step?"complex_offline_attempt3_readback.prt":"complex_probe_copy.prt"));
 IvBox2609Need(_part!=null&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters&&
  String.Equals(_part.FullPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path),"Exact saved copy required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var session=Session.GetSession();var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(session.Parts.Cast<BasePart>().Count()==1&&session.Parts.Work==_part&&session.Parts.Display==_part,"Sole work/display required");
 string hash=IvBox2609Hash(path);
 IvBox2609Need(hash==IvBox2609Hash(source)&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0,"Copy provenance or dirty state");
 var inventory=IvBox2609Inventory(_part);
 var body=_part.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"Single solid required");
 var faces=body.GetFaces().ToDictionary(f=>f.Tag.ToString());
 var edges=body.GetEdges().ToDictionary(e=>e.Tag.ToString());
 IvBox2609Need(faces.Count==4&&edges.Count==4,"Unexpected diagnostic fixture");
 var lines=File.ReadAllLines(Path.Combine(logs,(mode=="offset_stencil"||mode=="step_file_probe"?mode:family)+"-requests.tsv"));
 IvBox2609Need(lines.Length>0&&lines.Length<=20000,"Bounded geometry request");
 var culture=System.Globalization.CultureInfo.InvariantCulture;
 var handles=new Dictionary<string,IntPtr>();
 var bounds=new Dictionary<string,double[]>();
 foreach(var f in faces) {
  var uv=new double[4];uf.Modl.AskFaceUvMinmax(f.Value.Tag,uv);bounds[f.Key]=uv;
 }
 FinGeometryAttempted=true;
 string output=Path.Combine(logs,mode+"-native.jsonl");
 using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
 using(var writer=new StreamWriter(file)) {
  try {
   foreach(var edge in edges){IntPtr h;uf.Eval.Initialize(edge.Value.Tag,out h);handles.Add(edge.Key,h);}
   for(int i=0;i<lines.Length;++i) {
    string[] a=lines[i].Split('\t');
    double t=Double.Parse(a[4],culture);
    var h=handles[a[3]];
    if(a[0]=="E") {
     var xyz=new double[3];var d=new double[6];uf.Eval.Evaluate(h,2,t,xyz,d);
     writer.WriteLine(Json(new{kind="E",i=i,point=xyz,d=d}));
    } else if(a[0]=="F") {
     double u=Double.Parse(a[5],culture),v=Double.Parse(a[6],culture);
     var lim=bounds[a[2]];
     if(u<lim[0]-1e-8||u>lim[1]+1e-8||v<lim[2]-1e-8||v>lim[3]+1e-8) {
      writer.WriteLine(Json(new{kind="F",i=i,outside_parameter_box=true}));continue;
     }
     var uv=new[]{Math.Max(lim[0],Math.Min(lim[1],u)),Math.Max(lim[2],Math.Min(lim[3],v))};
     var xyz=new double[3];var du=new double[3];var dv=new double[3];var uu=new double[3];var vv=new double[3];var normal=new double[3];
     uf.Modl.AskFaceProps(faces[a[2]].Tag,uv,xyz,du,dv,uu,vv,normal,new double[2]);
     double phase;var closest=new double[3];var d=new double[6];
     uf.Eval.EvaluateClosestPoint(h,xyz,out phase,closest);
     uf.Eval.Evaluate(h,2,phase,closest,d);
     writer.WriteLine(Json(new{kind="F",i=i,uv=uv,point=xyz,du=du,dv=dv,uu=uu,vv=vv,normal=normal,
      edge_parameter=phase,edge_point=closest,edge_d=d}));
    } else if(a[0]=="X") {
     var xyz=new[]{Double.Parse(a[5],culture),Double.Parse(a[6],culture),Double.Parse(a[7],culture)};
     double phase;var closest=new double[3];var d=new double[6];
     uf.Eval.EvaluateClosestPoint(h,xyz,out phase,closest);
     uf.Eval.Evaluate(h,2,phase,closest,d);
     writer.WriteLine(Json(new{kind="X",i=i,parameter=phase,point=closest,d=d}));
    } else {
     IvBox2609Need(a[0]=="P","Unexpected request kind");
     var p=new[]{Double.Parse(a[5],culture),Double.Parse(a[6],culture),Double.Parse(a[7],culture)};
     var uv=new double[2];var q=new double[3];
     var face=faces[a[2]].Tag;
     uf.Modl.AskFaceParm2(face,p,uv,q);
     int state;uf.Modl.AskPointContainment(q,face,out state);
     writer.WriteLine(Json(new{kind="P",i=i,uv=uv,point=q,trim_status=state}));
    }
   }
  } finally {foreach(var h in handles.Values)uf.Eval.Free(h);}
  writer.Flush();file.Flush(true);
 }
 bool clean=hash==IvBox2609Hash(path)&&inventory.SequenceEqual(IvBox2609Inventory(_part))&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0;
 return Json(new{ok=clean,sha256=hash,pid=System.Diagnostics.Process.GetCurrentProcess().Id,objects=inventory.Length,requests=lines.Length,modified=0});
}
