#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('PairedPhone', 'SameSubnet')]
    [string]$Scope = 'PairedPhone',

    [string]$PhoneAddress,

    [switch]$Disable
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path (Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows10.0.19041.0\MediaDeck.Companion.exe')).Path

function Test-SameSubnet {
    param([System.Net.IPAddress]$Left, [System.Net.IPAddress]$Right, [int]$PrefixLength)
    $a = $Left.GetAddressBytes()
    $b = $Right.GetAddressBytes()
    if ($a.Length -ne 4 -or $b.Length -ne 4 -or $PrefixLength -lt 0 -or $PrefixLength -gt 32) { return $false }
    for ($index = 0; $index -lt 4; $index++) {
        $bits = [Math]::Min(8, [Math]::Max(0, $PrefixLength - $index * 8))
        $mask = if ($bits -eq 0) { 0 } else { (0xFF -shl (8 - $bits)) -band 0xFF }
        if (($a[$index] -band $mask) -ne ($b[$index] -band $mask)) { return $false }
    }
    return $true
}

function Get-NetworkAddress {
    param([System.Net.IPAddress]$Address, [int]$PrefixLength)
    $bytes = $Address.GetAddressBytes()
    for ($index = 0; $index -lt 4; $index++) {
        $bits = [Math]::Min(8, [Math]::Max(0, $PrefixLength - $index * 8))
        $mask = if ($bits -eq 0) { 0 } else { (0xFF -shl (8 - $bits)) -band 0xFF }
        $bytes[$index] = [byte]($bytes[$index] -band $mask)
    }
    return [System.Net.IPAddress]::new($bytes)
}

function Stop-MediaDeck {
    $task = Get-ScheduledTask -TaskName 'MediaDeck Companion' -ErrorAction SilentlyContinue
    if ($task) { Stop-ScheduledTask -TaskName 'MediaDeck Companion' -ErrorAction SilentlyContinue }
    Get-Process -Name 'MediaDeck.Companion' -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Start-MediaDeck {
    $task = Get-ScheduledTask -TaskName 'MediaDeck Companion' -ErrorAction SilentlyContinue
    if ($task) {
        Enable-ScheduledTask -TaskName 'MediaDeck Companion' | Out-Null
        Start-ScheduledTask -TaskName 'MediaDeck Companion'
    } else {
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
    }
}

function Disable-ExistingRules {
    $filters = Get-NetFirewallApplicationFilter -PolicyStore ActiveStore |
        Where-Object { $_.Program -like '*MediaDeck*' }
    foreach ($filter in $filters) {
        foreach ($rule in (Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $filter)) {
            Disable-NetFirewallRule -Name $rule.Name | Out-Null
        }
    }
    Get-NetFirewallRule -DisplayName 'MediaDeck Companion TCP (Restricted)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Get-NetFirewallRule -DisplayName 'MediaDeck Discovery UDP (Restricted)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

Stop-MediaDeck
Disable-ExistingRules

if ($Disable) {
    $configuration = Start-Process -FilePath $exe -ArgumentList '--disable-lan' -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -Wait -PassThru
    if ($configuration.ExitCode -ne 0) { throw 'MASHR Media Deck refused the loopback-only configuration.' }
    Start-MediaDeck
    Write-Host 'MASHR Media Deck LAN access is disabled; only this PC can reach the companion.'
    return
}

$phone = $null
if (-not [System.Net.IPAddress]::TryParse($PhoneAddress, [ref]$phone) -or $phone.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Provide an IPv4 address for a phone on the target local subnet with -PhoneAddress.'
}

$connection = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object {
        $_.AddressState -eq 'Preferred' -and
        $_.IPAddress -ne '127.0.0.1' -and
        (Test-SameSubnet -Left ([System.Net.IPAddress]::Parse($_.IPAddress)) -Right $phone -PrefixLength $_.PrefixLength)
    } |
    Sort-Object SkipAsSource, InterfaceIndex |
    Select-Object -First 1
if (-not $connection) { throw "Phone $PhoneAddress is not on a directly connected PC subnet." }

$network = Get-NetworkAddress -Address ([System.Net.IPAddress]::Parse($connection.IPAddress)) -PrefixLength $connection.PrefixLength
$remoteScope = if ($Scope -eq 'PairedPhone') { $PhoneAddress } else { "$network/$($connection.PrefixLength)" }
$mode = if ($Scope -eq 'PairedPhone') { 'paired-phone' } else { 'same-subnet' }

New-NetFirewallRule `
    -DisplayName 'MediaDeck Companion TCP (Restricted)' `
    -Description "Authenticated MASHR Media Deck control; $Scope scope only." `
    -Direction Inbound -Action Allow -Program $exe -Protocol TCP -LocalPort 43821 `
    -LocalAddress $connection.IPAddress -RemoteAddress $remoteScope -InterfaceAlias $connection.InterfaceAlias `
    -Profile Any | Out-Null

New-NetFirewallRule `
    -DisplayName 'MediaDeck Discovery UDP (Restricted)' `
    -Description "MASHR Media Deck discovery; $Scope scope only." `
    -Direction Inbound -Action Allow -Program $exe -Protocol UDP -LocalPort 43822 `
    -LocalAddress $connection.IPAddress -RemoteAddress $remoteScope -InterfaceAlias $connection.InterfaceAlias `
    -Profile Any | Out-Null

$configuration = Start-Process -FilePath $exe -ArgumentList @('--configure-lan', $mode, $PhoneAddress) -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -Wait -PassThru
if ($configuration.ExitCode -ne 0) { throw 'MASHR Media Deck rejected the requested LAN scope.' }

Start-MediaDeck
Write-Host "MASHR Media Deck is bound to $($connection.IPAddress) and firewall-scoped to $remoteScope ($Scope)."
