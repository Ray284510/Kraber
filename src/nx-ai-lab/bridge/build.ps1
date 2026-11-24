param([string]$OutputDirectory, [string]$PythonExecutable)
$ErrorActionPreference='Stop'
$labRoot=Split-Path -Parent $PSScriptRoot
$env:NX_AI_LAB_ROOT=$labRoot
$env:TEMP=Join-Path $labRoot 'tmp'
$env:TMP=$env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
if(!$OutputDirectory){$OutputDirectory=Join-Path $labRoot ('tmp\verified-build-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))}
if(!$PythonExecutable){$PythonExecutable=Join-Path $labRoot '.venv\Scripts\python.exe'}
# A clean source snapshot may explicitly reuse the existing locked lab environment.
# No installation, symlink, deployment, or runtime launch is performed here.
& $PythonExecutable "$PSScriptRoot\verified_loader.py" $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Loader generation failed'}
if (!$env:UGII_BASE_DIR) { throw 'Set UGII_BASE_DIR to the NX installation root' }
$nxRoot=(Resolve-Path -LiteralPath $env:UGII_BASE_DIR).Path
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$managed=Join-Path $nxRoot 'NXBIN/managed'
$refs=foreach($name in 'NXOpen.dll','NXOpenUI.dll','NXOpen.Utilities.dll','NXOpen.UF.dll'){ '/reference:'+(Join-Path $managed $name) }
$generated=Join-Path $OutputDirectory 'NX12_Loader.cs'
$sources=foreach($inputFile in $generated,(Join-Path $PSScriptRoot 'phase5_step_readonly_probe.cs'),(Join-Path $PSScriptRoot 'phase5_independent_nurbs_topology.cs')){
    $destination=Join-Path $OutputDirectory ('local-'+[IO.Path]::GetFileName($inputFile))
    $source=[IO.File]::ReadAllText($inputFile).Replace('@NX_LAB_ROOT@',$labRoot).Replace('@NX_INSTALL_ROOT@',$nxRoot)
    [IO.File]::WriteAllText($destination,$source,(New-Object Text.UTF8Encoding($true)))
    $destination
}
& $compiler /nologo /target:library "/out:$OutputDirectory/NX12_Loader.dll" @refs /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll /reference:System.dll @sources
if($LASTEXITCODE -ne 0){throw 'NX12 loader compilation failed'}
