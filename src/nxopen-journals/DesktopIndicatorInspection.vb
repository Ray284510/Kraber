Option Strict On
Option Infer On
Imports System
Imports System.IO
Imports System.Globalization
Imports NXOpen
Imports NXOpen.UF
Imports NXOpen.Assemblies
Imports System.Collections.Generic
Module R7Service
 Private r, m As String
 Private u As UFSession
 Private writer As StreamWriter
 Sub AssertThat(b As Boolean,msg As String)
  If Not b Then Throw New InvalidOperationException(msg)
 End Sub
 Sub Log(k As String,v As Object)
  writer.WriteLine(k & "=" & Convert.ToString(v,CultureInfo.InvariantCulture)):writer.Flush()
 End Sub
 Sub Run()
  Dim s=Session.GetSession()
  u=UFSession.GetUFSession()
  Dim p=s.Parts.Work
  AssertThat(p IsNot Nothing AndAlso p.FullPath=Path.Combine(m,"00_Display_Stand.prt") AndAlso s.Parts.Display IsNot Nothing AndAlso s.Parts.Display.Tag=p.Tag,"target changed")
  For Each q As BasePart In s.Parts
   AssertThat(Path.GetDirectoryName(q.FullPath)=m AndAlso Not u.Part.IsModified(q.Tag),"unrelated or modified item")
  Next
  Dim cs=p.ComponentAssembly.RootComponent.GetChildren()
  AssertThat(cs.Length=3,"component set changed")
  For Each c As Component In cs
   Dim org As Point3d, rot As Matrix3x3
   c.GetPosition(org,rot)
   AssertThat(Math.Abs(rot.Xx-1)+Math.Abs(rot.Yy-1)+Math.Abs(rot.Zz-1)<0.000001,"axes changed")
   Log("origin." & c.Name,String.Join(",",New Double(){org.X,org.Y,org.Z}))
   Dim child=DirectCast(c.Prototype,Part), bb=child.Bodies.ToArray()
   AssertThat(bb.Length=1,"body count")
   For Each f As Face In bb(0).GetFaces()
    Dim typ,n As Integer,pt(2),dir(2),bounds(5),rad,rd As Double
    u.Modl.AskFaceData(f.Tag,typ,pt,dir,bounds,rad,rd,n)
    Log("face." & c.Name, f.SolidFaceType.ToString() & "|" & String.Join(",",pt) & "|" & String.Join(",",dir) & "|" & String.Join(",",bounds) & "|" & rad.ToString("R",CultureInfo.InvariantCulture))
   Next
  Next
  Dim wm=s.SetUndoMark(Session.MarkVisibility.Invisible,"R7 transient section view")
  Dim section=p.DynamicSections.CreateSectionBuilder(Nothing,p.ModelingViews.WorkView)
  Try
   section.PlaneX():section.SetOrigin(New Point3d(50,24,9)):section.ShowCap=True
   section.ShowSectionCurves(True)
   section.Commit()
  Finally
   section.Destroy()
  End Try
  Try
   u.Disp.RegenerateDisplay()
   u.Disp.CreateImage(Path.Combine(r,"section_X50.png"),UFDisp.ImageFormat.Png,UFDisp.BackgroundColor.Original)
  Finally
   s.UndoToMark(wm,Nothing)
   s.DeleteUndoMark(wm,Nothing)
   u.Disp.RegenerateDisplay()
  End Try
  For Each q As BasePart In s.Parts
   Log("exit.modified." & q.Leaf,u.Part.IsModified(q.Tag))
  Next
  Log("outcome","PASS")
 End Sub
 Public Sub Main()
  Dim root As String=Environment.GetEnvironmentVariable("NX_EXAMPLE_DIR")
  AssertThat(Not String.IsNullOrWhiteSpace(root) AndAlso Path.IsPathRooted(root) AndAlso Directory.Exists(root),"Set NX_EXAMPLE_DIR to an existing absolute directory")
  r=Path.Combine(root,"desk-indicator")
  m=Path.Combine(r,"parts")
  Using fs As New FileStream(Path.Combine(r,"service.log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)
   Using sw As New StreamWriter(fs)
    writer=sw
    Try
     Run()
    Catch exc As Exception
     Log("error",exc.ToString()):Log("outcome","STOPPED")
    End Try
   End Using
  End Using
 End Sub
End Module
