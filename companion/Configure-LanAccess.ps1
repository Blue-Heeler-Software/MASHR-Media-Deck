#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('PairedPhone', 'SameSubnet')]
    [string]$Scope = 'PairedPhone',

    [string[]]$PhoneAddress,

    [switch]$Disable
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.19041.0\MediaDeck.Companion.exe')).Path

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

function Test-PrivateLanAddress {
    param([System.Net.IPAddress]$Address)
    $bytes = $Address.GetAddressBytes()
    return $bytes.Length -eq 4 -and (
        $bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    )
}

function Stop-MediaDeck {
    $task = Get-ScheduledTask -TaskName 'MediaDeck Companion' -ErrorAction SilentlyContinue
    if ($task) { Stop-ScheduledTask -TaskName 'MediaDeck Companion' -ErrorAction SilentlyContinue }
    Get-Process -Name 'MediaDeck.Companion' -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Start-MediaDeck {
    $task = Get-ScheduledTask -TaskName 'MediaDeck Companion' -ErrorAction SilentlyContinue
    if ($task) {
        $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
        Set-ScheduledTask -TaskName 'MediaDeck Companion' -Action $action | Out-Null
        Enable-ScheduledTask -TaskName 'MediaDeck Companion' | Out-Null
        Start-ScheduledTask -TaskName 'MediaDeck Companion'
    } else {
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
    }
}

function Disable-ExistingRules {
    $knownNames = @(
        'mediadeck.companion.exe',
        'MediaDeck Companion TCP (Restricted)',
        'MediaDeck Discovery UDP (Restricted)'
    )
    foreach ($name in $knownNames) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Disable-NetFirewallRule | Out-Null
    }
    Get-NetFirewallRule -DisplayName 'MediaDeck Companion TCP (Restricted)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Get-NetFirewallRule -DisplayName 'MediaDeck Discovery UDP (Restricted)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if ($Disable) {
    Stop-MediaDeck
    Disable-ExistingRules
    $configuration = Start-Process -FilePath $exe -ArgumentList '--disable-lan' -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -Wait -PassThru
    if ($configuration.ExitCode -ne 0) { throw 'MASHR Media Deck refused the loopback-only configuration.' }
    Start-MediaDeck
    Write-Host 'MASHR Media Deck LAN access is disabled; only this PC can reach the companion.'
    return
}

$phoneTexts = @(
    $PhoneAddress |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        Select-Object -Unique
)
if ($phoneTexts.Count -lt 1 -or $phoneTexts.Count -gt 16) {
    throw 'Provide between one and sixteen private IPv4 LAN addresses with -PhoneAddress.'
}
if ($Scope -eq 'SameSubnet' -and $phoneTexts.Count -ne 1) {
    throw 'SameSubnet mode accepts exactly one address used to select the directly connected subnet.'
}

$routes = @()
foreach ($phoneText in $phoneTexts) {
    $phone = $null
    if (-not [System.Net.IPAddress]::TryParse($phoneText, [ref]$phone) -or
        $phone.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
        -not (Test-PrivateLanAddress -Address $phone)) {
        throw "Phone '$phoneText' is not a private IPv4 LAN address."
    }

    $directConnection = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object {
            $_.AddressState -eq 'Preferred' -and
            $_.IPAddress -ne '127.0.0.1' -and
            (Test-SameSubnet -Left ([System.Net.IPAddress]::Parse($_.IPAddress)) -Right $phone -PrefixLength $_.PrefixLength)
        } |
        Sort-Object SkipAsSource, InterfaceIndex |
        Select-Object -First 1
    $connection = $directConnection
    if ($Scope -eq 'PairedPhone' -and -not $connection) {
        $connection = Find-NetRoute -RemoteIPAddress $phoneText |
            Where-Object { $_.CimClass.CimClassName -eq 'MSFT_NetIPAddress' -and $_.AddressState -eq 'Preferred' } |
            Select-Object -First 1
    }
    if (-not $connection) {
        throw $(if ($Scope -eq 'SameSubnet') {
            "Phone $phoneText is not on a directly connected PC subnet."
        } else {
            "Windows has no usable IPv4 route to phone $phoneText."
        })
    }
    if (-not (Test-PrivateLanAddress -Address ([System.Net.IPAddress]::Parse($connection.IPAddress)))) {
        throw "Windows routes to phone $phoneText through non-private address $($connection.IPAddress); refusing LAN mode."
    }
    $profile = Get-NetConnectionProfile -InterfaceIndex $connection.InterfaceIndex -ErrorAction SilentlyContinue
    if (-not $profile) { throw "Windows has no network profile for interface '$($connection.InterfaceAlias)'." }
    if ($Scope -eq 'SameSubnet' -and $profile.NetworkCategory -ne 'Private') {
        throw "SameSubnet mode requires interface '$($connection.InterfaceAlias)' to be a trusted Private network."
    }
    $firewallProfile = switch ([string]$profile.NetworkCategory) {
        'Public' { 'Public' }
        'Private' { 'Private' }
        'DomainAuthenticated' { 'Domain' }
        default { throw "Unsupported Windows network profile '$($profile.NetworkCategory)'." }
    }
    $routes += [pscustomobject]@{
        PhoneAddress = $phoneText
        Connection = $connection
        FirewallProfile = $firewallProfile
    }
}

$primaryConnection = $routes[0].Connection
$network = Get-NetworkAddress -Address ([System.Net.IPAddress]::Parse($primaryConnection.IPAddress)) -PrefixLength $primaryConnection.PrefixLength
$remoteScope = if ($Scope -eq 'PairedPhone') { @($routes.PhoneAddress) } else { @("$network/$($primaryConnection.PrefixLength)") }
$localScope = @($routes.Connection.IPAddress | Select-Object -Unique)
$interfaceScope = @($routes.Connection.InterfaceAlias | Select-Object -Unique)
$profileScope = @($routes.FirewallProfile | Select-Object -Unique)
$mode = if ($Scope -eq 'PairedPhone') { 'paired-phone' } else { 'same-subnet' }

Stop-MediaDeck
Disable-ExistingRules

New-NetFirewallRule `
    -DisplayName 'MediaDeck Companion TCP (Restricted)' `
    -Description "Authenticated MASHR Media Deck control; $Scope scope only." `
    -Direction Inbound -Action Allow -Program $exe -Protocol TCP -LocalPort 43821 `
    -LocalAddress $localScope -RemoteAddress $remoteScope -InterfaceAlias $interfaceScope `
    -Profile $profileScope | Out-Null

New-NetFirewallRule `
    -DisplayName 'MediaDeck Discovery UDP (Restricted)' `
    -Description "MASHR Media Deck discovery; $Scope scope only." `
    -Direction Inbound -Action Allow -Program $exe -Protocol UDP -LocalPort 43822 `
    -LocalAddress $localScope -RemoteAddress $remoteScope -InterfaceAlias $interfaceScope `
    -Profile $profileScope | Out-Null

$phoneArgument = $routes.PhoneAddress -join ','
$configuration = Start-Process -FilePath $exe -ArgumentList @('--configure-lan', $mode, $phoneArgument, 'scoped-route-validated') -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -Wait -PassThru
if ($configuration.ExitCode -ne 0) { throw 'MASHR Media Deck rejected the requested LAN scope.' }

Start-MediaDeck
Write-Host "MASHR Media Deck is bound to $($localScope -join ', ') and firewall-scoped to $($remoteScope -join ', ') ($Scope; profiles $($profileScope -join ', '))."
