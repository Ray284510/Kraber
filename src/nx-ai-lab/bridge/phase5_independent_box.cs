// Append inside NX_MCP_Loader. SOURCE ONLY: compile and native acceptance NOTRUN.
// Unique IvBox2609 prefix; only existing _part and Json are reused.
private const double IvBox2609Tol=1e-7;
private sealed class IvBox2609Edge {
 public NXOpen.Edge edge; public string tag; public double[] a,b;
 public int i,j; public string[] faces;
}
private static void IvBox2609Need(bool ok,string why) {
 if(!ok)throw new System.InvalidOperationException(why);
}
private static double[] IvBox2609V(NXOpen.Point3d p) {return new double[]{p.X,p.Y,p.Z};}
private static double[] IvBox2609Sub(double[] a,double[] b) {
 return new double[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};
}
private static double[] IvBox2609Add(double[] a,double[] b,double t) {
 return new double[]{a[0]+t*b[0],a[1]+t*b[1],a[2]+t*b[2]};
}
private static double IvBox2609Dot(double[] a,double[] b) {
 return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
}
private static double[] IvBox2609Cross(double[] a,double[] b) {
 return new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
}
private static double IvBox2609Norm(double[] a) {return Math.Sqrt(IvBox2609Dot(a,a));}
private static double[] IvBox2609Unit(double[] a) {
 double n=IvBox2609Norm(a);
 IvBox2609Need(!Double.IsNaN(n)&&!Double.IsInfinity(n)&&n>1e-15,"Invalid normal/derivative");
 return new double[]{a[0]/n,a[1]/n,a[2]/n};
}
private static void IvBox2609Finite(double[] p) {
 foreach(double v in p)IvBox2609Need(!Double.IsNaN(v)&&!Double.IsInfinity(v)&&Math.Abs(v)<=1e6,"Coordinate range unsupported");
}
private static int IvBox2609Corner(System.Collections.Generic.List<double[]> vertices,double[] p) {
 IvBox2609Finite(p);int found=-1;
 for(int i=0;i<vertices.Count;i++) {
  double d=IvBox2609Norm(IvBox2609Sub(p,vertices[i]));
  if(d<=IvBox2609Tol){IvBox2609Need(found<0,"Ambiguous corner clustering");found=i;}
  else IvBox2609Need(d>100*IvBox2609Tol,"Near coincident distinct corners unsupported");
 }
 if(found<0){found=vertices.Count;vertices.Add(p);}
 return found;
}
private static string IvBox2609Hash(string p) {
 using(var f=new System.IO.FileStream(p,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.Read))
 using(var h=System.Security.Cryptography.SHA256.Create())
 return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant();
}
private static string[] IvBox2609Inventory(NXOpen.Part p) {
 var uf=NXOpen.UF.UFSession.GetUFSession();
 var seen=new System.Collections.Generic.HashSet<NXOpen.Tag>();
 var rows=new System.Collections.Generic.List<string>();NXOpen.Tag t=NXOpen.Tag.Null;
 while((t=uf.Obj.CycleAll(p.Tag,t))!=NXOpen.Tag.Null) {
  IvBox2609Need(seen.Add(t)&&seen.Count<=100000,"Invalid/excessive object cycle");
  int type,sub;uf.Obj.AskTypeAndSubtype(t,out type,out sub);
  rows.Add(t.ToString()+":"+type+":"+sub);
 }
 rows.Sort(StringComparer.Ordinal);return rows.ToArray();
}
private static void IvBox2609Guard(NXOpen.Part p) {
 IvBox2609Need(p!=null&&!String.IsNullOrEmpty(p.FullPath),"Saved part required");
 string full=System.IO.Path.GetFullPath(p.FullPath);
 string root=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\independent_volume_20261109";
 IvBox2609Need(String.Equals(System.IO.Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)&&
 System.IO.Path.GetFileNameWithoutExtension(full).StartsWith("phase4_iv_",StringComparison.Ordinal)&&
 String.Equals(System.IO.Path.GetExtension(full),".prt",StringComparison.OrdinalIgnoreCase),"Dedicated box path required");
 IvBox2609Need(System.IO.File.Exists(full)&&p.IsFullyLoaded&&p.PartUnits==NXOpen.BasePart.Units.Millimeters,"Saved fully loaded MM part required");
 for(var d=new System.IO.DirectoryInfo(System.IO.Path.GetDirectoryName(full));d!=null;d=d.Parent)
  IvBox2609Need((d.Attributes&System.IO.FileAttributes.ReparsePoint)==0,"Reparse directory rejected");
 IvBox2609Need((System.IO.File.GetAttributes(full)&System.IO.FileAttributes.ReparsePoint)==0,"Reparse file rejected");
 var s=NXOpen.Session.GetSession();int n=0;
 foreach(NXOpen.BasePart q in s.Parts){n++;IvBox2609Need(q.Tag==p.Tag,"Other loaded part rejected");}
 IvBox2609Need(n==1&&s.Parts.Work==p&&s.Parts.Display==p,"Sole work/display part required");
 IvBox2609Need(p.ComponentAssembly.RootComponent==null,"Assembly rejected");
}
private static string IvBox2609Read(string[] args) {
 NXOpen.Part p=_part;var uf=NXOpen.UF.UFSession.GetUFSession();
 string beforeHash=null,afterHash=null,error=null,auditError=null;
 string[] beforeObjects=null,afterObjects=null;int? beforeModified=null,afterModified=null;
 object boundary=null;bool unchanged=false;
 var faceEvidence=new System.Collections.Generic.List<object>();
 var edgeEvidence=new System.Collections.Generic.List<object>();
 var featureEvidence=new System.Collections.Generic.List<object>();
 try {
  IvBox2609Need(args.Length==1,"nx_independent_box_boundary accepts no arguments");
  IvBox2609Guard(p);
  beforeHash=IvBox2609Hash(p.FullPath);
  beforeModified=Convert.ToInt32(uf.Part.IsModified(p.Tag));
  beforeObjects=IvBox2609Inventory(p);
  IvBox2609Need(beforeModified==0,"Unsaved modifications rejected");
  var bodies=new System.Collections.Generic.List<NXOpen.Body>();
  foreach(NXOpen.Body b in p.Bodies)bodies.Add(b);
  IvBox2609Need(bodies.Count==1&&bodies[0].IsSolidBody,"Sole solid required");
  NXOpen.Body body=bodies[0];int faultsCount;int[] faults;NXOpen.Tag[] bad;
  uf.Modl.AskBodyConsistency(body.Tag,out faultsCount,out faults,out bad);
  IvBox2609Need(faultsCount==0,"Native body consistency failure");
  foreach(NXOpen.Features.Feature f in p.Features) {
   string[] errors=f.GetFeatureErrorMessages(),warnings=f.GetFeatureWarningMessages();
   featureEvidence.Add(new{tag=f.Tag.ToString(),type=f.FeatureType,errors=errors,warnings=warnings,suppressed=f.Suppressed});
   IvBox2609Need(!f.Suppressed&&errors.Length==0&&warnings.Length==0,"Feature health failure");
  }
  NXOpen.Tag[] failed;uf.Modl.AskFeatFailList(out failed);
  IvBox2609Need(failed.Length==0,"Native failed-feature list nonempty");
  NXOpen.Face[] faces=body.GetFaces();NXOpen.Edge[] edges=body.GetEdges();
  IvBox2609Need(faces.Length==6&&edges.Length==12,"Six faces/twelve edges required");
  var faceTags=new System.Collections.Generic.HashSet<string>();
  foreach(var f in faces)IvBox2609Need(faceTags.Add(f.Tag.ToString()),"Duplicate face");
  var records=new System.Collections.Generic.Dictionary<string,IvBox2609Edge>();
  var vertices=new System.Collections.Generic.List<double[]>();
  var pairKeys=new System.Collections.Generic.HashSet<string>();
  double minLength=Double.MaxValue,nativeTol;
  uf.Modl.AskDistanceTolerance(out nativeTol);
  IvBox2609Need(!Double.IsNaN(nativeTol)&&!Double.IsInfinity(nativeTol)&&nativeTol>0,"Unknown native tolerance");
  foreach(var e in edges) {
   IvBox2609Need(e.SolidEdgeType==NXOpen.Edge.EdgeType.Linear&&e.GetBody().Tag==body.Tag,"Nonlinear/foreign edge");
   NXOpen.Point3d a,b;e.GetVertices(out a,out b);
   var r=new IvBox2609Edge{edge=e,tag=e.Tag.ToString(),a=IvBox2609V(a),b=IvBox2609V(b)};
   r.i=IvBox2609Corner(vertices,r.a);r.j=IvBox2609Corner(vertices,r.b);
   IvBox2609Need(r.i!=r.j,"Degenerate edge");
   double len=IvBox2609Norm(IvBox2609Sub(r.b,r.a));minLength=Math.Min(minLength,len);
   IvBox2609Need(len>Math.Max(1e-3,100*nativeTol),"Edge too small relative to native containment tolerance");
   string pair=Math.Min(r.i,r.j)+":"+Math.Max(r.i,r.j);
   IvBox2609Need(pairKeys.Add(pair),"Duplicate geometric edge");
   NXOpen.Face[] ef=e.GetFaces();
   IvBox2609Need(ef.Length==2&&ef[0].Tag!=ef[1].Tag,"Edge must have exactly two distinct face incidences");
   r.faces=new string[]{ef[0].Tag.ToString(),ef[1].Tag.ToString()};
   foreach(string ft in r.faces)IvBox2609Need(faceTags.Contains(ft),"Foreign edge face");
   IvBox2609Need(!records.ContainsKey(r.tag),"Duplicate native edge");records.Add(r.tag,r);
   edgeEvidence.Add(new{edge_tag=r.tag,type=e.SolidEdgeType.ToString(),vertices=new double[][]{r.a,r.b},
    corner_indices=new int[]{r.i,r.j},face_tags=r.faces,length_mm=len});
  }
  IvBox2609Need(vertices.Count==8,"Eight corners required");
  int[] degree=new int[8];foreach(var r in records.Values){degree[r.i]++;degree[r.j]++;}
  foreach(int d in degree)IvBox2609Need(d==3,"Non-box corner valence");
  var edgeUses=new System.Collections.Generic.Dictionary<string,int>();
  var orientedUses=new System.Collections.Generic.Dictionary<string,int>();
  foreach(string et in records.Keys){edgeUses.Add(et,0);orientedUses.Add(et,0);}
  var patches=new System.Collections.Generic.List<object>();
  var senses=new System.Collections.Generic.Dictionary<string,int>();
  var normals=new System.Collections.Generic.List<double[]>();
  var center=new double[3];foreach(var v in vertices)center=IvBox2609Add(center,v,0.125);
  int centerStatus;uf.Modl.AskPointContainment(center,body.Tag,out centerStatus);
  IvBox2609Need(centerStatus==1,"Corner centroid not strictly inside solid");
  double offset=Math.Max(8*nativeTol,100*IvBox2609Tol);
  IvBox2609Need(offset<minLength/10,"Containment offsets too large");
  foreach(var f in faces) {
   string ft=f.Tag.ToString();NXOpen.Edge[] fe=f.GetEdges();
   IvBox2609Need(fe.Length==4,"Face must have exactly four boundary edges; holes unsupported");
   var local=new System.Collections.Generic.List<IvBox2609Edge>();
   var unique=new System.Collections.Generic.HashSet<string>();
   var adjacency=new System.Collections.Generic.Dictionary<int,System.Collections.Generic.List<int>>();
   foreach(var e in fe) {
    string et=e.Tag.ToString();
    IvBox2609Need(unique.Add(et)&&records.ContainsKey(et),"Repeated/foreign face edge");
    var r=records[et];IvBox2609Need(Array.IndexOf(r.faces,ft)>=0,"Nonreciprocal edge/face incidence");
    local.Add(r);edgeUses[et]++;
    foreach(int v in new int[]{r.i,r.j})if(!adjacency.ContainsKey(v))adjacency.Add(v,new System.Collections.Generic.List<int>());
    adjacency[r.i].Add(r.j);adjacency[r.j].Add(r.i);
   }
   IvBox2609Need(adjacency.Count==4,"Face not four-corner rectangle");
   int start=Int32.MaxValue;
   foreach(var kv in adjacency){IvBox2609Need(kv.Value.Count==2,"Face boundary not a simple cycle");start=Math.Min(start,kv.Key);}
   int[] loop=new int[4];loop[0]=start;loop[1]=Math.Min(adjacency[start][0],adjacency[start][1]);
   for(int k=2;k<4;k++){var ns=adjacency[loop[k-1]];loop[k]=ns[0]==loop[k-2]?ns[1]:ns[0];}
   IvBox2609Need(new System.Collections.Generic.HashSet<int>(loop).Count==4&&adjacency[loop[3]].Contains(start),"Disconnected/nonclosed face boundary");
   double[] origin=vertices[loop[0]],du=IvBox2609Sub(vertices[loop[1]],origin),dv=IvBox2609Sub(vertices[loop[3]],origin);
   double[] fourth=IvBox2609Add(IvBox2609Add(origin,du,1),dv,1);
   double lu=IvBox2609Norm(du),lv=IvBox2609Norm(dv);
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(fourth,vertices[loop[2]]))<=IvBox2609Tol,"Opposite rectangle edges mismatch");
   IvBox2609Need(Math.Abs(IvBox2609Dot(du,dv))<=IvBox2609Tol*Math.Min(lu,lv),"Nonorthogonal rectangle");
   int type,normDir;double radius,radData;double[] point=new double[3],dir=new double[3],box=new double[6];
   uf.Modl.AskFaceData(f.Tag,out type,point,dir,box,out radius,out radData,out normDir);
   IvBox2609Need(type==22&&(normDir==1||normDir==-1),"Only native bounded planes with known sense supported");
   double[] planeNormal=IvBox2609Unit(dir),patchNormal=IvBox2609Unit(IvBox2609Cross(du,dv));
   IvBox2609Need(Math.Abs(IvBox2609Dot(planeNormal,patchNormal))>1-1e-10,"Plane and rectangle normals disagree");
   foreach(int v in loop)IvBox2609Need(Math.Abs(IvBox2609Dot(IvBox2609Sub(vertices[v],point),planeNormal))<=IvBox2609Tol,"Corner off native plane");
   double[] mid=IvBox2609Add(IvBox2609Add(origin,du,.5),dv,.5);
   double[] uv=new double[2],projected=new double[3],evaluated=new double[3],u1=new double[3],v1=new double[3],
    u2=new double[3],v2=new double[3],normal=new double[3],radii=new double[2];
   uf.Modl.AskFaceParm2(f.Tag,mid,uv,projected);
   uf.Modl.AskFaceProps(f.Tag,uv,evaluated,u1,v1,u2,v2,normal,radii);
   IvBox2609Need(IvBox2609Norm(IvBox2609Sub(mid,projected))<=IvBox2609Tol&&
    IvBox2609Norm(IvBox2609Sub(mid,evaluated))<=IvBox2609Tol,"Native surface evaluation not at rectangle interior");
   normal=IvBox2609Unit(normal);
   double[] surfaceNormal=IvBox2609Unit(IvBox2609Cross(u1,v1));
   IvBox2609Need(normDir*IvBox2609Dot(surfaceNormal,normal)>1-1e-10&&
    Math.Abs(IvBox2609Dot(normal,patchNormal))>1-1e-10,"Native face sense/derivative/patch mismatch");
   int sense=IvBox2609Dot(patchNormal,normal)>0?1:-1;
   var containment=new System.Collections.Generic.List<object>();
   foreach(double u in new double[]{.25,.5,.75})foreach(double v in new double[]{.25,.5,.75}) {
    double[] x=IvBox2609Add(IvBox2609Add(origin,du,u),dv,v);int fc,bc;
    uf.Modl.AskPointContainment(x,f.Tag,out fc);uf.Modl.AskPointContainment(x,body.Tag,out bc);
    IvBox2609Need(fc==1&&bc==3,"Interior sample not in actual trimmed face/on solid boundary");
    foreach(double d in new double[]{offset/2,offset}) {
     double[] inside=IvBox2609Add(x,normal,-d),outside=IvBox2609Add(x,normal,d);int si,so;
     uf.Modl.AskPointContainment(inside,body.Tag,out si);uf.Modl.AskPointContainment(outside,body.Tag,out so);
     containment.Add(new{u=u,v=v,point=x,face_status=fc,body_status=bc,offset_mm=d,
      inward_point=inside,outward_point=outside,inward_status=si,outward_status=so});
     IvBox2609Need(si==1&&so==2,"Face normal not independently outward at both offset scales");
    }
   }
   // For this six-rectangle family, reject concave/folded/overlapping configurations.
   IvBox2609Need(IvBox2609Dot(IvBox2609Sub(center,mid),normal)<-offset,"Box centroid not behind face");
   foreach(var v in vertices)IvBox2609Need(IvBox2609Dot(IvBox2609Sub(v,mid),normal)<=IvBox2609Tol,"Nonconvex box boundary");
   normals.Add(normal);
   var orderedTags=new string[4];
   for(int k=0;k<4;k++) {
    int a=loop[k],b=loop[(k+1)%4];IvBox2609Edge found=null;
    foreach(var r in local)if((r.i==a&&r.j==b)||(r.i==b&&r.j==a)){IvBox2609Need(found==null,"Ambiguous loop edge");found=r;}
    IvBox2609Need(found!=null,"Missing loop edge");orderedTags[k]=found.tag;
    orientedUses[found.tag]+=sense*(found.i==a?1:-1);
   }
   string name="face_"+ft;senses.Add(name,sense);
   var domain=new{kind="full_rectangle",no_exclusions=true,single_cover=true,
    source="NX bounded plane + complete four-linear-edge simple rectangular boundary + native body consistency",
    coordinates="affine edge-derived u,v; not native surface UV"};
   patches.Add(new{name=name,origin=origin,du=du,dv=dv,sense=sense,bounds=new double[]{0,1,0,1},domain=domain});
   var orderedVertices=new double[][]{vertices[loop[0]],vertices[loop[1]],vertices[loop[2]],vertices[loop[3]]};
   faceEvidence.Add(new{face_tag=ft,face_type=type,native_norm_dir=normDir,native_plane_point=point,
    native_plane_direction=dir,native_uv=uv,native_evaluated_point=evaluated,native_du=u1,native_dv=v1,
    native_face_normal=normal,affine_patch_sense=sense,edge_tags=orderedTags,
    corner_indices=loop,vertices=orderedVertices,containment=containment,
    full_rectangle_verified=true,single_boundary_cycle=true});
  }
  foreach(string et in records.Keys)IvBox2609Need(edgeUses[et]==2&&orientedUses[et]==0,"Boundary not closed and coherently oriented");
  // Each rectangular-box normal must have exactly one opposite and four perpendicular peers.
  for(int i=0;i<6;i++){int opposite=0;
   for(int j=0;j<6;j++)if(i!=j){double d=IvBox2609Dot(normals[i],normals[j]);
    if(d<-1+1e-10)opposite++;else IvBox2609Need(Math.Abs(d)<1e-10,"Not an orthogonal box");}
   IvBox2609Need(opposite==1,"Missing/duplicate opposite box face");
  }
  boundary=new{schema_version=1,units="mm",body_tag=body.Tag.ToString(),patches=patches,
   topology=new{closed=true,oriented=true,complete=true,senses=senses,source="nx_independent_box_boundary:v1",
    actual_two_face_incidence_verified=true,oriented_edge_cancellation_verified=true,
    rectangular_domains_verified=true,corner_count=8,edge_count=12,face_count=6,euler_characteristic=2},
   corners=vertices,consistency_fault_count=faultsCount,failed_feature_count=failed.Length,
   center=center,center_containment=centerStatus,native_distance_tolerance_mm=nativeTol,
   geometric_comparison_tolerance_mm=IvBox2609Tol,volume_computed=false,native_volume_verified=false};
 } catch(Exception e) {error=e.Message;}
 // Audit also on rejected geometry. No undo/update/save: any change requires coordinator inspection.
 if(beforeHash!=null&&beforeObjects!=null)try {
  IvBox2609Guard(p);afterModified=Convert.ToInt32(uf.Part.IsModified(p.Tag));
  afterObjects=IvBox2609Inventory(p);afterHash=IvBox2609Hash(p.FullPath);
  unchanged=beforeHash==afterHash&&beforeModified==0&&afterModified==0&&
   String.Join("\n",beforeObjects)==String.Join("\n",afterObjects);
  if(!unchanged)auditError="Read-only invariant failed";
 }catch(Exception e){auditError=e.Message;}
 bool ok=error==null&&auditError==null&&unchanged&&boundary!=null;
 return Json(new{ok=ok,command="nx_independent_box_boundary",read_only_query=true,error=error,audit_error=auditError,
  boundary=ok?boundary:null,face_evidence=faceEvidence,edge_evidence=edgeEvidence,features=featureEvidence,
  audit=new{sha256_before=beforeHash,sha256_after=afterHash,modified_before=beforeModified,modified_after=afterModified,
   objects_before=beforeObjects,objects_after=afterObjects,unchanged=unchanged},
  safe_pause_required=auditError!=null,retry_automatically=false,
  volume_computed=false,native_volume_verified=false,
  limitations="Only well-conditioned orthogonal six-planar-rectangle solids; tolerance-based geometric checks, not exact arithmetic or integration error bounds"});
}
