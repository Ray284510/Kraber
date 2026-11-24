// Single-use experimental native lifecycle trial; process and file guards precede the call.
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\ask2dtrim-20261110\ask2d-native-trial-v2.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_ask2dtrim_once(uint face,string path);
[System.Runtime.InteropServices.DllImport(@"@NX_LAB_ROOT@\logs\phase5\ask2dtrim-20261110\ask2d-native-sheet-trial.dll", CallingConvention=System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet=System.Runtime.InteropServices.CharSet.Ansi, ExactSpelling=true)]
private static extern int nx12_ask2d_sheet_once(uint face,string path);
private static bool IvAsk2610Attempted;
private static string IvAsk2610Trial(string[] a) {
 const string root=@"@NX_LAB_ROOT@\models\phase4_phase5_staged_20261108_212227\phase4_ask2dtrim_20261110";
 const string logs=@"@NX_LAB_ROOT@\logs\phase5\ask2dtrim-20261110";
 var mode=Environment.GetEnvironmentVariable("NX_ASK2D_TRIAL");
 IvBox2609Need(a.Length==1&&!IvAsk2610Attempted&&(mode=="planar"||mode=="sheet")&&a[0]=="nx_ask2d_"+mode+"_trial_once","Independent single-use trial process required");
 string fixture=Path.Combine(root,"phase4_ask2d_"+mode+(mode=="planar"?"_once_v2.prt":"_once_v1.prt"));string output=Path.Combine(logs,"ask2d-"+mode+(mode=="planar"?"-native-v2.log":"-native.log"));
 IvBox2609Need(!File.Exists(output)&&!File.Exists(output+".preflight.txt"),"Prior trial evidence exists; no replay");
 IvBox2609Need(_part!=null&&String.Equals(_part.FullPath,fixture,StringComparison.OrdinalIgnoreCase)&&_part.IsFullyLoaded&&_part.PartUnits==BasePart.Units.Millimeters,"Exact saved trial copy required");
 for(DirectoryInfo d=new DirectoryInfo(root);d!=null;d=d.Parent)IvBox2609Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Reparse directory");
 IvBox2609Need((File.GetAttributes(fixture)&FileAttributes.ReparsePoint)==0,"Reparse file");
 var s=Session.GetSession();int count=0;foreach(BasePart p in s.Parts){count++;IvBox2609Need(p.Tag==_part.Tag,"Other part loaded");}
 IvBox2609Need(count==1&&s.Parts.Work==_part&&s.Parts.Display==_part,"Sole work/display part required");
 var uf=NXOpen.UF.UFSession.GetUFSession();int modified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));string hash=IvBox2609Hash(fixture);
 string expected=mode=="planar"?"2a7669bf7c12f8bf70196ea23e1956cdc885f331a0b87f6639f244704a852a94":"12d26f73c58eb28dd664ac74b8566ac11ec611a3515313704fdfc7f6b522e748";
 IvBox2609Need(modified==0&&hash==expected,"Saved fixture provenance mismatch");
 var body=_part.Bodies.ToArray().Single();Face face;
 if(mode=="planar") {
  IvBox2609Need(body.IsSolidBody,"Control must be solid");
  var caps=body.GetFaces().Select(IvAn2609Data).Where(f=>f.type==22&&f.f.GetEdges().Length==5&&Math.Abs(f.dir[2])>0.999999).OrderByDescending(f=>f.point[2]).ToArray();
  IvBox2609Need(caps.Length==2&&caps[0].point[2]-caps[1].point[2]>1,"Two distinct holed caps required");face=caps[0].f;
  IvBox2609Need(face.GetEdges().Count(e=>e.SolidEdgeType.ToString()=="Circular")==1&&face.GetEdges().Count(e=>e.SolidEdgeType.ToString()=="Linear")==4,"Known cap border mismatch");
 } else {
  IvBox2609Need(!body.IsSolidBody,"Sheet required");face=body.GetFaces().Single();IvBox2609Need(face.SolidFaceType.ToString()=="Parametric"&&face.GetEdges().Length==2,"Expected parametric sheet");
 }
 var before=IvBox2609Inventory(_part);int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
 using(var f=new FileStream(output+".preflight.txt",FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var w=new StreamWriter(f)) {w.WriteLine("pid="+pid+" nx=12.0.2.9 part="+fixture+" face="+face.Tag+" tolerance_mm=0.001 sha256="+hash+" objects="+before.Length+" modified="+modified);w.Flush();f.Flush(true);}
 IvAsk2610Attempted=true;uint tag=Convert.ToUInt32(face.Tag);int rc=mode=="planar"?nx12_ask2dtrim_once(tag,output):nx12_ask2d_sheet_once(tag,output);
 if(rc!=0)return Json(new{ok=false,rc=rc,pid=pid,mode=mode,face=face.Tag.ToString(),native_log=output,quarantine=true,post_nx_queries_permitted=false});
 var after=IvBox2609Inventory(_part);int afterModified=Convert.ToInt32(uf.Part.IsModified(_part.Tag));string afterHash=IvBox2609Hash(fixture);bool clean=hash==afterHash&&before.SequenceEqual(after);
 return Json(new{ok=clean,rc=rc,pid=pid,mode=mode,face=face.Tag.ToString(),native_type=face.SolidFaceType.ToString(),object_count_before=before.Length,object_count_after=after.Length,modified_before=modified,modified_after=afterModified,sha256_before=hash,sha256_after=afterHash,inventory_unchanged=clean,native_log=output,save_permitted=false});
}
