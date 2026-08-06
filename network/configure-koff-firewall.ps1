$ErrorActionPreference = 'Stop'

$rules = @(
    @{ Name = 'KOFF Online Lobby TCP 5088'; Protocol = 'TCP'; Port = 5088 },
    @{ Name = 'KOFF Online IKEMEN UDP 7500'; Protocol = 'UDP'; Port = 7500 },
    @{ Name = 'KOFF Online IKEMEN TCP 7500'; Protocol = 'TCP'; Port = 7500 }
)

foreach ($rule in $rules) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
            -Protocol $rule.Protocol -LocalPort $rule.Port -Profile Any | Out-Null
    }
}
