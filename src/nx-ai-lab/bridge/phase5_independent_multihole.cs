// Scoped analytic evidence for the saved, hash-verified two-hole test copy.
private static string IvMulti2610Read(string[] args) {
 var p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 IvBox2609Need(args.Length==1,"No query arguments");IvBox2609Guard(p);
 IvBox2609Need(Path.GetFileName(p.FullPath)=="phase4_iv_multihole_integral_20261110.prt","Exact test copy required");
 string hash=IvBox2609Hash(p.FullPath);
 IvBox2609Need(hash=="128a3b26d74e1737c939402ed9aba748a662201ff34ce523dda31a04eac5b6f7"&&Convert.ToInt32(uf.Part.IsModified(p.Tag))==0,"Saved verified copy required");
 var inventory=IvBox2609Inventory(p);var body=p.Bodies.ToArray().Single();
 IvBox2609Need(body.IsSolidBody,"Solid required");
 var faces=body.GetFaces();var edges=body.GetEdges();
 IvBox2609Need(faces.Length==8&&edges.Length==16,"Unexpected fixture topology");
 var edgeRows=new List<object>();var faceRows=new List<object>();
 var circles=new Dictionary<Tag,IvAn2609Circle>();
 foreach(var edge in edges) {
  var adj=edge.GetFaces();IvBox2609Need(adj.Length==2&&adj[0].Tag!=adj[1].Tag&&edge.GetBody().Tag==body.Tag,"Invalid edge adjacency");
  Point3d a,b;edge.GetVertices(out a,out b);
  var kind=edge.SolidEdgeType.ToString();object circle=null;
  if(kind=="Circular") {
   var c=IvAn2609Arc(edge);circles.Add(edge.Tag,c);
   circle=new{center=c.center,x=c.x,y=c.y,radius=c.radius,limits=c.limits,eval_limits=c.evalLimits,periodic=c.periodic};
  }else IvBox2609Need(kind=="Linear","Unsupported edge");
  edgeRows.Add(new{tag=edge.Tag.ToString(),kind=kind,start=IvBox2609V(a),end=IvBox2609V(b),faces=adj.Select(f=>f.Tag.ToString()).ToArray(),circle=circle});
 }
 double tol;uf.Modl.AskDistanceTolerance(out tol);double eps=Math.Max(8*tol,100*IvBox2609Tol);
 foreach(var f in faces) {
  var info=IvAn2609Data(f);var fe=f.GetEdges();object periodic=null;double[] witness,n;
  IvBox2609Need(fe.All(e=>edges.Any(q=>q.Tag==e.Tag)&&e.GetFaces().Any(q=>q.Tag==f.Tag)),"Nonreciprocal face incidence");
  if(info.type==22) {
   var points=new List<double[]>();foreach(var e in fe)if(!circles.ContainsKey(e.Tag)){Point3d a,b;e.GetVertices(out a,out b);points.Add(IvBox2609V(a));points.Add(IvBox2609V(b));}
   IvBox2609Need(points.Count==8,"Plane must have four linear edges");
   witness=Enumerable.Range(0,3).Select(k=>points.Average(q=>q[k])).ToArray();n=IvBox2609Unit(info.dir);
  }else {
   IvBox2609Need(info.type==16&&fe.Length==2&&fe.All(e=>circles.ContainsKey(e.Tag)),"Unsupported side topology");
   var c=circles[fe[0].Tag];var d=circles[fe[1].Tag];
   n=c.x;witness=IvBox2609Add(IvBox2609Add(c.center,IvBox2609Sub(d.center,c.center),.5),n,c.radius);
   double[] uv=new double[4];int us,vs;double up,vp;uf.Modl.AskFaceUvMinmax(f.Tag,uv);uf.Modl.AskFacePeriodicity(f.Tag,out us,out up,out vs,out vp);
   IvBox2609Need((us==1)!=(vs==1),"Missing unique periodic parameter");int k=us==1?0:1;int j=1-k;double period=k==0?up:vp;
   IvBox2609Need(period>0&&Math.Abs(uv[2*k+1]-uv[2*k]-period)<1e-8,"Unexpected period span");
   var aa=new double[2];var bb=new double[2];aa[k]=uv[2*k];bb[k]=uv[2*k+1];aa[j]=bb[j]=(uv[2*j]+uv[2*j+1])/2;
   var x=new double[3];var y=new double[3];var xu=new double[3];var xv=new double[3];var yu=new double[3];var yv=new double[3];
   uf.Modl.AskFaceProps(f.Tag,aa,x,xu,xv,new double[3],new double[3],new double[3],new double[2]);
   uf.Modl.AskFaceProps(f.Tag,bb,y,yu,yv,new double[3],new double[3],new double[3],new double[2]);
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(x,y))<1e-7&&IvBox2609Norm(IvBox2609Sub(xu,yu))<1e-7&&IvBox2609Norm(IvBox2609Sub(xv,yv))<1e-7,"Seam mismatch");
   periodic=new{bounds=uv,u_status=us,v_status=vs,u_period=up,v_period=vp,seam_start=x,seam_end=y,du_start=xu,du_end=yu,dv_start=xv,dv_end=yv};
  }
  int plus=IvTrim2610Status(IvBox2609Add(witness,n,eps),body.Tag),minus=IvTrim2610Status(IvBox2609Add(witness,n,-eps),body.Tag);
  IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Outward witness ambiguous");
  n=IvBox2609Add(new double[3],n,plus==2?1:-1);var checkedWitness=IvAn2609Witness(info,body,witness,n,eps);
  faceRows.Add(new{tag=f.Tag.ToString(),type=info.type,native_sense=info.sense,axis=info.dir,axis_point=info.point,radius=info.radius,edges=fe.Select(e=>e.Tag.ToString()).ToArray(),point=witness,normal=n,witness=checkedWitness,periodic=periodic});
 }
 int faults;int[] faultCodes;Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out faults,out faultCodes,out bad);
 bool same=hash==IvBox2609Hash(p.FullPath)&&inventory.SequenceEqual(IvBox2609Inventory(p))&&Convert.ToInt32(uf.Part.IsModified(p.Tag))==0;
 return Json(new{ok=same&&faults==0,unchanged=same,sha256=hash,part=p.FullPath,faces=faceRows,edges=edgeRows,consistency_fault_count=faults,scope="Actual analytic boundary evidence; ordered loops and volume require independent host validation"});
}
