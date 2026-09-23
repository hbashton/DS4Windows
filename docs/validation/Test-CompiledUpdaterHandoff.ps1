#requires -Version 7.2
<#
Invokes the compiled application emitters and compiled updater policy/parsers.
It does not call Launch, Process.Start, a coordinator, the network, or an installer.
Temporary package markers and JSON evidence are the only files written.
Legacy -autolaunch cases are explicitly parser compatibility fixtures, not
observed emitter calls. Run in a fresh PowerShell process after building both repos.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationAssemblyPath,
    [Parameter(Mandatory)][string]$UpdaterAssemblyPath,
    [Parameter(Mandatory)][string]$ReleaseApiPath,
    [Parameter(Mandatory)][string]$ReleaseReceiptPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$cases = [Collections.Generic.List[object]]::new()
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function FullFile([string]$path) {
    Require ([IO.Path]::IsPathFullyQualified($path) -and -not $path.StartsWith('\\')) 'Require an explicit local absolute file path.'
    $item = Get-Item -LiteralPath $path
    Require (-not $item.PSIsContainer) 'Expected a file.'
    for ($node = $item; $null -ne $node; $node = if ($node -is [IO.FileInfo]) { $node.Directory } else { $node.Parent }) {
        Require (($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Evidence and assemblies must not pass through a reparse point.'
    }
    return $item.FullName
}
function Call([Type]$type, [string]$method, [object[]]$arguments) {
    $selected = $type.GetMethod($method, $static)
    Require ($null -ne $selected) "Compiled method missing: $($type.FullName).$method"
    $parameters = $selected.GetParameters()
    Require ($parameters.Count -eq $arguments.Count) "Compiled parameter count differs: $($type.FullName).$method"
    for ($i = 0; $i -lt $arguments.Count; $i++) {
        $arguments[$i] = [Management.Automation.LanguagePrimitives]::ConvertTo($arguments[$i], $parameters[$i].ParameterType)
    }
    return $selected.Invoke($null, $arguments)
}
function Property([object]$value, [string]$name) {
    return $value.GetType().GetProperty($name, $instance).GetValue($value)
}
function Constructor([Type]$type, [object[]]$arguments) {
    $constructors = @($type.GetConstructors($instance) | Where-Object { $_.GetParameters().Count -eq $arguments.Count })
    Require ($constructors.Count -eq 1) "Compiled constructor missing or ambiguous: $($type.FullName)"
    $parameters = $constructors[0].GetParameters()
    for ($i = 0; $i -lt $arguments.Count; $i++) {
        $arguments[$i] = [Management.Automation.LanguagePrimitives]::ConvertTo($arguments[$i], $parameters[$i].ParameterType)
    }
    return $constructors[0].Invoke($arguments)
}
function Record([string]$name, [object]$detail) { $cases.Add([ordered]@{ name = $name; passed = $true; detail = $detail }) }

$appPath = FullFile $ApplicationAssemblyPath
$updaterPath = FullFile $UpdaterAssemblyPath
$apiPath = FullFile $ReleaseApiPath
$receiptPath = FullFile $ReleaseReceiptPath
Require ([IO.Path]::GetFileName($appPath) -ceq 'DS4Windows.dll') 'Supply the compiled application DLL, not its apphost.'
Require ([IO.Path]::GetFileName($updaterPath) -ceq 'DS4Updater.dll') 'Supply the compiled updater DLL, not its apphost.'
Require (@([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -in @('DS4Windows', 'DS4Updater') }).Count -eq 0) 'Use a fresh PowerShell process.'
$appHash = (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash
$updaterHash = (Get-FileHash -LiteralPath $updaterPath -Algorithm SHA256).Hash
$app = [Reflection.Assembly]::LoadFrom($appPath)
$updater = [Reflection.Assembly]::LoadFrom($updaterPath)
Require ($app.Location -ceq $appPath -and $updater.Location -ceq $updaterPath) 'Loaded assemblies differ from requested build outputs.'
$managedEmitter = $app.GetType('DS4Windows.ManagedUpdaterBootstrap', $true)
$portableEmitter = $app.GetType('DS4Windows.PortableUpdaterBootstrap', $true)
$ticketType = $app.GetType('DS4Windows.PortableUpdaterTicket', $true)
$managedTicketType = $app.GetType('DS4Windows.ManagedUpdaterTicket', $true)
$deployment = $updater.GetType('DS4Updater.UpdateDeploymentPolicy', $true)
$managedParser = $updater.GetType('DS4Updater.ManagedUpdateRequest', $true)
$portableParser = $updater.GetType('DS4Updater.PortableUpdateRequest', $true)

Require ([IO.Path]::IsPathFullyQualified($EvidenceDirectory) -and -not $EvidenceDirectory.StartsWith('\\')) 'Require an explicit local evidence directory.'
$evidence = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($EvidenceDirectory))
Require ($evidence -ne [IO.Path]::GetPathRoot($evidence)) 'A drive root is not an evidence directory.'
$null = [IO.Directory]::CreateDirectory($evidence)
for ($node = Get-Item -LiteralPath $evidence; $null -ne $node; $node = $node.Parent) {
    Require (($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Evidence directory contains a reparse point.'
}
$proof = Join-Path $evidence ('compiled-handoff-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($proof)
$managedRoot = Join-Path $proof 'Registered installed folder'
$cacheRoot = Join-Path $proof 'Verified updater cache'
$portableRoot = Join-Path $proof 'Portable controllers (custom)'
foreach ($directory in @($managedRoot, $cacheRoot, $portableRoot)) { $null = [IO.Directory]::CreateDirectory($directory) }
$markerText = $app.GetType('DS4Windows.PortableBrokerContext', $true).GetField('MarkerText', $static).GetRawConstantValue()
$markerName = $app.GetType('DS4Windows.PortableBrokerContext', $true).GetField('MarkerFileName', $static).GetRawConstantValue()
foreach ($directory in @($managedRoot, $portableRoot)) {
    [IO.File]::WriteAllText((Join-Path $directory $markerName), $markerText)
}
[IO.File]::WriteAllText((Join-Path $portableRoot '.ds4windows-managed-files.txt'), "DS4Windows.exe`nDS4Updater.exe`n")
[Func[string,string]]$validateManaged = {
    param($path)
    if (-not [string]::Equals($path, $managedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected fake registered root.' }
    return $managedRoot
}
[Func[Collections.Generic.IEnumerable[string]]]$registeredRoots = { return ,([string[]]@($managedRoot)) }
[Func[string,string]]$findManaged = {
    param($path)
    return Call $deployment 'FindManagedRoot' @($path, $registeredRoots)
}
$tag = 'VIIPERRC4.6.5'
$customExe = 'My Controller Hub.exe'
$image = Constructor $ticketType @($cacheRoot, (Join-Path $cacheRoot 'DS4Updater.exe'), [Version]'2.0.8.0', ('0' * 64), [long]1)
$ticket = Constructor $managedTicketType @($managedRoot, $image)
$start = Call $managedEmitter 'CreateStartInfo' @($ticket, $tag, $customExe, $validateManaged)
$emitted = [string[]]@($start.ArgumentList)
$mode = Call $deployment 'Resolve' @($emitted, $start.FileName, $registeredRoots)
Require ($mode.ToString() -ceq 'Managed') 'Current managed emitter routed into another deployment.'
$parsed = Call $managedParser 'Parse' @($emitted, $start.FileName, $findManaged, $validateManaged)
Require ((Property $parsed 'TargetDirectory') -ceq $managedRoot) 'Managed target did not survive cache handoff.'
Require ((Property $parsed 'ReleaseTag') -ceq $tag -and (Property $parsed 'LaunchExe') -ceq $customExe) 'Managed release/custom executable did not round trip.'
Require ($start.FileName -ceq (Join-Path $cacheRoot 'DS4Updater.exe')) 'Managed handoff selected an updater outside the verified cache.'
Require ($start.WorkingDirectory -ceq $cacheRoot -and -not $start.UseShellExecute) 'Managed staged updater must use its verified cache without a shell.'
Record 'actual-compiled-managed-emitter-to-parser' ([ordered]@{ arguments = $emitted; target = $managedRoot; cache = $cacheRoot; classification = $mode.ToString() })

$portableImage = Constructor $ticketType @($portableRoot, (Join-Path $portableRoot 'DS4Updater.exe'), [Version]'2.0.7.0', ('0' * 64), [long]1)
$expectedPid = 43123
$expectedStart = [long]638933559010000000
$portableStart = Call $portableEmitter 'CreateStartInfo' @($portableImage, $tag, $customExe, $expectedPid, $expectedStart)
$portableArgs = [string[]]@($portableStart.ArgumentList)
$mode = Call $deployment 'Resolve' @($portableArgs, $portableStart.FileName, $registeredRoots)
Require ($mode.ToString() -ceq 'Portable') 'Current portable emitter routed into another deployment.'
$parsedPortable = Call $portableParser 'Parse' @($portableArgs, $portableStart.FileName)
Require ((Property $parsedPortable 'TargetDirectory') -ceq $portableRoot) 'Portable target changed.'
Require ((Property $parsedPortable 'ParentPid') -eq $expectedPid -and (Property $parsedPortable 'ParentStartUtcTicks') -eq $expectedStart) 'Portable parent identity changed.'
Require ((Property $parsedPortable 'ReleaseTag') -ceq $tag -and (Property $parsedPortable 'LaunchExe') -ceq $customExe) 'Portable tag or custom executable changed.'
Require (-not (Property $parsedPortable 'IsWorker') -and -not $portableStart.UseShellExecute) 'Initial portable handoff must not become a worker/shell launch.'
Record 'actual-compiled-portable-emitter-to-parser' ([ordered]@{ arguments = $portableArgs; target = $portableRoot; parentPid = $expectedPid; parentStartUtcTicks = $expectedStart; classification = $mode.ToString() })

foreach ($userHint in @($false, $true)) {
    # Deliberately labelled fixture: the old inline emitter cannot be called
    # without executing Process.Start, so this is a receiver compatibility test.
    $legacy = [Collections.Generic.List[string]]::new()
    $legacy.Add('-autolaunch')
    if ($userHint) { $legacy.Add('-user') }
    foreach ($value in @('--releaseTag', $tag, '--launchExe', $customExe)) { $legacy.Add($value) }
    $legacyArgs = $legacy.ToArray()
    $legacyPath = Join-Path $managedRoot 'DS4Updater.exe'
    $mode = Call $deployment 'Resolve' @($legacyArgs, $legacyPath, $registeredRoots)
    Require ($mode.ToString() -ceq 'Managed') 'Stale portable marker overrode managed ownership.'
    $legacyParsed = Call $managedParser 'Parse' @($legacyArgs, $legacyPath, $findManaged, $validateManaged)
    Require ((Property $legacyParsed 'TargetDirectory') -ceq $managedRoot -and (Property $legacyParsed 'LaunchExe') -ceq $customExe) 'Legacy installed hints changed target or executable.'
    Record "legacy-parser-only-stale-marker-userHint-$userHint" ([ordered]@{ arguments = $legacyArgs; classification = $mode.ToString(); emitterInvoked = $false })
}

$releaseType = $updater.GetType('DS4Updater.Dtos.GitHubRelease', $true)
$resolver = $updater.GetType('DS4Updater.PortableReleaseResolver', $true)
$release = [Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText($apiPath), $releaseType, [Text.Json.JsonSerializerOptions]::new())
$receipt = [IO.File]::ReadAllBytes($receiptPath)
$selected = Call $resolver 'ResolveInstaller' @($release, 'x64', $receipt)
$asset = Property $selected 'Asset'
Require ((Property $selected 'Tag') -ceq $tag -and (Property $selected 'FromBuildReceipt')) 'Published receipt did not bind the installer identity.'
Require ((Property $selected 'FileVersion').ToString() -ceq '5.0.11.0') 'Installer PE version mismatch.'
Require ($asset.name -ceq 'DS4Windows_5.0.11.0_Setup_x64.exe') 'Installer resolver selected the wrong asset.'
Require ($asset.browser_download_url -ceq "https://github.com/hbashton/DS4Windows/releases/download/$tag/DS4Windows_5.0.11.0_Setup_x64.exe") 'Installer resolver selected the wrong release URL.'
Record 'actual-published-rc465-installer-receipt-resolution' ([ordered]@{ releaseId = $release.id; tag = Property $selected 'Tag'; version = (Property $selected 'FileVersion').ToString(); asset = $asset.name; url = $asset.browser_download_url; digest = $asset.digest; downloadedOrExecuted = $false })
Require ((Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash -ceq $appHash -and (Get-FileHash -LiteralPath $updaterPath -Algorithm SHA256).Hash -ceq $updaterHash) 'Compiled assembly changed while running the proof.'
$result = [ordered]@{
    schema = 1
    createdUtc = [DateTime]::UtcNow.ToString('O')
    applicationAssembly = $appPath
    applicationSha256 = $appHash
    updaterAssembly = $updaterPath
    updaterSha256 = $updaterHash
    releaseApi = $apiPath
    releaseApiSha256 = (Get-FileHash -LiteralPath $apiPath -Algorithm SHA256).Hash
    receipt = $receiptPath
    receiptSha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash
    processLaunches = 0
    networkRequests = 0
    installedFileRegistryOrDriverMutations = 0
    validationBoundary = 'Compiled emitter/parser contract only, using fixture tickets and fake registered roots. Does not verify downloading, image signature/hash admission, process launching, shutdown, elevation, or installation.'
    cases = $cases.ToArray()
}
$resultPath = Join-Path $proof 'compiled-updater-handoff.json'
$result | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $resultPath -Encoding utf8
[pscustomobject]@{ Passed = $cases.Count; Proof = $resultPath; ApplicationSha256 = $appHash; UpdaterSha256 = $updaterHash }
