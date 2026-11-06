$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" run --project "c:\dev\SquareConnector\Source\SquareGsproConnector\src\GrimesGolf.Relay" --no-launch-profile