Option Strict On
Imports System
Imports System.IO
Imports System.Globalization
Imports System.Diagnostics
Imports System.Security.Cryptography
Imports NXOpen
Imports NXOpen.UF

Module FacetedStlImport
    Private input, output, audit As String
    Private sw As StreamWriter
    Private phase As String = "entry"

    Sub Record(ByVal k As String, ByVal v As String)
        sw.WriteLine(k & "=" & v)
        sw.Flush()
    End Sub
    Sub Need(ByVal ok As Boolean, ByVal msg As String)
        If Not ok Then Throw New InvalidOperationException(msg)
    End Sub
    Function Digest(ByVal path As String) As String
        Using f As FileStream = File.OpenRead(path)
            Using h As SHA256 = SHA256.Create()
                Return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant()
            End Using
        End Using
    End Function
    Sub OnlyTarget(ByVal s As Session, ByVal p As Part)
        Need(p IsNot Nothing AndAlso s.Parts.Work IsNot Nothing AndAlso s.Parts.Display IsNot Nothing, "No active target")
        Need(s.Parts.Work.Tag = p.Tag AndAlso s.Parts.Display.Tag = p.Tag, "Target role changed")
        Need(String.Equals(p.FullPath, output, StringComparison.OrdinalIgnoreCase), "Target path changed")
        Dim n As Integer = 0
        For Each b As BasePart In s.Parts
            n += 1
            Need(b.Tag=p.Tag, "Unexpected loaded part")
        Next
        Need(n=1, "Expected exactly one part")
    End Sub
    Function Measure(ByVal s As Session, ByVal uf As UFSession, ByVal p As Part, ByVal tagName As String) As Double()
        OnlyTarget(s,p)
        Record(tagName & ".path", p.FullPath)
        Record(tagName & ".units", p.PartUnits.ToString())
        Need(p.PartUnits = BasePart.Units.Millimeters, "Expected mm")
        Dim facets As Integer = 0
        Dim normalBodies As Integer = 0
        Dim b As NXOpen.Facet.FacetedBody = Nothing
        For Each f As NXOpen.Facet.FacetedBody In p.FacetedBodies
            b=f
            facets+=1
        Next
        Dim cb As Body=Nothing
        For Each body As Body In p.Bodies
            cb=body
            normalBodies+=1
        Next
        Record(tagName & ".facet_bodies",facets.ToString())
        Record(tagName & ".part_bodies",normalBodies.ToString())
        Dim lo As Double()={Double.PositiveInfinity,Double.PositiveInfinity,Double.PositiveInfinity}
        Dim hi As Double()={Double.NegativeInfinity,Double.NegativeInfinity,Double.NegativeInfinity}
        Dim count As Integer=0, nFacets As Integer=0
        If facets=1 AndAlso normalBodies=0 Then

        Record(tagName & ".body_type", b.GetType().FullName)
        Record(tagName & ".body_tag", b.Tag.ToString())
        Dim capacity As Integer
        uf.Facet.AskNFacetsInModel(b.Tag,nFacets)
        uf.Facet.AskMaxFacetVerts(b.Tag,capacity)
        Need(nFacets>0 AndAlso capacity>=3 AndAlso capacity<=1024, "Invalid NX facet census")
        Dim idx As Integer=-1
        Dim verts(capacity-1,2) As Double
        Do
            uf.Facet.CycleFacets(b.Tag,idx)
            If idx=-1 Then Exit Do
            count+=1
            Need(count<=nFacets, "Facet cycle exceeded census")
            Dim nv As Integer=0
            uf.Facet.AskVerticesOfFacet(b.Tag,idx,nv,verts)
            Need(nv=3, "Non-triangular facet")
            For j As Integer=0 To nv-1
                For k As Integer=0 To 2
                    Dim v As Double=verts(j,k)
                    Need(Not Double.IsInfinity(v) AndAlso Not Double.IsNaN(v), "Nonfinite NX vertex")
                    lo(k)=Math.Min(lo(k),v)
                    hi(k)=Math.Max(hi(k),v)
                Next
            Next
        Loop
        ElseIf normalBodies=1 AndAlso cb IsNot Nothing AndAlso cb.IsConvergentBody AndAlso facets=0 Then
            Record(tagName & ".body_type","NXOpen.Body (Convergent)")
            Record(tagName & ".body_tag",cb.Tag.ToString())
            nFacets=cb.GetNumberOfFacets()
            Dim cf As ConvergentFacet=cb.GetFirstFacetOnBody()
            While cf IsNot Nothing
                Dim cs As Point3d()=cf.GetVertices()
                Need(cs.Length=3,"Non-triangular convergent facet")
                For Each pt As Point3d In cs
                    Dim pos As Double()={pt.X,pt.Y,pt.Z}
                    For k As Integer=0 To 2
                        Need(Not Double.IsNaN(pos(k)) AndAlso Not Double.IsInfinity(pos(k)),"Nonfinite coordinate")
                        lo(k)=Math.Min(lo(k),pos(k))
                        hi(k)=Math.Max(hi(k),pos(k))
                    Next
                Next
                count+=1
                Need(count<=nFacets,"Convergent facet cycle exceeded census")
                cf=cb.GetNextFacet(cf)
            End While
        Else
            Throw New InvalidOperationException("Unexpected body inventory; see recorded counts")
        End If
        Record(tagName & ".facets",count.ToString())
        Need(count=nFacets AndAlso count>0,"Facet count changed")
        Dim vals As Double()={hi(0)-lo(0),hi(1)-lo(1),hi(2)-lo(2)}
        For k As Integer=0 To 2
            Record(tagName & ".axis" & k.ToString(),vals(k).ToString("R",CultureInfo.InvariantCulture))
        Next
        Return vals
    End Function
    Public Sub Main()
        Dim root As String = Environment.GetEnvironmentVariable("NX_EXAMPLE_DIR")
        Need(Not String.IsNullOrWhiteSpace(root) AndAlso Path.IsPathRooted(root) AndAlso Directory.Exists(root), "Set NX_EXAMPLE_DIR to an existing absolute directory")
        input = Path.Combine(root, "input.stl")
        output = Path.Combine(root, "faceted.prt")
        audit = Path.Combine(root, "faceted.log")
        Using logStream As New FileStream(audit, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            Using writer As New StreamWriter(logStream)
                sw=writer
                Try
                    Record("started",DateTimeOffset.Now.ToString("O"))
                    Dim pid As Integer=Process.GetCurrentProcess().Id
                    Record("pid",pid.ToString())
                    Need(File.Exists(input) AndAlso Not File.Exists(output), "Missing input or output collision")
                    Dim s As Session=Session.GetSession()
                    Dim uf As UFSession=UFSession.GetUFSession()
                    For Each old As BasePart In s.Parts
                        Throw New InvalidOperationException("Existing user part present")
                    Next
                    phase="new-display"
                    Dim p As Part=s.Parts.NewDisplay(output,Part.Units.Millimeters)
                    OnlyTarget(s,p)
                    phase="stl-import"
                    Dim imp As STLImporter=p.ImportManager.CreateStlImporter()
                    Try
                        imp.FileName=input
                        imp.FileFormat=STLImporter.FileFormatType.Binary
                        imp.FileUnits=STLImporter.FileUnitsType.Millimeters
                        imp.DisplayInformation=False
                        imp.Commit()
                    Finally
                        imp.Destroy()
                    End Try
                    Record("import_commit","returned")
                    phase="measure-import"
                    Dim first As Double()=Measure(s,uf,p,"import")
                    phase="save"
                    Dim saved As PartSaveStatus=p.Save(BasePart.SaveComponents.[False],BasePart.CloseAfterSave.[False])
                    Try
                        Need(saved.NumberUnsavedParts=0 AndAlso saved.NumberUnsavedObjects=0, "Unsaved items")
                    Finally
                        saved.Dispose()
                    End Try
                    OnlyTarget(s,p)
                    Need(Not uf.Part.IsModified(p.Tag) AndAlso File.Exists(output),"Save did not leave clean part")
                    Record("saved_sha256",Digest(output))
                    phase="close-target"
                    p.Close(BasePart.CloseWholeTree.[False],BasePart.CloseModified.CloseModified,Nothing)
                    For Each leftover As BasePart In s.Parts
                        Throw New InvalidOperationException("Unexpected remaining part")
                    Next
                    Record("close_target","returned; loaded parts zero")
                    phase="reopen"
                    Dim ls As PartLoadStatus=Nothing
                    Dim again As Part=s.Parts.OpenDisplay(output,ls)
                    Try
                        Need(ls.NumberUnloadedParts=0,"Load reported incomplete parts")
                    Finally
                        ls.Dispose()
                    End Try
                    Dim second As Double()=Measure(s,uf,again,"reopen")
                    For k As Integer=0 To 2
                        Need(Math.Abs(first(k)-second(k))<=0.000001R,"Reopen changed dimensions")
                    Next
                    Need(Not uf.Part.IsModified(again.Tag),"Reopened part modified")
                    Record("reopen.modified","false")
                    Record("outcome","PASS")
                Catch exc As Exception
                    Record("phase",phase)
                    If TypeOf exc Is NXException Then Record("nx_error_code",DirectCast(exc,NXException).ErrorCode.ToString())
                    Record("exception",exc.ToString())
                    Record("outcome","STOPPED")
                End Try
                Record("ended",DateTimeOffset.Now.ToString("O"))
            End Using
        End Using
    End Sub
End Module
