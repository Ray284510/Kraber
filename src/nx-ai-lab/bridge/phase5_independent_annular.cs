// Append INSIDE NX_MCP_Loader after unchanged box and analytic helpers.
// SOURCE ONLY: compilation and native execution NOTRUN.
// Dispatch: case "nx_independent_annular_caps": return IvTrim2610Read(parts);
private const string IvTrim2610Path=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\independent_volume_20261109\phase4_iv_b2_annular_20261110_v1.prt";
private static void IvTrim2610Guard(NXOpen.Part p) {
 IvBox2609Guard(p);
 IvBox2609Need(String.Equals(System.IO.Path.GetFullPath(p.FullPath),IvTrim2610Path,StringComparison.OrdinalIgnoreCase),"Exact B2 fixture required");
}
private static int IvTrim2610Status(double[] x,NXOpen.Tag t) {
 int s;NXOpen.UF.UFSession.GetUFSession().Modl.AskPointContainment(x,t,out s);return s;
}
private static object IvTrim2610Loop(IvAn2609Circle c,string role,double[] n) {
 double sign=IvBox2609Dot(IvBox2609Cross(c.x,c.y),n);
 IvBox2609Need(Math.Abs(sign)>1-1e-10,"Circle/plane axes disagree");
 bool forward=(sign>0)==(role=="outer");
 return new{edge_tag=c.e.Tag.ToString(),role=role,center=c.center,radius=c.radius,
 x_axis=c.x,y_axis=c.y,periodic=c.periodic,arc_limits=c.limits,evaluator_limits=c.evalLimits,
 start=forward?0.0:2*Math.PI,end=forward?2*Math.PI:0.0,
 parameterization="analytic theta from native full circle; not evaluator parameter",
 closure_evidence="Native periodic arc and evaluator spans both 2*pi; coincident edge vertices; one use of this edge.",
 sense_evidence="Traversal selected from dot(cross(x_axis,y_axis),outward_normal); outer CCW, hole CW."};
}
private static string IvTrim2610Read(string[] args) {
 var p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string bh=null,ah=null,error=null,auditError=null;string[] bi=null,ai=null;int? bm=null,am=null;
 bool unchanged=false;object boundary=null;
 try {
  IvBox2609Need(args.Length==1,"No query arguments allowed");IvTrim2610Guard(p);
  bh=IvBox2609Hash(p.FullPath);bm=Convert.ToInt32(uf.Part.IsModified(p.Tag));bi=IvBox2609Inventory(p);
  IvBox2609Need(bm==0,"Saved fixture required");
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid required");var body=bodies[0];
  int nf;int[] faults;NXOpen.Tag[] bad;uf.Modl.AskBodyConsistency(body.Tag,out nf,out faults,out bad);
  IvBox2609Need(nf==0,"Body faults");
  foreach(NXOpen.Features.Feature f in p.Features)
   IvBox2609Need(!f.Suppressed&&f.GetFeatureErrorMessages().Length==0&&f.GetFeatureWarningMessages().Length==0,"Unhealthy feature");
  NXOpen.Tag[] failed;uf.Modl.AskFeatFailList(out failed);IvBox2609Need(failed.Length==0,"Failed features");
  var faces=body.GetFaces();var edges=body.GetEdges();
  IvBox2609Need(faces.Length==4&&edges.Length==4,"Only unseamed four-face/four-circle hollow fixture supported");
  var data=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Face>();
  var circles=new System.Collections.Generic.Dictionary<NXOpen.Tag,IvAn2609Circle>();
  var uses=new System.Collections.Generic.Dictionary<NXOpen.Tag,int>();
  var incidence=new System.Collections.Generic.List<object>();int capCount=0,sideCount=0;
  foreach(var f in faces) {
   IvBox2609Need(!data.ContainsKey(f.Tag),"Duplicate face");var d=IvAn2609Data(f);data.Add(f.Tag,d);
   if(d.type==22)capCount++;else {IvBox2609Need(d.type==16,"Nonanalytic side");sideCount++;}
  }
  IvBox2609Need(capCount==2&&sideCount==2,"Two planes and two cylinders required");
  foreach(var e in edges) {
   IvBox2609Need(e.GetBody().Tag==body.Tag&&!circles.ContainsKey(e.Tag),"Foreign/duplicate edge");
   circles.Add(e.Tag,IvAn2609Arc(e));uses.Add(e.Tag,0);
   var fs=e.GetFaces();IvBox2609Need(fs.Length==2&&fs[0].Tag!=fs[1].Tag,"Two distinct incidences required");
   IvBox2609Need(data.ContainsKey(fs[0].Tag)&&data.ContainsKey(fs[1].Tag),"Foreign incidence");
   IvBox2609Need((data[fs[0].Tag].type==22)!=(data[fs[1].Tag].type==22),"Cap/side incidence required");
   incidence.Add(new{edge_tag=e.Tag.ToString(),face_tags=new string[]{fs[0].Tag.ToString(),fs[1].Tag.ToString()}});
  }
  foreach(var f in faces) {
   var fe=f.GetEdges();IvBox2609Need(fe.Length==2,"Extra trims/seams rejected");
   var seen=new System.Collections.Generic.HashSet<NXOpen.Tag>();
   foreach(var e in fe) {
    IvBox2609Need(circles.ContainsKey(e.Tag)&&seen.Add(e.Tag),"Duplicate/unknown face edge");
    bool reciprocal=false;foreach(var q in e.GetFaces())if(q.Tag==f.Tag)reciprocal=true;
    IvBox2609Need(reciprocal,"Nonreciprocal incidence");uses[e.Tag]++;
   }
  }
  foreach(int count in uses.Values)IvBox2609Need(count==2,"Incomplete incidence");
  double tol;uf.Modl.AskDistanceTolerance(out tol);
  IvBox2609Need(tol>0&&!Double.IsInfinity(tol),"Invalid model tolerance");double offset=Math.Max(8*tol,100*IvBox2609Tol);
  var caps=new System.Collections.Generic.List<object>();
  foreach(var f in data.Values)if(f.type==22) {
   var fe=f.f.GetEdges();var inner=circles[fe[0].Tag];var outer=circles[fe[1].Tag];
   if(inner.radius>outer.radius){var t=inner;inner=outer;outer=t;}
   IvBox2609Need(inner.radius>10*offset&&outer.radius-inner.radius>20*offset,"Unresolved annular gap");
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(inner.center,outer.center))<=IvBox2609Tol,"Nonconcentric loops");
   // AskFaceData direction can already carry face sense. Establish outward independently.
   var axisNormal=IvBox2609Unit(f.dir);
   var mid=IvBox2609Add(outer.center,outer.x,(inner.radius+outer.radius)/2);
   int plus=IvTrim2610Status(IvBox2609Add(mid,axisNormal,offset),body.Tag);
   int minus=IvTrim2610Status(IvBox2609Add(mid,axisNormal,-offset),body.Tag);
   IvBox2609Need((plus==2&&minus==1)||(plus==1&&minus==2),"Cap normal does not separate solid and exterior");
   var n=IvBox2609Add(new double[3],axisNormal,plus==2?1:-1);
   foreach(var c in new IvAn2609Circle[]{outer,inner}) {
    IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Sub(c.center,f.point),n))<=IvBox2609Tol&&
     Math.Abs(IvBox2609Dot(IvBox2609Cross(c.x,c.y),n))>1-1e-10,"Noncoplanar circle");
    foreach(var ef in c.e.GetFaces())if(ef.Tag!=f.f.Tag) {
     var side=data[ef.Tag];var axis=IvBox2609Unit(side.dir);
     double z=IvBox2609Dot(IvBox2609Sub(c.center,side.point),axis);
     IvBox2609Need(Math.Abs(side.radius-c.radius)<=IvBox2609Tol&&
      Math.Abs(IvBox2609Dot(axis,n))>1-1e-10&&
      IvBox2609Norm(IvBox2609Sub(c.center,IvBox2609Add(side.point,axis,z)))<=IvBox2609Tol,"Side/circle mismatch");
    }
   }
   var witnesses=new System.Collections.Generic.List<object>();var exclusions=new System.Collections.Generic.List<object>();
   foreach(double theta in new double[]{.37,2.1,4.3}) {
    var radial=IvBox2609Add(IvBox2609Add(new double[3],outer.x,Math.Cos(theta)),outer.y,Math.Sin(theta));
    foreach(double fraction in new double[]{.27,.63}) {
     var x=IvBox2609Add(outer.center,radial,inner.radius+(outer.radius-inner.radius)*fraction);
     witnesses.Add(IvAn2609Witness(f,body,x,n,offset));
    }
    foreach(double radius in new double[]{inner.radius/2,outer.radius+10*offset}) {
     var x=IvBox2609Add(outer.center,radial,radius);
     int fs=IvTrim2610Status(x,f.f.Tag),bs=IvTrim2610Status(x,body.Tag);
     IvBox2609Need(fs==2&&bs==2,"Hole/exterior not excluded");
     exclusions.Add(new{point=x,face_status=fs,body_status=bs});
    }
   }
   string proof="Bounded planar face has exactly two distinct full circular edges, each one closed simple loop; concentric coplanar radii are strictly ordered. Complete reciprocal edge inventory excludes additional boundary components. Native annular witnesses lie inside the face; hole and exterior witnesses lie outside. This establishes only this connected annular cap under the checked analytic topology.";
   caps.Add(new{face_tag=f.f.Tag.ToString(),point=outer.center,normal=n,native_point=f.point,native_direction=f.dir,native_sense=f.sense,direction_plus_status=plus,direction_minus_status=minus,
    loops=new object[]{IvTrim2610Loop(outer,"outer",n),IvTrim2610Loop(inner,"hole",n)},witnesses=witnesses,exclusions=exclusions,
    topology=new{coverage_evidence=proof,simplicity_evidence="Each positive-radius full circle is simple and traversed once.",
     disjoint_evidence="Concentric circles with strictly separated radii do not intersect.",
     containment_evidence="Smaller measured radius lies strictly inside larger measured radius; native hole exclusion corroborates."}});
  }
  boundary=new{schema_version=1,family="annular_planar_caps_only",units="mm",part_path=p.FullPath,body_tag=body.Tag.ToString(),
   caps=caps,incidence=incidence,consistency_fault_count=nf,failed_feature_count=failed.Length,
   native_distance_tolerance_mm=tol,geometric_comparison_tolerance_mm=IvBox2609Tol,
   whole_body_closure_proven=false,side_faces_integrated=false,native_mass_used=false};
 }catch(Exception e){error=e.Message;}
 if(bh!=null&&bi!=null)try {
  IvTrim2610Guard(p);am=Convert.ToInt32(uf.Part.IsModified(p.Tag));ai=IvBox2609Inventory(p);ah=IvBox2609Hash(p.FullPath);
  unchanged=bh==ah&&bm==0&&am==0&&String.Join("\n",bi)==String.Join("\n",ai);
  if(!unchanged)auditError="Read-only invariant failed";
 }catch(Exception e){auditError=e.Message;}
 bool ok=error==null&&auditError==null&&unchanged&&boundary!=null;
 return Json(new{ok=ok,command="nx_independent_annular_caps",read_only_query=true,error=error,audit_error=auditError,
  boundary=ok?boundary:null,audit=new{sha256_before=bh,sha256_after=ah,modified_before=bm,modified_after=am,
   objects_before=bi,objects_after=ai,unchanged=unchanged},safe_pause_required=auditError!=null,retry_automatically=false,
  volume_computed=false,native_verified=false});
}
