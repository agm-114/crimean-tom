# Surfaces COMMAND_TIMEOUT to the agent so a hung command is not mistaken for silence.
$ErrorActionPreference = 'Stop'
$inputJson = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($inputJson)) {
    exit 0
}

$payload = $inputJson | ConvertFrom-Json
$output = ''
if ($payload.tool_output) { $output = [string]$payload.tool_output }
elseif ($payload.output) { $output = [string]$payload.output }

if ($output -notmatch 'COMMAND_TIMEOUT') {
    exit 0
}

@{
    additional_context = 'The shell command hit COMMAND_TIMEOUT and was killed. Treat that as failure, do not wait for it, and retry only with a narrower command or an explicit longer timeout.'
} | ConvertTo-Json -Compress
exit 0
