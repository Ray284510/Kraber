// An isolated, single-use NX 12 native topology acceptance probe.
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\nurbs-reader-safety-20261110\nurbs-native-brep-probe.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_brep_topology_once(uint face,string path);
private static bool IvBrep2610Attempted;
private static string IvBrep2610Trial(string[] a) {
 const string fixture=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\phase4_nurbs_native_safety_20261110\phase4_nurbs_brep_isolated_v1.prt";
 const string output=@"@NX_LAB_ROOT@\logs\phase5\nurbs-reader-safety-20261110\native-brep-runtime0.log";
 IvBox2609Need(a.Length==1&&!IvBrep2610Attempted&&!File.Exists(output),"One native test only");
 IvBox2609Need(Environment.GetEnvironmentVariable("NX_NURBS_BREP_TRIAL")=="single-sheet-20261110","Independent trial process required");
 IvBox2609Need(_part!=null&&String.Equals(_part.FullPath,fixture,StringComparison.OrdinalIgnoreCase)&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters,"Exact saved sheet copy required");
 for(DirectoryInfo d=new DirectoryInfo(Path.GetDirectoryName(fixture));d!=null;d=d.Parent)IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse path rejected");
 IvBox2609Need((File.GetAttributes(fixture)&FileAttributes.ReparsePoint)==0,"Reparse file rejected");
 var s=Session.GetSession();int parts=0;foreach(BasePart p in s.Parts){parts++;IvBox2609Need(p.Tag==_part.Tag,"Other part loaded");}
 IvBox2609Need(parts==1&&s.Parts.Work==_part&&s.Parts.Display==_part,"Sole work/display part required");
 var uf=NXOpen.UF.UFSession.GetUFSession();int before=Convert.ToInt32(uf.Part.IsModified(_part.Tag));string hash=IvBox2609Hash(fixture);
 IvBox2609Need(before==0&&hash=="12d26f73c58eb28dd664ac74b8566ac11ec611a3515313704fdfc7f6b522e748","Verified unmodified copy required");
 var body=_part.Bodies.ToArray().Single();IvBox2609Need(!body.IsSolidBody,"Sheet required");var face=body.GetFaces().Single();
 IvBox2609Need(face.SolidFaceType.ToString()=="Parametric"&&face.GetEdges().Length==2,"Expected parametric sheet topology");
 var inventory=IvBox2609Inventory(_part);int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 File.WriteAllText(output+".preflight.txt","pid="+pid+" nx=12.0.2.9 face="+face.Tag+" hash="+hash+" objects="+inventory.Length+" modified="+before+"\n");
 IvBrep2610Attempted=true;int rc=nx12_brep_topology_once(Convert.ToUInt32(face.Tag),output);
 var after=IvBox2609Inventory(_part);string afterHash=IvBox2609Hash(fixture);int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));
 bool unchanged=hash==afterHash&&modified==0&&inventory.SequenceEqual(after);
 return Json(new{ok=rc==0&&unchanged,rc=rc,pid=pid,face=face.Tag.ToString(),native_type=face.SolidFaceType.ToString(),native_edges=face.GetEdges().Select(e=>e.Tag.ToString()).ToArray(),object_count_before=inventory.Length,object_count_after=after.Length,modified_before=before,modified_after=modified,sha256_before=hash,sha256_after=afterHash,unchanged=unchanged,native_log=output,geometry_extraction=false});
}
