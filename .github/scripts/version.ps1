$ErrorActionPreference = 'Stop'
[xml]$props = Get-Content -LiteralPath 'Directory.Build.props'
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Version 必须为 主版本.次版本.修订号。'
}
$parts = [version]$version
$code = [long]$parts.Major * 1000000 + $parts.Minor * 1000 + $parts.Build
if ($parts.Minor -ge 1000 -or $parts.Build -ge 1000 -or $code -lt 1 -or $code -gt 2100000000) {
    throw '版本号超出 Android 内部序号范围。'
}
$suffix = '-test'
if ($env:GITHUB_REF_TYPE -eq 'tag') {
    if ($env:GITHUB_REF_NAME -cne "v$version") { throw "标签必须为 v$version。" }
    $suffix = ''
}
"version=$version" >> $env:GITHUB_OUTPUT
"suffix=$suffix" >> $env:GITHUB_OUTPUT
