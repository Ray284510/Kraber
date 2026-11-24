// Append INSIDE NX_MCP_Loader AFTER existing annular/analytic helpers.
// SOURCE CANDIDATE ONLY: not compiled, deployed or executed.
// Dispatch ONE new readonly query:
// case "nx_independent_annular_periodic": return IvPer2610Read(parts);
private static double[] IvPer2610Add(double[] a,double[] b,double k) {
 return new double[]{a[0]+k*b[0],a[1]+k*b[1],a[2]+k*b[2]};
}
private static void IvPer2610Near(double[] a,double[] b,double tol,string why) {
 IvBox2609Need(IvBox2609Norm(IvBox2609Sub(a,b))<=tol,why);
}
private static double[] IvPer2610Unit(double[] a) {
 double n=IvBox2609Norm(a);IvBox2609Need(n>1e-12,"Degenerate vector");
 return new double[]{a[0]/n,a[1]/n,a[2]/n};
}
private static double[] IvPer2610Circle(IvAn2609Circle c,double t) {
 return IvPer2610Add(IvPer2610Add(c.center,c.x,c.radius*Math.Cos(t)),c.y,c.radius*Math.Sin(t));
}
private static string IvPer2610Read(string[] args) {
 var p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string before=null,error=null,auditError=null;string[] inventory=null;
 bool unchanged=false;object result=null;
 try {
  IvBox2609Need(args.Length==1,"No query arguments");IvTrim2610Guard(p);
  before=IvBox2609Hash(p.FullPath);inventory=IvBox2609Inventory(p);
  IvBox2609Need(Convert.ToInt32(uf.Part.IsModified(p.Tag))==0,"Saved fixture required");
  // Preserve existing cap query and its full evidence verbatim, not reimplement it.
  string capReceipt=IvTrim2610Read(args);
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();
  foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid");
  var body=bodies[0];var faces=body.GetFaces();var edges=body.GetEdges();
  int nf;int[] faults;NXOpen.Tag[] bad;
  uf.Modl.AskBodyConsistency(body.Tag,out nf,out faults,out bad);
  IvBox2609Need(nf==0&&faces.Length==4&&edges.Length==4,"Four-face/four-circle healthy fixture only");
  var fd=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Face>();
  var ec=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Circle>();
  var uses=new System.Collections.Generic.Dictionary<NXOpen.Tag,int>();
  var caps=new System.Collections.Generic.List<IvAn2609Face>();
  var sides=new System.Collections.Generic.List<IvAn2609Face>();
  foreach(var f in faces) {
   var d=IvAn2609Data(f);fd.Add(f.Tag,d);
   if(d.type==22)caps.Add(d);else {IvBox2609Need(d.type==16,"Nonanalytic face");sides.Add(d);}
  }
  IvBox2609Need(caps.Count==2&&sides.Count==2,"Two caps/two cylinders");
  foreach(var e in edges) {
   IvBox2609Need(e.GetBody().Tag==body.Tag,"Foreign edge");
   ec.Add(e.Tag,IvAn2609Arc(e));uses.Add(e.Tag,0);
   var adj=e.GetFaces();IvBox2609Need(adj.Length==2&&adj[0].Tag!=adj[1].Tag,"Two distinct edge incidences");
   IvBox2609Need(fd.ContainsKey(adj[0].Tag)&&fd.ContainsKey(adj[1].Tag),"Foreign face");
   IvBox2609Need((fd[adj[0].Tag].type==22)!=(fd[adj[1].Tag].type==22),"Cap/side adjacency");
  }
  foreach(var f in faces) {
   var seen=new System.Collections.Generic.HashSet<NXOpen.Tag>();var fe=f.GetEdges();
   IvBox2609Need(fe.Length==2,"Extra trims or topological seams rejected");
   foreach(var e in fe) {
    IvBox2609Need(ec.ContainsKey(e.Tag)&&seen.Add(e.Tag),"Unknown/repeated edge");
    bool back=false;foreach(var a in e.GetFaces())if(a.Tag==f.Tag)back=true;
    IvBox2609Need(back,"Nonreciprocal incidence");uses[e.Tag]++;
   }
  }
  foreach(var u in uses)IvBox2609Need(u.Value==2,"Incomplete edge coverage");
  // Two concentric circular loops on each actual plane, with common cylinder axis.
  foreach(var cap in caps) {
   var ce=cap.f.GetEdges();var a=ec[ce[0].Tag];var b=ec[ce[1].Tag];
   IvPer2610Near(a.center,b.center,1e-7,"Nonconcentric cap");
   IvBox2609Need(Math.Abs(a.radius-b.radius)>1e-5,"Zero wall");
   var n=IvPer2610Unit(cap.dir);
   IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Sub(a.center,cap.point),n))<1e-7,"Circle off plane");
   IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Cross(a.x,a.y),n))>1-1e-9
    &&Math.Abs(IvBox2609Dot(IvBox2609Cross(b.x,b.y),n))>1-1e-9,"Cap circle axes");
  }
  var rows=new System.Collections.Generic.List<object>();var cancellations=new System.Collections.Generic.List<object>();
  foreach(var side in sides) {
   var se=side.f.GetEdges();var c0=ec[se[0].Tag];var c1=ec[se[1].Tag];
   var delta=IvBox2609Sub(c1.center,c0.center);double height=IvBox2609Norm(delta);
   var axis=IvPer2610Unit(delta);var z=IvBox2609Cross(c0.x,c0.y);
   IvBox2609Need(height>1e-5&&Math.Abs(c0.radius-c1.radius)<1e-7
    &&Math.Abs(side.radius-c0.radius)<1e-7,"Cylinder circle radii/span");
   IvBox2609Need(Math.Abs(IvBox2609Dot(z,axis))>1-1e-9
    &&Math.Abs(IvBox2609Dot(IvPer2610Unit(side.dir),axis))>1-1e-9
    &&Math.Abs(IvBox2609Dot(IvBox2609Cross(c1.x,c1.y),axis))>1-1e-9,"Cylinder/loop axis mismatch");
   var off=IvBox2609Sub(c0.center,side.point);
   IvPer2610Near(off,new double[]{axis[0]*IvBox2609Dot(off,axis),axis[1]*IvBox2609Dot(off,axis),axis[2]*IvBox2609Dot(off,axis)},1e-7,"Circle off cylinder axis");
   double[] bounds=new double[4];uf.Modl.AskFaceUvMinmax(side.f.Tag,bounds);IvBox2609Finite(bounds);
   int us,vs;double up,vp;uf.Modl.AskFacePeriodicity(side.f.Tag,out us,out up,out vs,out vp);
   // Positive finite native period selects axis; status retained for coordinator review.
   bool pu=us==1&&up>0&&!Double.IsInfinity(up)&&!Double.IsNaN(up);
   bool pv=vs==1&&vp>0&&!Double.IsInfinity(vp)&&!Double.IsNaN(vp);
   IvBox2609Need(pu!=pv,"Exactly one native periodic parameter");
   int pi=pu?0:1,qi=1-pi;double period=pu?up:vp;
   IvBox2609Need(Math.Abs(bounds[2*pi+1]-bounds[2*pi]-period)<1e-8*Math.Max(1,period),"UV not one period");
   var samples=new System.Collections.Generic.List<object>();int orientation=0;
   double nativeTol;uf.Modl.AskDistanceTolerance(out nativeTol);
   double eps=Math.Max(8*nativeTol,100*IvBox2609Tol);
   IvBox2609Need(eps<Math.Min(height,c0.radius)/10,"Unsafe containment witness offset");
   for(int i=0;i<=16;i++)for(int j=0;j<=4;j++) {
    double t=2*Math.PI*i/16.0,s=j/4.0;
    var x=IvPer2610Add(IvPer2610Circle(c0,t),delta,s);
    double[] uv=new double[2],projected=new double[3],q=new double[3],du=new double[3],dv=new double[3],normal=new double[3];
    uf.Modl.AskFaceParm2(side.f.Tag,x,uv,projected);
    uf.Modl.AskFaceProps(side.f.Tag,uv,q,du,dv,new double[3],new double[3],normal,new double[2]);
    IvBox2609Finite(uv);IvBox2609Finite(du);IvBox2609Finite(dv);IvBox2609Finite(normal);
    IvPer2610Near(x,projected,1e-7,"Inverse projection mismatch");IvPer2610Near(x,q,1e-7,"Native correspondence mismatch");
    var dt=new double[]{c0.radius*(-c0.x[0]*Math.Sin(t)+c0.y[0]*Math.Cos(t)),
     c0.radius*(-c0.x[1]*Math.Sin(t)+c0.y[1]*Math.Cos(t)),
     c0.radius*(-c0.x[2]*Math.Sin(t)+c0.y[2]*Math.Cos(t))};
    var cross=IvBox2609Cross(dt,delta);var n=IvPer2610Unit(cross);
    var nativeCross=IvPer2610Unit(IvBox2609Cross(du,dv));
    IvBox2609Need(Math.Abs(IvBox2609Dot(nativeCross,n))>1-1e-8,"Derivative tangent-plane disagreement");
    var dtheta=pu?du:dv;var daxial=pu?dv:du;
    IvBox2609Need(Math.Abs(IvBox2609Dot(IvPer2610Unit(dtheta),IvPer2610Unit(dt)))>1-1e-8
     &&Math.Abs(IvBox2609Dot(IvPer2610Unit(daxial),axis))>1-1e-8,"Native UV axes not separable cylinder");
    IvBox2609Need(Math.Abs(IvBox2609Norm(dtheta)*period-2*Math.PI*c0.radius)<1e-6,"Periodic derivative scaling");
    IvBox2609Need(Math.Abs(IvBox2609Norm(daxial)*(bounds[2*qi+1]-bounds[2*qi])-height)<1e-6,"Axial derivative scaling");
    int trim=IvTrim2610Status(q,side.f.Tag);IvBox2609Need(trim==1||trim==3,"Point outside side trim");
    int plus=0,minus=0,sgn=0;
    if(j>0&&j<4) {
     plus=IvTrim2610Status(IvPer2610Add(q,n,eps),body.Tag);
     minus=IvTrim2610Status(IvPer2610Add(q,n,-eps),body.Tag);
     IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Containment cannot orient");
     sgn=plus==2?1:-1;if(orientation==0)orientation=sgn;
     IvBox2609Need(orientation==sgn,"Inconsistent side orientation");
    }
    // Seam endpoints evaluated at both native period bounds, not merely inverse-mapped.
    var ua=(double[])uv.Clone();var ub=(double[])uv.Clone();
    ua[pi]=bounds[2*pi];ub[pi]=bounds[2*pi+1];
    double[] qa=new double[3],qb=new double[3],au=new double[3],av=new double[3],bu=new double[3],bv=new double[3];
    uf.Modl.AskFaceProps(side.f.Tag,ua,qa,au,av,new double[3],new double[3],new double[3],new double[2]);
    uf.Modl.AskFaceProps(side.f.Tag,ub,qb,bu,bv,new double[3],new double[3],new double[3],new double[2]);
    IvPer2610Near(qa,qb,1e-7,"Open parameter seam");IvPer2610Near(au,bu,1e-7,"Seam du mismatch");IvPer2610Near(av,bv,1e-7,"Seam dv mismatch");
    samples.Add(new{theta=t,s=s,uv=uv,point=q,du=du,dv=dv,native_normal=normal,dt=dt,ds=delta,
     trim_status=trim,plus_status=plus,minus_status=minus,orientation=sgn,offset_mm=eps,
     seam_uv_a=ua,seam_uv_b=ub,seam_point_a=qa,seam_point_b=qb,seam_du_a=au,seam_du_b=bu,seam_dv_a=av,seam_dv_b=bv});
   }
   // Oriented patch boundary: at s=0 +theta, at s=1 -theta (times orientation).
   // Compare this induced direction with independently outward-oriented planar annulus.
   for(int k=0;k<2;k++) {
    var c=k==0?c0:c1;IvAn2609Face cap=null;
    foreach(var a in c.e.GetFaces())if(fd[a.Tag].type==22)cap=fd[a.Tag];
    IvBox2609Need(cap!=null,"Missing cap");
    var ce=cap.f.GetEdges();var other=ec[ce[0].Tag==c.e.Tag?ce[1].Tag:ce[0].Tag];
    bool outer=c.radius>other.radius;double rmid=(c.radius+other.radius)*0.5;
    var test=IvPer2610Add(c.center,c0.x,rmid);var n=IvPer2610Unit(cap.dir);
    int plus=IvTrim2610Status(IvPer2610Add(test,n,eps),body.Tag);
    int minus=IvTrim2610Status(IvPer2610Add(test,n,-eps),body.Tag);
    IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Cap normal ambiguous");
    if(plus==1)n=new double[]{-n[0],-n[1],-n[2]};
    int capSense=(IvBox2609Dot(z,n)>0?1:-1)*(outer?1:-1);
    int sideSense=orientation*(k==0?1:-1);
    IvBox2609Need(capSense+sideSense==0,"Cap/side oriented boundary does not cancel");
    cancellations.Add(new{edge_tag=c.e.Tag.ToString(),cap_tag=cap.f.Tag.ToString(),side_tag=side.f.Tag.ToString(),
     cap_sense_in_common_circle_frame=capSense,side_sense_in_common_circle_frame=sideSense,
     cap_normal=n,cap_plus_status=plus,cap_minus_status=minus});
   }
   rows.Add(new{face_tag=side.f.Tag.ToString(),type=side.type,native_sense_metadata=side.sense,
    edge_tags=new string[]{c0.e.Tag.ToString(),c1.e.Tag.ToString()},center=c0.center,x_axis=c0.x,y_axis=c0.y,
    radius=c0.radius,delta=delta,height=height,orientation=orientation,uv_bounds=bounds,
    u_status=us,u_period=up,v_status=vs,v_period=vp,periodic_axis=pi,
    witnesses=samples,domain="theta=[0,2*pi],s=[0,1]; analytic circle translation; seam identified once"});
  }
  result=new{schema_version=1,family="checked_annular_analytic_fixture",part_path=p.FullPath,body_tag=body.Tag.ToString(),
   source_sha256=before,cap_receipt_json=capReceipt,sides=rows,boundary_cancellation=cancellations,
   closure_scope="Only exact guarded healthy four-face/four-full-circle analytic fixture. Complete edge inventory, reciprocal cap/side incidences, concentric planar rings, coaxial cylinder circles, one periodic cover and induced orientation cancellation. Witness grid alone is not a general trim proof.",
   native_volume_used_in_integrand=false};
 }catch(Exception e){error=e.Message;}
 finally {
  try {unchanged=before!=null&&IvBox2609Hash(p.FullPath)==before
    &&Convert.ToInt32(uf.Part.IsModified(p.Tag))==0
    &&System.Linq.Enumerable.SequenceEqual(inventory,IvBox2609Inventory(p));}
  catch(Exception e){auditError=e.Message;}
 }
 return Json(new{ok=error==null&&auditError==null&&unchanged,error=error,audit_error=auditError,
  unchanged=unchanged,read_only_query=true,boundary=result,
  scope="Read-only periodic side boundary and orientation evidence."});
}
