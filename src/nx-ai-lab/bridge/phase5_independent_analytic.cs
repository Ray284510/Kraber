// Append inside NX_MCP_Loader AFTER unchanged phase5_independent_box.cs.
// SOURCE ONLY / NOTRUN. Dispatch: case "nx_independent_analytic_boundary": return IvAn2609Read(parts);
private sealed class IvAn2609Face {
 public NXOpen.Face f; public int type,sense;
 public double[] point=new double[3],dir=new double[3],box=new double[6];
 public double radius,minor;
}
private static IvAn2609Face IvAn2609Data(NXOpen.Face f) {
 var q=new IvAn2609Face();q.f=f;
 NXOpen.UF.UFSession.GetUFSession().Modl.AskFaceData(f.Tag,out q.type,q.point,q.dir,q.box,out q.radius,out q.minor,out q.sense);
 IvBox2609Finite(q.point);IvBox2609Finite(q.box);
 IvBox2609Need(q.sense==1||q.sense==-1,"Unknown native face sense");
 return q;
}
// NX12 XML confirms AskArc signature, C header defines these field names.
// Fail closed if managed struct layout differs; never infer alternative fields.
private static T IvAn2609Field<T>(object value,string name) {
 var f=value.GetType().GetField(name);
 IvBox2609Need(f!=null&&f.FieldType==typeof(T),"Unsupported NX12 Arc field: "+name);
 return (T)f.GetValue(value);
}
private static double[] IvAn2609Array(object value,string name,int n) {
 var a=IvAn2609Field<double[]>(value,name);
 IvBox2609Need(a!=null&&a.Length==n,"Invalid Arc array: "+name);
 IvBox2609Finite(a);return a;
}
private sealed class IvAn2609Circle {
 public NXOpen.Edge e;public double[] center,x,y,limits,evalLimits;
 public double radius;public bool periodic;
}
private static IvAn2609Circle IvAn2609Arc(NXOpen.Edge e) {
 var uf=NXOpen.UF.UFSession.GetUFSession();IntPtr handle=IntPtr.Zero;
 var c=new IvAn2609Circle();c.e=e;
 try {
  uf.Eval.Initialize(e.Tag,out handle);
  IvBox2609Need(handle!=IntPtr.Zero,"No curve evaluator");
  bool isArc;uf.Eval.IsArc(handle,out isArc);IvBox2609Need(isArc,"Noncircular edge unsupported");
  NXOpen.UF.UFEval.Arc arc;uf.Eval.AskArc(handle,out arc);
  c.periodic=IvAn2609Field<bool>(arc,"is_periodic");
  c.radius=IvAn2609Field<double>(arc,"radius");
  c.center=IvAn2609Array(arc,"center",3);c.x=IvAn2609Array(arc,"x_axis",3);c.y=IvAn2609Array(arc,"y_axis",3);
  c.limits=IvAn2609Array(arc,"limits",2);c.evalLimits=new double[2];uf.Eval.AskLimits(handle,c.evalLimits);
  IvBox2609Finite(c.evalLimits);
  IvBox2609Need(c.periodic&&Math.Abs(c.limits[1]-c.limits[0]-2*Math.PI)<1e-10&&
   Math.Abs(c.evalLimits[1]-c.evalLimits[0]-2*Math.PI)<1e-10,"Partial/repeated circle unsupported");
  IvBox2609Need(Math.Abs(IvBox2609Norm(c.x)-1)<1e-10&&Math.Abs(IvBox2609Norm(c.y)-1)<1e-10&&
   Math.Abs(IvBox2609Dot(c.x,c.y))<1e-10,"Invalid circle frame");
  IvBox2609Need(c.radius>0&&c.radius<=1e6,"Invalid circle radius");
  NXOpen.Point3d a,b;e.GetVertices(out a,out b);
  IvBox2609Need(IvBox2609Norm(IvBox2609Sub(IvBox2609V(a),IvBox2609V(b)))<=IvBox2609Tol,"Open circular edge");
  return c;
 } finally {if(handle!=IntPtr.Zero)uf.Eval.Free(handle);}
}
private static object IvAn2609Witness(IvAn2609Face f,NXOpen.Body body,double[] x,double[] outward,double offset) {
 var uf=NXOpen.UF.UFSession.GetUFSession();
 double[] uv=new double[2],projected=new double[3],p=new double[3],du=new double[3],dv=new double[3],
  du2=new double[3],dv2=new double[3],normal=new double[3],radii=new double[2];
 uf.Modl.AskFaceParm2(f.f.Tag,x,uv,projected);
 uf.Modl.AskFaceProps(f.f.Tag,uv,p,du,dv,du2,dv2,normal,radii);
 IvBox2609Finite(uv);IvBox2609Finite(p);IvBox2609Finite(projected);
 IvBox2609Finite(du);IvBox2609Finite(dv);IvBox2609Finite(normal);
 IvBox2609Need(IvBox2609Norm(IvBox2609Sub(x,p))<=IvBox2609Tol&&
  IvBox2609Norm(IvBox2609Sub(x,projected))<=IvBox2609Tol,"Analytic/native position mismatch");
 var n=IvBox2609Unit(normal);var cross=IvBox2609Unit(IvBox2609Cross(du,dv));
 IvBox2609Need(f.sense*IvBox2609Dot(cross,n)>1-1e-9&&IvBox2609Dot(n,outward)>1-1e-9,"Derivative/sense/outward mismatch");
 int fc,bc;uf.Modl.AskPointContainment(x,f.f.Tag,out fc);uf.Modl.AskPointContainment(x,body.Tag,out bc);
 IvBox2609Need(fc==1&&bc==3,"Witness outside trimmed face or off body");
 var offsets=new System.Collections.Generic.List<object>();
 foreach(double d in new double[]{offset/2,offset}) {
  var inside=IvBox2609Add(x,n,-d);var outside=IvBox2609Add(x,n,d);int si,so;
  uf.Modl.AskPointContainment(inside,body.Tag,out si);uf.Modl.AskPointContainment(outside,body.Tag,out so);
  offsets.Add(new{offset_mm=d,inside=inside,outside=outside,inside_status=si,outside_status=so});
  IvBox2609Need(si==1&&so==2,"Native normal does not separate inside/outside");
 }
 return new{point=x,native_uv=uv,projected=projected,evaluated=p,native_du=du,native_dv=dv,
  native_normal=normal,native_sense=f.sense,analytic_outward=outward,face_status=fc,body_status=bc,offsets=offsets};
}
private static object IvAn2609Patch(string name,string kind,double[] o,double[] a,double[] b,double[] axis,double r,double h,int sense,string proof) {
 double[] bounds=kind=="sphere"?new double[]{0,2*Math.PI,-Math.PI/2,Math.PI/2}:
  new double[]{0,2*Math.PI,0,kind=="cylinder"?h:r};
 return new{name=name,kind=kind,origin=o,a=a,b=b,axis=axis,radius=r,height=h,sense=sense,bounds=bounds,
  domain=new{kind="full_rectangle",no_exclusions=true,single_cover=true,source=proof,
   coordinates="analytic reparameterization from native geometry, not native UV",
   periodic_seam="theta=0 and theta=2*pi identified; endpoint duplication has zero area",
   singularities=kind=="sphere"?"latitude endpoints: zero area poles":kind=="disk"?"rho=0: zero area center":"none"}};
}
private static string IvAn2609Read(string[] args) {
 NXOpen.Part p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string bh=null,ah=null,error=null,auditError=null;string[] bi=null,ai=null;int? bm=null,am=null;
 object boundary=null;bool unchanged=false;
 var evidence=new System.Collections.Generic.List<object>();var circles=new System.Collections.Generic.List<object>();
 var features=new System.Collections.Generic.List<object>();
 try {
  IvBox2609Need(args.Length==1,"nx_independent_analytic_boundary accepts no arguments");IvBox2609Guard(p);
  bh=IvBox2609Hash(p.FullPath);bm=Convert.ToInt32(uf.Part.IsModified(p.Tag));bi=IvBox2609Inventory(p);
  IvBox2609Need(bm==0,"Unsaved modifications rejected");
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid required");var body=bodies[0];
  int nf;int[] faults;NXOpen.Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out nf,out faults,out bad);
  IvBox2609Need(nf==0,"Body consistency failure");
  foreach(NXOpen.Features.Feature f in p.Features) {
   var errors=f.GetFeatureErrorMessages();var warnings=f.GetFeatureWarningMessages();
   features.Add(new{tag=f.Tag.ToString(),type=f.FeatureType,errors=errors,warnings=warnings,suppressed=f.Suppressed});
   IvBox2609Need(!f.Suppressed&&errors.Length==0&&warnings.Length==0,"Feature health failure");
  }
  NXOpen.Tag[] failed;uf.Modl.AskFeatFailList(out failed);IvBox2609Need(failed.Length==0,"Failed feature list nonempty");
  var faces=body.GetFaces();var edges=body.GetEdges();
  var data=new System.Collections.Generic.List<IvAn2609Face>();
  var ft=new System.Collections.Generic.HashSet<NXOpen.Tag>();
  foreach(var f in faces){IvBox2609Need(ft.Add(f.Tag),"Duplicate face");data.Add(IvAn2609Data(f));}
  double tol;uf.Modl.AskDistanceTolerance(out tol);
  IvBox2609Need(tol>0&&!Double.IsInfinity(tol),"Unknown distance tolerance");
  double offset=Math.Max(8*tol,100*IvBox2609Tol);
  var patches=new System.Collections.Generic.List<object>();var senses=new System.Collections.Generic.Dictionary<string,int>();
  string family,proof;double[] center;double radius,height=0;
  if(faces.Length==1&&edges.Length==0&&data[0].type==18) {
   family="sphere";var f=data[0];
   IvBox2609Need(f.f.GetEdges().Length==0,"Sphere seam/trim unsupported");
   center=f.point;radius=f.radius;
   IvBox2609Need(radius>10*offset&&radius<=1e6,"Sphere radius ill-conditioned");
   proof="Sole consistent solid; sole native type18 spherical face; complete body and face edge sets empty. A proper trimmed subset requires a boundary; seam-bearing representations rejected. Native witnesses corroborate orientation, not coverage.";
   double[] a={1,0,0},b={0,1,0},axis={0,0,1};
   var witnesses=new System.Collections.Generic.List<object>();
   foreach(double theta in new double[]{.37,2.1,4.3})foreach(double lat in new double[]{-.43,.19,.57}) {
    double[] n={Math.Cos(lat)*Math.Cos(theta),Math.Cos(lat)*Math.Sin(theta),Math.Sin(lat)};
    witnesses.Add(IvAn2609Witness(f,body,IvBox2609Add(center,n,radius),n,offset));
   }
   string name="face_"+f.f.Tag;senses.Add(name,1);
   patches.Add(IvAn2609Patch(name,"sphere",center,a,b,axis,radius,0,1,proof));
   evidence.Add(new{face_tag=f.f.Tag.ToString(),native_type=f.type,native_point=f.point,native_direction=f.dir,
    native_radius=f.radius,native_bounds=f.box,native_sense=f.sense,edge_tags=new string[0],witnesses=witnesses});
  } else {
   family="cylinder";IvBox2609Need(faces.Length==3&&edges.Length==2,"Only unseamed pure cylinder or sphere supported");
   IvAn2609Face side=null;var caps=new System.Collections.Generic.List<IvAn2609Face>();
   foreach(var f in data) {if(f.type==16){IvBox2609Need(side==null,"Multiple cylinders");side=f;}
    else {IvBox2609Need(f.type==22,"Nonplane cap unsupported");caps.Add(f);}}
   IvBox2609Need(side!=null&&caps.Count==2,"Cylinder and two planar caps required");
   var axis=IvBox2609Unit(side.dir);radius=side.radius;
   IvBox2609Need(radius>10*offset&&radius<=1e6,"Cylinder radius ill-conditioned");
   var ce=new IvAn2609Circle[2];var et=new System.Collections.Generic.HashSet<NXOpen.Tag>();
   for(int k=0;k<2;k++) {
    var e=edges[k];IvBox2609Need(et.Add(e.Tag)&&e.GetBody().Tag==body.Tag,"Duplicate/foreign edge");
    ce[k]=IvAn2609Arc(e);
    var ef=e.GetFaces();IvBox2609Need(ef.Length==2&&ef[0].Tag!=ef[1].Tag,"Two distinct incidences required");
    bool hasSide=false,hasCap=false;
    foreach(var f in ef){IvBox2609Need(ft.Contains(f.Tag),"Foreign incident face");if(f.Tag==side.f.Tag)hasSide=true;else hasCap=true;}
    IvBox2609Need(hasSide&&hasCap,"Cap/side incidence required");
    IvBox2609Need(Math.Abs(ce[k].radius-radius)<=IvBox2609Tol,"Circle/cylinder radius mismatch");
    double z=IvBox2609Dot(IvBox2609Sub(ce[k].center,side.point),axis);
    IvBox2609Need(IvBox2609Norm(IvBox2609Sub(ce[k].center,IvBox2609Add(side.point,axis,z)))<=IvBox2609Tol&&
     Math.Abs(IvBox2609Dot(IvBox2609Cross(ce[k].x,ce[k].y),axis))>1-1e-10,"Circle not coaxial");
    circles.Add(new{edge_tag=e.Tag.ToString(),center=ce[k].center,radius=ce[k].radius,x_axis=ce[k].x,y_axis=ce[k].y,
     periodic=ce[k].periodic,arc_limits=ce[k].limits,evaluator_limits=ce[k].evalLimits,
     face_tags=new string[]{ef[0].Tag.ToString(),ef[1].Tag.ToString()}});
   }
   var uses=new System.Collections.Generic.Dictionary<NXOpen.Tag,int>();foreach(var e in edges)uses.Add(e.Tag,0);
   var capCircles=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Circle>();
   foreach(var f in data) {
    var fe=f.f.GetEdges();IvBox2609Need(fe.Length==(f==side?2:1),"Extra trim/hole/seam rejected");
    var local=new System.Collections.Generic.HashSet<NXOpen.Tag>();
    foreach(var e in fe) {
     IvBox2609Need(et.Contains(e.Tag)&&local.Add(e.Tag),"Unknown/duplicate face edge");
     bool reciprocal=false;foreach(var q in e.GetFaces())if(q.Tag==f.f.Tag)reciprocal=true;
     IvBox2609Need(reciprocal,"Nonreciprocal incidence");uses[e.Tag]++;
     if(f!=side)capCircles.Add(f.f.Tag,ce[0].e.Tag==e.Tag?ce[0]:ce[1]);
    }
   }
   foreach(var n in uses.Values)IvBox2609Need(n==2,"Incomplete incidence");
   IvBox2609Need(capCircles[caps[0].f.Tag].e.Tag!=capCircles[caps[1].f.Tag].e.Tag,"Caps share same loop");
   if(IvBox2609Dot(IvBox2609Sub(ce[1].center,ce[0].center),axis)<0){var t=ce[0];ce[0]=ce[1];ce[1]=t;}
   var origin=ce[0].center;height=IvBox2609Dot(IvBox2609Sub(ce[1].center,origin),axis);
   IvBox2609Need(height>10*offset&&height<=1e6,"Height ill-conditioned");
   var a=ce[0].x;var b=IvBox2609Unit(IvBox2609Cross(axis,a));center=IvBox2609Add(origin,axis,height/2);
   proof="Sole consistent solid; complete reciprocal 3-face/2-edge incidence; two distinct periodic full 2*pi native circular loops coaxial with type16 side; each type22 cap has exactly one loop. Bounded planar disks and periodic cylindrical band give complete boundary; no extra holes, seams or sector edges. Finite witnesses only corroborate orientation.";
   foreach(var f in data) {
    var witnesses=new System.Collections.Generic.List<object>();string name="face_"+f.f.Tag;
    bool isSide=f==side;var o=isSide?origin:capCircles[f.f.Tag].center;
    int sense=1;
    if(!isSide) {
     var pn=IvBox2609Unit(f.dir);
     IvBox2609Need(Math.Abs(IvBox2609Dot(pn,axis))>1-1e-10&&
      Math.Abs(IvBox2609Dot(IvBox2609Sub(o,f.point),pn))<=IvBox2609Tol,"Cap plane mismatch");
     sense=capCircles[f.f.Tag].e.Tag==ce[0].e.Tag?1:-1; // theta,rho cross = -axis
    }
    foreach(double theta in new double[]{.37,2.1,4.3})foreach(double t in new double[]{.27,.63}) {
     var radial=IvBox2609Add(new double[3],a,Math.Cos(theta));radial=IvBox2609Add(radial,b,Math.Sin(theta));
     var x=IvBox2609Add(o,radial,isSide?radius:radius*t);
     if(isSide)x=IvBox2609Add(x,axis,height*t);
     var n=isSide?radial:IvBox2609Add(new double[3],axis,-sense);
     witnesses.Add(IvAn2609Witness(f,body,x,n,offset));
    }
    senses.Add(name,sense);patches.Add(IvAn2609Patch(name,isSide?"cylinder":"disk",o,a,b,axis,radius,height,sense,proof));
    var tags=new System.Collections.Generic.List<string>();foreach(var e in f.f.GetEdges())tags.Add(e.Tag.ToString());
    evidence.Add(new{face_tag=f.f.Tag.ToString(),native_type=f.type,native_point=f.point,native_direction=f.dir,
     native_radius=f.radius,native_bounds=f.box,native_sense=f.sense,edge_tags=tags,witnesses=witnesses});
   }
  }
  int inside;uf.Modl.AskPointContainment(center,body.Tag,out inside);IvBox2609Need(inside==1,"Analytic center not strictly inside");
  boundary=new{schema_version=1,units="mm",family=family,body_tag=body.Tag.ToString(),patches=patches,
   center=center,radius=radius,height=height,center_containment=inside,native_distance_tolerance_mm=tol,
   geometric_comparison_tolerance_mm=IvBox2609Tol,consistency_fault_count=nf,failed_feature_count=failed.Length,
   topology=new{closed=true,oriented=true,complete=true,senses=senses,source="nx_independent_analytic_boundary:v1",
    proof=proof,face_count=faces.Length,edge_count=edges.Length,
    orientation_proof="Outward analytic patches checked against native derivative cross times face sense and two-scale containment; periodic seams cancel; cap/side circle orientations oppose."},
   volume_computed=false,native_verified=false};
 }catch(Exception e){error=e.Message;}
 if(bh!=null&&bi!=null)try {
  IvBox2609Guard(p);am=Convert.ToInt32(uf.Part.IsModified(p.Tag));ai=IvBox2609Inventory(p);ah=IvBox2609Hash(p.FullPath);
  unchanged=bh==ah&&bm==0&&am==0&&String.Join("\n",bi)==String.Join("\n",ai);
  if(!unchanged)auditError="Read-only invariant failed";
 }catch(Exception e){auditError=e.Message;}
 bool ok=error==null&&auditError==null&&unchanged&&boundary!=null;
 return Json(new{ok=ok,command="nx_independent_analytic_boundary",read_only_query=true,error=error,audit_error=auditError,
  boundary=ok?boundary:null,face_evidence=evidence,edge_evidence=circles,features=features,
  audit=new{sha256_before=bh,sha256_after=ah,modified_before=bm,modified_after=am,objects_before=bi,objects_after=ai,unchanged=unchanged},
  safe_pause_required=auditError!=null,retry_automatically=false,volume_computed=false,native_verified=false,
  limitations="Only consistent unseamed analytic sphere or pure capped cylinder. Tolerance-based geometry; no rigorous geometric or quadrature error bound. Native compilation/testing NOTRUN."});
}
