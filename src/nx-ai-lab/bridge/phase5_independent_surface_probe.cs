// Coordinator independent acceptance probe. No builders, mutation, mass evaluation or saves.
private static string IndependentSurfaceProbe(string[] args) {
 if(args.Length!=1 || _part==null || !_part.IsFullyLoaded)throw new Exception("One loaded saved part required");
 string path=Path.GetFullPath(_part.FullPath);
 string root=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227";
 bool allowed=path==root+@"\complex_probe_copy.prt" || path==root+@"\complex_offline_attempt3_readback.prt" ||
  (String.Equals(Path.GetDirectoryName(path),GsRoot,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(path).StartsWith("phase4_gs_",StringComparison.Ordinal));
 if(!allowed || !File.Exists(path) || _part.PartUnits!=BasePart.Units.Millimeters)throw new Exception("Only exact saved diagnostic or GS parts permitted");
 var s=Session.GetSession();int count=0;foreach(BasePart part in s.Parts){count++;if(part.Tag!=_part.Tag)throw new Exception("Other part loaded");}
 if(count!=1||s.Parts.Work!=_part||s.Parts.Display!=_part)throw new Exception("Sole work/display required");
 var uf=NXOpen.UF.UFSession.GetUFSession();if(Convert.ToInt32(uf.Part.IsModified(_part.Tag))!=0)throw new Exception("Unsaved part refused");
 string hash=IvBox2609Hash(path);var inventory=IvBox2609Inventory(_part);var faces=new List<object>();var edges=new List<object>();
 var bodies=_part.Bodies.ToArray();if(bodies.Length!=1)throw new Exception("Exactly one local body required");
 var body=bodies[0];string error=null;
 try {
  int n;int[] faults;Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out n,out faults,out bad);if(n!=0)throw new Exception("Body consistency faults");
  foreach(Face f in body.GetFaces()) {
   double[] lim=new double[4];uf.Modl.AskFaceUvMinmax(f.Tag,lim);
   int type,dirSense;double radius,radData;var pt=new double[3];var axis=new double[3];var bb=new double[6];
   uf.Modl.AskFaceData(f.Tag,out type,pt,axis,bb,out radius,out radData,out dirSense);
   int us,vs;double up,vp;uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
   var samples=new List<object>();
   for(int i=0;i<=16;i++)for(int j=0;j<=8;j++) {
    var uv=new double[]{lim[0]+(lim[1]-lim[0])*i/16.0,lim[2]+(lim[3]-lim[2])*j/8.0};
    var p=new double[3];var du=new double[3];var dv=new double[3];var normal=new double[3];
    uf.Modl.AskFaceProps(f.Tag,uv,p,du,dv,new double[3],new double[3],normal,new double[2]);
    int trim;uf.Modl.AskPointContainment(p,f.Tag,out trim);
    samples.Add(new{uv=uv,point=p,du=du,dv=dv,normal=normal,trim_status=trim});
   }
   faces.Add(new{tag=f.Tag.ToString(),type=type,native_type=f.SolidFaceType.ToString(),point=pt,axis=axis,radius=radius,rad_data=radData,norm_dir=dirSense,
    uv_bounds=lim,u_status=us,u_period=up,v_status=vs,v_period=vp,edges=f.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),samples=samples});
  }
  foreach(Edge e in body.GetEdges()) {
   IntPtr h=IntPtr.Zero;try {
    uf.Eval.Initialize(e.Tag,out h);var lim=new double[2];uf.Eval.AskLimits(h,lim);var points=new List<object>();
    for(int i=0;i<=128;i++){double t=lim[0]+(lim[1]-lim[0])*i/128.0;var p=new double[3];var d=new double[3];uf.Eval.Evaluate(h,1,t,p,d);points.Add(new{parameter=t,point=p,derivative=d});}
    edges.Add(new{tag=e.Tag.ToString(),type=e.SolidEdgeType.ToString(),limits=lim,faces=e.GetFaces().Select(f=>f.Tag.ToString()).ToArray(),samples=points});
   }finally{if(h!=IntPtr.Zero)uf.Eval.Free(h);}
  }
 }catch(Exception e){error=e.Message;}
 bool unchanged=IvBox2609Hash(path)==hash&&Convert.ToInt32(uf.Part.IsModified(_part.Tag))==0&&inventory.SequenceEqual(IvBox2609Inventory(_part));
 return Json(new{ok=error==null&&unchanged,error=error,part=path,sha256=hash,unchanged=unchanged,read_only_query=true,faces=faces,edges=edges,
  scope="Finite actual native surface/edge observations only; UV bounds are NOT asserted to equal trimmed domains; no independent integral or global error bound"});
}
