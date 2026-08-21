# Wraps hang-prone research commands (ilspycmd, decompile) with a process timeout.
$ErrorActionPreference = 'Stop'
$inputJson = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($inputJson)) {
    Write-Output '{ "permission": "allow" }'
    exit 0
}

$payload = $inputJson | ConvertFrom-Json
$toolName = [string]$payload.tool_name
$toolInput = $payload.tool_input
$command = [string]$toolInput.command
if ($toolName -cne 'Shell' -or [string]::IsNullOrWhiteSpace($command)) {
    Write-Output '{ "permission": "allow" }'
    exit 0
}

if ($command -match 'invoke-with-timeout\.ps1' -or $command -match 'COMMAND_TIMEOUT') {
    Write-Output '{ "permission": "allow" }'
    exit 0
}

$isHangProne = $command -match '(?i)(\bilspycmd\b|decompile-sea-power\.ps1|\bpnpm\s+decompile)'
if (-not $isHangProne) {
    Write-Output '{ "permission": "allow" }'
    exit 0
}

$timeoutSeconds = 60
if ($command -match '(?i)(--project|decompile-sea-power\.ps1|\bpnpm\s+decompile)') {
    $timeoutSeconds = 600
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($payload.workspace_roots -and $payload.workspace_roots.Count -gt 0) {
    $repoRoot = [string]$payload.workspace_roots[0]
}

$wrapper = Join-Path $repoRoot 'scripts\invoke-with-timeout.ps1'
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($command))
$wrapped = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$wrapper`" -TimeoutSeconds $timeoutSeconds -CommandBase64 $encoded"
$blockUntilMs = ($timeoutSeconds * 1000) + 5000

$updated = [ordered]@{
    command = $wrapped
    block_until_ms = $blockUntilMs
    timeout = $blockUntilMs
}
if ($toolInput.cwd) { $updated.cwd = $toolInput.cwd }
if ($toolInput.working_directory) { $updated.working_directory = $toolInput.working_directory }
if ($toolInput.description) { $updated.description = $toolInput.description }

$response = [ordered]@{
    permission = 'allow'
    updated_input = $updated
    agent_message = "Wrapped hang-prone command with a ${timeoutSeconds}s process timeout. Exit 124 / COMMAND_TIMEOUT means it hung."
}
$response | ConvertTo-Json -Compress -Depth 6
exit 0
