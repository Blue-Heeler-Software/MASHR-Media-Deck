#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path (Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows10.0.19041.0\MediaDeck.Companion.exe')).Path

$filters = Get-NetFirewallApplicationFilter -PolicyStore ActiveStore |
    Where-Object { $_.Program -like '*MediaDeck*' }
foreach ($filter in $filters) {
    foreach ($rule in (Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $filter)) {
        Disable-NetFirewallRule -Name $rule.Name | Out-Null
    }
}

Get-NetFirewallRule -DisplayName 'MediaDeck Companion TCP (Private)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'MediaDeck Discovery UDP (Private)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

New-NetFirewallRule `
    -DisplayName 'MediaDeck Companion TCP (Private)' `
    -Description 'Authenticated MediaDeck control channel; private local subnet only.' `
    -Direction Inbound -Action Allow -Program $exe -Protocol TCP -LocalPort 43821 `
    -Profile Private -RemoteAddress LocalSubnet | Out-Null

New-NetFirewallRule `
    -DisplayName 'MediaDeck Discovery UDP (Private)' `
    -Description 'MediaDeck discovery broadcast; private local subnet only.' `
    -Direction Inbound -Action Allow -Program $exe -Protocol UDP -LocalPort 43822 `
    -Profile Private -RemoteAddress LocalSubnet | Out-Null

Write-Host 'MediaDeck firewall rules now allow only the current build, Private profile, and local subnet.'
