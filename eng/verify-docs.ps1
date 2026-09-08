#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$problems = [Collections.Generic.List[string]]::new()
$anchors = @{}
$linkCount = 0
$jsonCount = 0

function Get-Anchors([string] $Path) {
    if ($anchors.ContainsKey($Path)) { return $anchors[$Path] }
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $duplicates = @{}
    $body = Get-Content $Path -Raw
    $body = [regex]::Replace($body, '(?ms)^```.*?^```[^\r\n]*', '')
    foreach ($heading in [regex]::Matches($body, '(?m)^#{1,6}\s+(.+?)\s*#*\r?$')) {
        $slug = $heading.Groups[1].Value.ToLowerInvariant()
        $slug = [regex]::Replace($slug, '<[^>]+>', '')
        # Rendered heading text follows GitHub's documented section-link rules.
        $slug = [regex]::Replace($slug, '\[([^\]]+)\]\([^)]*\)', '$1')
        $slug = [regex]::Replace($slug, '[^\p{L}\p{N}\p{M} _-]', '').Replace(' ', '-')
        $base = $slug
        if ($duplicates.ContainsKey($base)) { $duplicates[$base]++; $slug += '-' + $duplicates[$base] }
        else { $duplicates[$base] = 0 }
        $null = $set.Add($slug)
    }
    foreach ($explicit in [regex]::Matches($body, '<a\s+(?:name|id)="([^"]+)"')) { $null = $set.Add($explicit.Groups[1].Value) }
    $anchors[$Path] = $set
    return ,$set
}

Push-Location $repo
try {
    $files = @(& git ls-files -- '*.md')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked documentation.' }
    foreach ($file in $files) {
        $body = Get-Content -LiteralPath $file -Raw
        foreach ($block in [regex]::Matches($body, '(?ms)^```(jsonc?)\s*\r?\n(.*?)^```')) {
            $jsonCount++
            try {
                $options = [System.Text.Json.JsonDocumentOptions]::new()
                if ($block.Groups[1].Value -eq 'jsonc') { $options.CommentHandling = 'Skip'; $options.AllowTrailingCommas = $true }
                $parsed = [System.Text.Json.JsonDocument]::Parse($block.Groups[2].Value, $options)
                $parsed.Dispose()
            }
            catch { $problems.Add("${file}: invalid JSON example: $($_.Exception.Message)") }
        }
        $prose = [regex]::Replace($body, '(?ms)^```.*?^```[^\r\n]*', '')
        $prose = [regex]::Replace($prose, '`[^`\r\n]+`', '')
        $links = [regex]::Matches($prose, '\[[^\]\r\n]*\]\((?<url><[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)|(?m)^\[[^\]]+\]:\s*(?<url>\S+)')
        foreach ($link in $links) {
            $url = $link.Groups['url'].Value.Trim('<', '>')
            if ($url -match '^(?:[a-z][a-z0-9+.-]*:|//)') { continue }
            $linkCount++
            $parts = $url.Split('#', 2)
            $relative = [Uri]::UnescapeDataString($parts[0].Split('?')[0])
            $target = if (-not $relative) { Join-Path $repo $file }
                elseif ($relative.StartsWith('/')) { Join-Path $repo $relative.TrimStart('/') }
                else { Join-Path (Split-Path (Join-Path $repo $file)) $relative }
            $target = [IO.Path]::GetFullPath($target)
            if (-not (Test-Path -LiteralPath $target)) { $problems.Add("${file}: missing $url"); continue }
            if ($parts.Length -eq 2 -and $parts[1] -and [IO.Path]::GetExtension($target) -eq '.md') {
                $fragment = [Uri]::UnescapeDataString($parts[1])
                if (-not (Get-Anchors $target).Contains($fragment)) { $problems.Add("${file}: missing anchor $url") }
            }
        }
    }
}
finally { Pop-Location }
if ($problems.Count) { $problems | Write-Output; throw "$($problems.Count) documentation errors." }
Write-Output "Validated $($files.Count) Markdown files, $linkCount local links/anchors and $jsonCount JSON examples. External URLs are not fetched."
