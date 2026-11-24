Option Strict On
Option Infer On
Imports System
Imports System.IO
Imports System.Globalization
Imports System.Collections.Generic
Imports System.Linq
Imports NXOpen
Imports NXOpen.UF
Imports NXOpen.Features
Imports NXOpen.Assemblies
Imports NXOpen.Positioning
Module StandR7
 Private runDir, modelDir As String
 Private session As Session
 Private uf As UFSession
 Private sw As StreamWriter
 Private stage As String="entry"
 Private currentTop As Part
 Private baseComp, coverComp, slideComp As Component
 Private known As New List(Of ComponentConstraint)
 Private xpos As ComponentConstraint
 Sub Note(k As String, v As Object)
  sw.WriteLine(k & "=" & Convert.ToString(v,CultureInfo.InvariantCulture))
  sw.Flush()
 End Sub
 Sub Check(ok As Boolean, msg As String)
  If Not ok Then Throw New InvalidOperationException(msg)
 End Sub
 Function Own(p As BasePart) As Boolean
  Return p IsNot Nothing AndAlso String.Equals(Path.GetDirectoryName(p.FullPath),modelDir,StringComparison.OrdinalIgnoreCase)
 End Function
 Sub OnlyOwn()
  For Each obj As BasePart In session.Parts
   Check(Own(obj),"Non-project part loaded. Stop")
  Next
 End Sub
 Function NewP(n As String) As Part
  OnlyOwn()
  Dim f As String=Path.Combine(modelDir,n & ".prt")
  Check(Not File.Exists(f),"PRT collision")
  Dim p As Part=session.Parts.NewDisplay(f,Part.Units.Millimeters)
  Check(p.Tag=session.Parts.Work.Tag AndAlso p.Tag=session.Parts.Display.Tag AndAlso Own(p),"New part role mismatch")
  Return p
 End Function
 Sub Save(p As Part)
  OnlyOwn()
  Check(Own(p),"Save outside project")
  Using st As PartSaveStatus=p.Save(BasePart.SaveComponents.[False],BasePart.CloseAfterSave.[False])
   Check(st.NumberUnsavedParts=0 AndAlso st.NumberUnsavedObjects=0,"Unsaved items")
  End Using
  Check(File.Exists(p.FullPath) AndAlso Not uf.Part.IsModified(p.Tag),"Save not confirmed")
  Note("saved",p.FullPath)
 End Sub
 Function Pt(x As Double,y As Double,z As Double) As Point3d
  Return New Point3d(x,y,z)
 End Function
 Function Iden() As Matrix3x3
  Dim m As New Matrix3x3()
  m.Xx=1:m.Yy=1:m.Zz=1
  Return m
 End Function
 Function Body1(p As Part) As Body
  Dim xs=p.Bodies.ToArray()
  Check(xs.Length=1 AndAlso xs(0).IsSolidBody,"Need one solid: " & p.Leaf)
  Return xs(0)
 End Function
 Sub Param(p As Part,n As String, v As String)
  p.Expressions.CreateWithUnits(n & "=" & v,p.UnitCollection.FindObject("MilliMeter"))
 End Sub
 Sub UpdateModel()
  Dim mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"R7 update")
  Check(session.UpdateManager.DoUpdate(mark)=0,"NX update failed")
 End Sub
 Sub Outline(p As Part, height As String)
  Dim zero=Pt(0,0,0), up=New Vector3d(0,0,1)
  Dim b=p.Sketches.CreateSketchInPlaceBuilder2(Nothing)
  Dim sk As Sketch
  Try
   b.PlaneReference=p.Planes.CreateFixedTypePlane(zero,Iden(),SmartObject.UpdateOption.WithinModeling)
   b.AxisReference=p.Directions.CreateDirection(zero,New Vector3d(1,0,0),SmartObject.UpdateOption.WithinModeling)
   b.SketchOrigin=p.Points.CreatePoint(zero)
   b.PlaneOption=Sketch.PlaneOption.ExistingPlane
   b.OriginOption=OriginMethod.SpecifyPoint
   sk=DirectCast(b.Commit(),Sketch)
  Finally
   b.Destroy()
  End Try
  sk.SetName("STAND_PERIMETER_XY")
  sk.Activate(Sketch.ViewReorient.[False])
  Dim xy As Double(,)={{4,0},{96,0},{100,4},{100,44},{96,48},{4,48},{0,44},{0,4}}
  For i As Integer=0 To 7
   Dim j As Integer=(i+1) Mod 8
   Dim ln=p.Curves.CreateLine(Pt(xy(i,0),xy(i,1),0),Pt(xy(j,0),xy(j,1),0))
   sk.AddGeometry(ln,Sketch.InferConstraintsOption.InferNoConstraints)
   Dim g As New Sketch.ConstraintGeometry()
   g.Geometry=ln:g.PointType=Sketch.ConstraintPointType.None
   sk.CreateFixedConstraint(g)
  Next
  sk.Deactivate(Sketch.ViewReorient.[False],Sketch.UpdateLevel.Model)
  Dim sect=p.Sections.CreateSection()
  Dim rule=p.ScRuleFactory.CreateRuleCurveFeature(New Feature(){sk.Feature})
  sect.AddToSection(New SelectionIntentRule(){rule},Nothing,Nothing,Nothing,zero,Section.Mode.Create,False)
  Dim ex=p.Features.CreateExtrudeBuilder(Nothing)
  Try
   ex.Section=sect
   ex.Direction=p.Directions.CreateDirection(zero,up,SmartObject.UpdateOption.WithinModeling)
   ex.Limits.StartExtend.Value.RightHandSide="0"
   ex.Limits.EndExtend.Value.RightHandSide=height
   Dim f=ex.CommitFeature():f.SetName("EXTRUDED_DISPLAY_OUTLINE")
  Finally
   ex.Destroy()
  End Try
  For Each ent As NXObject In sk.GetAllGeometry()
   DirectCast(ent,DisplayableObject).Blank()
  Next
 End Sub
 Sub Box(p As Part,n As String,org As Point3d, l As String,w As String,h As String, op As Feature.BooleanType)
  Dim b=p.Features.CreateBlockFeatureBuilder(Nothing)
  Try
   b.Type=BlockFeatureBuilder.Types.OriginAndEdgeLengths
   Dim target As Body=Nothing
   If op<>Feature.BooleanType.Create Then target=Body1(p)
   b.SetBooleanOperationAndTarget(op,target)
   b.SetOriginAndLengths(org,l,w,h)
   Dim f=b.CommitFeature():f.SetName(n)
  Finally
   b.Destroy()
  End Try
 End Sub
 Sub Hole(p As Part,x As Double,y As Double,label As String)
  Dim t As Tag
  uf.Modl.CreateCyl1(FeatureSigns.Negative,New Double(){x,y,-1},"20","fastener_clearance",New Double(){0,0,1},t)
  DirectCast(NXOpen.Utilities.NXObjectManager.Get(t),Feature).SetName(label)
 End Sub
 Function Bounds(b As Body) As Double()
  Dim mn(2),ds(2),dirs(2,2) As Double
  uf.Modl.AskBoundingBoxExact(b.Tag,Tag.Null,mn,dirs,ds)
  Return ds
 End Function
 Function Numbers(a As Double()) As String
  Return String.Join(",",Array.ConvertAll(a,Function(x) x.ToString("R",CultureInfo.InvariantCulture)))
 End Function
 Sub Inspect(p As Part,prefix As String)
  Dim b=Body1(p),fault As Integer, faults() As Integer=Nothing, tags() As Tag=Nothing
  uf.Modl.AskBodyConsistency(b.Tag,fault,faults,tags)
  Dim names As New List(Of String)
  For Each f As Feature In p.Features
   Dim errors=f.GetFeatureErrorMessages()
   Check(errors Is Nothing OrElse errors.Length=0,"Feature error " & f.Name)
   names.Add(f.GetFeatureName() & ":" & f.Name)
  Next
  Note(prefix & ".bbox_mm",Numbers(Bounds(b))):Note(prefix & ".features",String.Join("|",names))
  Note(prefix & ".body_faults",fault)
  Check(fault=0 AndAlso names.Count>2,"Bad native solid")
 End Sub
 Sub FinishPart(p As Part,col As Integer)
  Dim b=Body1(p):b.Color=col:b.RedisplayObject()
  p.ModelingViews.WorkView.Orient(View.Canned.Isometric,View.ScaleAdjustment.Fit)
  Inspect(p,p.Leaf):Save(p)
 End Sub
 Function BasePartBuild() As Part
  stage="build_base":Dim p=NewP("01_Plinth")
  Param(p,"plinth_height","12"):Param(p,"fastener_clearance","3.4"):Param(p,"nut_flat_clearance","5.8")
  Outline(p,"plinth_height")
  Box(p,"GUIDE_TRAY_WITH_INTEGRAL_END_STOPS",Pt(12,13,6),"76","22","plinth_height",Feature.BooleanType.Subtract)
  Dim i As Integer=0
  For Each x As Double In New Double(){8,92}
   For Each y As Double In New Double(){6,42}
    i+=1:Hole(p,x,y,"THROUGH_FIXING_" & i)
    Box(p,"OPEN_NUT_ACCESS_" & i,Pt(x-2.9,If(y<24,-1,38.5),-1),"nut_flat_clearance","10.5","4.4",Feature.BooleanType.Subtract)
   Next
  Next
  FinishPart(p,176):Return p
 End Function
 Function SlideBuild() As Part
  stage="build_slide":Dim p=NewP("02_Indicator")
  Param(p,"shoe_width","21.2"):Param(p,"shoe_length","22"):Param(p,"shoe_height","5.5")
  Box(p,"INDICATOR_SHOE",Pt(0,0,0),"shoe_length","shoe_width","shoe_height",Feature.BooleanType.Create)
  Box(p,"RAISED_THUMB_TAB",Pt(6,5.6,5),"10","10","9.5",Feature.BooleanType.Unite)
  For i As Integer=0 To 2
   Box(p,"FINGER_GROOVE_" & (i+1),Pt(8+i*2.6,5,13.8),"0.9","12","1",Feature.BooleanType.Subtract)
  Next
  Dim ex=p.Expressions.FindObject("shoe_width")
  For Each v As String In New String(){"21.2","20.8","21.2"}
   If Math.Abs(ex.Value-Double.Parse(v,CultureInfo.InvariantCulture))>0.00001 Then
    p.Expressions.Edit(ex,v):UpdateModel()
   End If
   Dim wid=Bounds(Body1(p))(1)
   Note("edit.shoe_width.nominal_measured",v & "," & wid.ToString("R",CultureInfo.InvariantCulture))
   Check(Math.Abs(wid-ex.Value)<0.00001,"Parameter change not measured")
  Next
  FinishPart(p,36):Return p
 End Function
 Function CoverBuild() As Part
  stage="build_cover":Dim p=NewP("03_Bezel")
  Param(p,"bezel_thickness","3"):Param(p,"fastener_clearance","3.4")
  Outline(p,"bezel_thickness")
  Box(p,"TRAVEL_WINDOW",Pt(16,18,-1),"68","12","bezel_thickness+2",Feature.BooleanType.Subtract)
  Dim i As Integer=0
  For Each x As Double In New Double(){8,92}
   For Each y As Double In New Double(){6,42}
    i+=1:Hole(p,x,y,"THROUGH_FIXING_" & i)
   Next
  Next
  For i=0 To 4
   Box(p,"INDEX_MARK_" & (i+1),Pt(22.5+i*13.5,32,2.5),"1",If(i Mod 2=0,"5","3"),"1",Feature.BooleanType.Subtract)
  Next
  FinishPart(p,120):Return p
 End Function
 Function Add(p As Part,name As String,org As Point3d) As Component
  Dim ls As PartLoadStatus=Nothing
  Dim c=currentTop.ComponentAssembly.AddComponent(p.FullPath,"Entire Part",name,org,Iden(),-1,ls)
  Using ls
   Check(ls.NumberUnloadedParts=0,"Incomplete add")
  End Using
  Return c
 End Function
 Function FaceAt(c As Component,ax As Integer,plane As Double,at As Integer, hit As Double) As Face
  Dim p=DirectCast(c.Prototype,Part)
  Dim found As New List(Of Face)
  For Each face As Face In Body1(p).GetFaces()
   If face.SolidFaceType<>Face.FaceType.Planar Then Continue For
   Dim t As Integer,n As Integer,rad,rd As Double,pt1(2),d(2),bb(5) As Double
   uf.Modl.AskFaceData(face.Tag,t,pt1,d,bb,rad,rd,n)
   If Math.Abs(d(ax))>0.99999 AndAlso Math.Abs(pt1(ax)-plane)<0.00001 AndAlso bb(at)-0.00001<=hit AndAlso bb(at+3)+0.00001>=hit Then found.Add(face)
  Next
  Check(found.Count=1,"Datum face ambiguous/missing " & c.Name & " axis=" & ax & " plane=" & plane & " count=" & found.Count)
  Return DirectCast(c.FindOccurrence(found(0)),Face)
 End Function
 Function Con(n As String,typ As Constraint.Type, a As Component,ra As NXObject,b As Component,rb As NXObject,same As Boolean,dist As String) As ComponentConstraint
  stage="constraint_" & n
  Dim pos=currentTop.ComponentAssembly.Positioner
  pos.ClearNetwork()
  Dim net=DirectCast(pos.EstablishNetwork(),ComponentNetwork)
  net.MoveObjectsState=True:net.NetworkArrangementsMode=ComponentNetwork.ArrangementsMode.Existing
  pos.BeginAssemblyConstraints()
  Try
   Dim q=DirectCast(pos.CreateConstraint(True),ComponentConstraint)
   q.SetName(n):q.ConstraintType=typ
   If b IsNot Nothing Then q.ConstraintAlignment=If(same,Constraint.Alignment.CoAlign,Constraint.Alignment.ContraAlign)
   q.CreateConstraintReference(a,ra,False,False)
   If b IsNot Nothing Then q.CreateConstraintReference(b,rb,False,False)
   If dist<>"" Then q.Expression.RightHandSide=dist
   net.AddConstraint(q):net.LoadReferencedGeometry():net.Solve():net.ApplyToModel()
   Note("constraint." & n,q.GetConstraintStatus().ToString())
   Check(q.GetConstraintStatus().ToString()="Solved","Unsolved " & n)
   known.Add(q):Return q
  Finally
   pos.ClearNetwork():pos.EndAssemblyConstraints()
  End Try
 End Function
 Sub Solve()
  Dim pos=currentTop.ComponentAssembly.Positioner:pos.ClearNetwork()
  Try
   Dim net=DirectCast(pos.EstablishNetwork(),ComponentNetwork)
   net.MoveObjectsState=True:net.NetworkArrangementsMode=ComponentNetwork.ArrangementsMode.Existing
   For Each q In known
    If Not q.Suppressed Then net.AddConstraint(q)
   Next
   net.LoadReferencedGeometry():net.Solve():net.ApplyToModel()
  Finally
   pos.ClearNetwork()
  End Try
 End Sub
 Sub Dof(c As Component,label As String)
  Dim d=c.GetDegreesOfFreedom()
  Note("dof." & label,d.Result.ToString() & ",T=" & d.NumTranslational & ",R=" & d.NumRotational & ",instantT=" & d.NumInstantaneousTranslational & ",instantR=" & d.NumInstantaneousRotational)
 End Sub
 Sub Screen(label As String)
  Try
   currentTop.ModelingViews.WorkView.Orient(View.Canned.Isometric,View.ScaleAdjustment.Fit)
   uf.Disp.RegenerateDisplay()
   uf.Disp.CreateImage(Path.Combine(runDir,label & ".png"),UFDisp.ImageFormat.Png,UFDisp.BackgroundColor.Original)
  Catch e As Exception
   Note("capture." & label,e.Message)
  End Try
 End Sub
 Function Inter(a As Component,b As Component) As Integer
  Dim at=DirectCast(a.FindOccurrence(Body1(DirectCast(a.Prototype,Part))),Body)
  Dim bt=DirectCast(b.FindOccurrence(Body1(DirectCast(b.Prototype,Part))),Body)
  Dim ans(0) As Integer
  uf.Modl.CheckInterference(at.Tag,1,New Tag(){bt.Tag},ans)
  Return ans(0)
 End Function
 Sub Position(x As Double,label As String)
  stage="measure_" & label:OnlyOwn()
  Dim org As Point3d,rot As Matrix3x3
  slideComp.GetPosition(org,rot)
  Note("position." & label,Numbers(New Double(){org.X,org.Y,org.Z}))
  Check(Math.Abs(org.X-x)<0.0001 AndAlso Math.Abs(org.Y-13.4)<0.0001 AndAlso Math.Abs(org.Z-6)<0.0001,"Unexpected slider position")
  Dim a=Inter(baseComp,coverComp), b=Inter(baseComp,slideComp), c=Inter(coverComp,slideComp)
  Note("interference." & label,"base_cover=" & a & ",base_slide=" & b & ",cover_slide=" & c)
  Check(a=3 AndAlso b=3 AndAlso c=2,"Unexpected interface classification")
 End Sub
 Sub MoveTo(x As Double)
  xpos.Expression.RightHandSide=x.ToString("R",CultureInfo.InvariantCulture):Solve()
 End Sub
 Sub Assemble(bp As Part,sp As Part,cp As Part)
  stage="assemble":currentTop=NewP("00_Display_Stand")
  baseComp=Add(bp,"PLINTH",Pt(0,0,0))
  slideComp=Add(sp,"INDICATOR",Pt(39,13.4,6))
  coverComp=Add(cp,"REMOVABLE_BEZEL",Pt(0,0,12))
  Save(currentTop)
  Con("BASE_DATUM",Constraint.Type.Fix,baseComp,baseComp,Nothing,Nothing,False,"")
  Con("BEZEL_SEATING",Constraint.Type.Touch,coverComp,FaceAt(coverComp,2,0,0,50),baseComp,FaceAt(baseComp,2,12,0,50),False,"")
  Con("BEZEL_X_DATUM",Constraint.Type.Touch,coverComp,FaceAt(coverComp,0,0,1,24),baseComp,FaceAt(baseComp,0,0,1,24),True,"")
  Con("BEZEL_Y_DATUM",Constraint.Type.Touch,coverComp,FaceAt(coverComp,1,0,0,50),baseComp,FaceAt(baseComp,1,0,0,50),True,"")
  Con("SHOE_ON_FLOOR",Constraint.Type.Touch,slideComp,FaceAt(slideComp,2,0,0,11),baseComp,FaceAt(baseComp,2,6,0,50),False,"")
  Con("SHOE_SIDE_GUIDE",Constraint.Type.Distance,slideComp,FaceAt(slideComp,1,0,0,11),baseComp,FaceAt(baseComp,1,0,0,50),True,"13.4")
  Dof(coverComp,"cover"):Dof(slideComp,"free_slide")
  Save(currentTop)
  xpos=Con("INDICATOR_POSITION_X",Constraint.Type.Distance,slideComp,FaceAt(slideComp,0,0,1,10),baseComp,FaceAt(baseComp,0,0,1,24),True,"39")
  Dof(slideComp,"positioned")
  Position(39,"middle"):Screen("middle")
  MoveTo(12):Position(12,"start"):Screen("start")
  MoveTo(66):Position(66,"end"):Screen("end")
  MoveTo(39):Position(39,"restored_middle")
 End Sub
 Sub Service(bp As Part,cp As Part)
  stage="service_checks"
  For Each x As Double In New Double(){8,92}
   For Each y As Double In New Double(){6,42}
    For Each elem As Part In New Part(){bp,cp}
     Dim topZ As Double=If(elem.Tag=bp.Tag,12,3)
     For Each z As Double In New Double(){0.2,topZ/2,topZ-0.2}
      For i As Integer=0 To 7
       Dim angle=i*Math.PI/4,answer As Integer
       uf.Modl.AskPointContainment(New Double(){x+1.48*Math.Cos(angle),y+1.48*Math.Sin(angle),z},Body1(elem).Tag,answer)
       Check(answer=2,"M3 shank clearance blocked")
      Next
     Next
    Next
    For Each dx As Double In New Double(){-2.75,0,2.75}
     For Each dy As Double In New Double(){-1.55,1.55}
      Dim answer As Integer
      uf.Modl.AskPointContainment(New Double(){x+dx,y+dy,2.2},Body1(bp).Tag,answer)
      Check(answer=2,"Nut seat blocked")
     Next
    Next
   Next
  Next
  Note("service.shank","4 stations x 2 parts x 3 heights x 8 radial samples free")
  Note("service.nut","4 open underside seats x 6 witness points free")
 End Sub
 Sub Reopen()
  stage="save_close_reopen":OnlyOwn():Save(currentTop)
  Dim pathTop=currentTop.FullPath
  For Each p As BasePart In session.Parts
   Check(Not uf.Part.IsModified(p.Tag),"Refusing close with dirty part: " & p.FullPath)
  Next
  currentTop.Close(BasePart.CloseWholeTree.[True],BasePart.CloseModified.CloseModified,Nothing)
  For Each p As BasePart In session.Parts
   Check(Own(p) AndAlso Not uf.Part.IsModified(p.Tag),"Unsafe remaining part")
  Next
  Dim leftover=session.Parts.ToArray()
  For Each p As BasePart In leftover
   p.Close(BasePart.CloseWholeTree.[False],BasePart.CloseModified.CloseModified,Nothing)
  Next
  Check(session.Parts.ToArray().Length=0,"Could not fully unload")
  Note("closed","all four project parts unloaded")
  Dim ls As PartLoadStatus=Nothing
  currentTop=session.Parts.OpenDisplay(pathTop,ls)
  Using ls
   Check(ls.NumberUnloadedParts=0,"Missing dependency after reopen")
  End Using
  OnlyOwn()
  Dim cs=currentTop.ComponentAssembly.RootComponent.GetChildren()
  Check(cs.Length=3,"Missing components")
  Dim uniq As New Dictionary(Of Tag,String)
  For Each comp As Component In cs
   Dim p=DirectCast(comp.Prototype,Part)
   Check(Own(p),"External prototype")
   If Not p.IsFullyLoaded Then
    Using st=p.LoadFully()
     Check(st.NumberUnloadedParts=0,"Incomplete child")
    End Using
   End If
   Note("reopen.component",comp.Name & " | " & p.FullPath)
   Inspect(p,"reopen." & comp.Name)
   Select Case comp.Name
    Case "INDICATOR"
     slideComp=comp
     Check(Math.Abs(p.Expressions.FindObject("shoe_width").Value-21.2)<0.00001,"Restored expression mismatch")
    Case "PLINTH"
     baseComp=comp
    Case "REMOVABLE_BEZEL"
     coverComp=comp
   End Select
  Next
  For Each comp In cs
   For Each q In comp.GetConstraints()
    If Not uniq.ContainsKey(q.Tag) Then
     uniq.Add(q.Tag,q.Name)
     Note("reopen.constraint",q.Name & " | " & q.GetConstraintStatus().ToString())
     Check(q.GetConstraintStatus().ToString()="Solved","Reopen constraint unsolved")
    End If
   Next
  Next
  Check(uniq.Count=7,"Unexpected constraint count")
  Position(39,"reopened"):Screen("reopened")
 End Sub
 Public Sub Main()
  Dim root As String=Environment.GetEnvironmentVariable("NX_EXAMPLE_DIR")
  Check(Not String.IsNullOrWhiteSpace(root) AndAlso Path.IsPathRooted(root) AndAlso Directory.Exists(root),"Set NX_EXAMPLE_DIR to an existing absolute directory")
  runDir=Path.Combine(root,"desk-indicator")
  Check(Not Directory.Exists(runDir),"Use a fresh example directory")
  modelDir=Path.Combine(runDir,"parts")
  Directory.CreateDirectory(modelDir)
  Using file As New FileStream(Path.Combine(runDir,"native.log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)
   Using log As New StreamWriter(file)
    sw=log
    Try
     Note("start",DateTimeOffset.Now.ToString("O"))
     session=Session.GetSession():uf=UFSession.GetUFSession()
     Check(session.Parts.ToArray().Length=0 AndAlso session.Parts.Work Is Nothing AndAlso session.Parts.Display Is Nothing,"Not an empty NX session")
     Note("initial","work=null, display=null, loaded=0")
     Dim bp=BasePartBuild(),sp=SlideBuild(),cp=CoverBuild()
     Service(bp,cp)
     Assemble(bp,sp,cp)
     Reopen()
     Note("outcome","PASS")
    Catch ex As Exception
     Note("stage",stage):Note("exception",ex.ToString()):Note("outcome","STOPPED")
    End Try
    Note("end",DateTimeOffset.Now.ToString("O"))
   End Using
  End Using
 End Sub
End Module
