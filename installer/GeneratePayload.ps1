param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,
    [string]$OutputFile = (Join-Path $PSScriptRoot 'Payload.wxs')
)

$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputFile)

function Get-StableId([string]$prefix, [string]$value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant())
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return $prefix + ([Convert]::ToHexString($hash).Substring(0, 24))
}

function Get-StableGuid([string]$value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes('DocConvert.Payload.' + $value.ToLowerInvariant())
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($hash, $guidBytes, 16)
    $guidBytes[7] = ($guidBytes[7] -band 0x0F) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new($guidBytes)).ToString().ToUpperInvariant()
}

function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

$files = Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Sort-Object FullName
if ($files.Count -eq 0) { throw "Publish directory is empty: $publishRoot" }
if (-not ($files.Name -contains 'PDFConverter.exe')) { throw 'PDFConverter.exe is missing from the publish directory.' }

$relativeDirectories = $files | ForEach-Object {
    $relative = [System.IO.Path]::GetRelativePath($publishRoot, $_.DirectoryName)
    if ($relative -ne '.') { $relative }
} | Sort-Object -Unique

$directoryIds = @{ '.' = 'INSTALLFOLDER' }
foreach ($relativeDirectory in $relativeDirectories) {
    $directoryIds[$relativeDirectory] = Get-StableId 'Dir' $relativeDirectory
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$builder.AppendLine('  <Fragment>')
[void]$builder.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')

function Write-DirectoryTree([string]$parent, [int]$indent) {
    $children = $relativeDirectories | Where-Object {
        $candidateParent = [System.IO.Path]::GetDirectoryName($_)
        if ([string]::IsNullOrEmpty($candidateParent)) { $candidateParent = '.' }
        $candidateParent -eq $parent
    }
    foreach ($child in $children) {
        $name = [System.IO.Path]::GetFileName($child)
        [void]$builder.AppendLine((' ' * $indent) + '<Directory Id="' + $directoryIds[$child] + '" Name="' + (Escape-Xml $name) + '">')
        Write-DirectoryTree $child ($indent + 2)
        [void]$builder.AppendLine((' ' * $indent) + '</Directory>')
    }
}

Write-DirectoryTree '.' 6
[void]$builder.AppendLine('    </DirectoryRef>')
[void]$builder.AppendLine('  </Fragment>')
[void]$builder.AppendLine('  <Fragment>')
[void]$builder.AppendLine('    <ComponentGroup Id="PublishFiles">')
foreach ($relativeDirectory in @('.') + $relativeDirectories) {
    $componentId = Get-StableId 'Cmp' $relativeDirectory
    $componentGuid = Get-StableGuid $relativeDirectory
    $removeFolderId = Get-StableId 'Rmv' $relativeDirectory
    $registryName = Get-StableId 'Payload' $relativeDirectory
    [void]$builder.AppendLine('      <Component Id="' + $componentId + '" Directory="' + $directoryIds[$relativeDirectory] + '" Guid="' + $componentGuid + '">')
    $directoryFiles = $files | Where-Object {
        $relativeFile = [System.IO.Path]::GetRelativePath($publishRoot, $_.FullName)
        $fileDirectory = [System.IO.Path]::GetDirectoryName($relativeFile)
        if ([string]::IsNullOrEmpty($fileDirectory)) { $fileDirectory = '.' }
        $fileDirectory -eq $relativeDirectory
    }
    foreach ($file in $directoryFiles) {
        $relativeFile = [System.IO.Path]::GetRelativePath($publishRoot, $file.FullName)
        $fileId = Get-StableId 'Fil' $relativeFile
        $source = '$(var.PublishDir)\' + $relativeFile
        [void]$builder.AppendLine('        <File Id="' + $fileId + '" Source="' + (Escape-Xml $source) + '" />')
    }
    [void]$builder.AppendLine('        <RemoveFolder Id="' + $removeFolderId + '" Directory="' + $directoryIds[$relativeDirectory] + '" On="uninstall" />')
    [void]$builder.AppendLine('        <RegistryValue Root="HKCU" Key="Software\DocConvert\Payload" Name="' + $registryName + '" Type="integer" Value="1" KeyPath="yes" />')
    [void]$builder.AppendLine('      </Component>')
}
[void]$builder.AppendLine('    </ComponentGroup>')
[void]$builder.AppendLine('  </Fragment>')
[void]$builder.AppendLine('</Wix>')

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputPath)) | Out-Null
[System.IO.File]::WriteAllText($outputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $outputPath with $($files.Count) files."
