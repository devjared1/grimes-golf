# Sends a shot to Grimes Golf (or GSPro) the way a GSPro Open Connect connector does, to try Open Connect without a
# launch monitor. Turn on "Accept shots from GSPro connector apps" in Settings (or run with --open-connect) first.
#
#   .\tools\send-openconnect-shot.ps1                                  # a 7-iron-ish shot
#   .\tools\send-openconnect-shot.ps1 -Speed 160 -Launch 11 -Spin 2600 -Axis -5 -Direction 1.5 -ClubSpeed 108
#   .\tools\send-openconnect-shot.ps1 -ReadyOnly                       # just the "ready, ball detected" signal
#
# Signs as GSPro uses them: Direction (HLA) and Axis positive = right; a positive axis curves the ball right.
param(
    [double]$Speed = 120, [double]$Launch = 16.5, [double]$Direction = 0, [double]$Spin = 7000, [double]$Axis = 0,
    [double]$ClubSpeed = 0, [int]$ShotNumber = 1, [string]$HostName = "127.0.0.1", [int]$Port = 921, [switch]$ReadyOnly
)
$ErrorActionPreference = "Stop"
$inv = [Globalization.CultureInfo]::InvariantCulture

if ($ReadyOnly) {
    $json = '{"DeviceID":"Test script","Units":"Yards","ShotNumber":0,"APIversion":"1",' +
            '"ShotDataOptions":{"ContainsBallData":false,"ContainsClubData":false,"LaunchMonitorIsReady":true,' +
            '"LaunchMonitorBallDetected":true,"IsHeartBeat":true}}'
} else {
    $club = if ($ClubSpeed -gt 0) { [string]::Format($inv, ',"ClubData":{{"Speed":{0}}}', $ClubSpeed) } else { "" }
    $json = [string]::Format($inv,
        '{{"DeviceID":"Test script","Units":"Yards","ShotNumber":{0},"APIversion":"1",' +
        '"BallData":{{"Speed":{1},"SpinAxis":{2},"TotalSpin":{3},"HLA":{4},"VLA":{5}}}{6},' +
        '"ShotDataOptions":{{"ContainsBallData":true,"ContainsClubData":{7}}}}}',
        $ShotNumber, $Speed, $Axis, $Spin, $Direction, $Launch, $club, $(if ($ClubSpeed -gt 0) { "true" } else { "false" }))
}

$tcp = [Net.Sockets.TcpClient]::new()
try {
    $tcp.Connect($HostName, $Port)
    $stream = $tcp.GetStream()
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $stream.Write($bytes, 0, $bytes.Length)
    Write-Host "Sent: $json"
    # Replies: 201 (the player up) when connecting, 200 for a shot taken, 501 for one turned down.
    $stream.ReadTimeout = 2000
    $buffer = New-Object byte[] 4096
    try {
        while ($true) {
            $n = $stream.Read($buffer, 0, $buffer.Length)
            if ($n -le 0) { break }
            Write-Host "Reply: $([Text.Encoding]::UTF8.GetString($buffer, 0, $n))"
            if ($ReadyOnly) { continue }
        }
    } catch [System.IO.IOException] { }   # no more replies within the timeout
} finally { $tcp.Close() }
