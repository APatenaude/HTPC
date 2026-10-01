#Requires -Version 5.1
# Job verb: input-task. Makes the \HTPC\Input task (lib\Register-InputTask.ps1: the launcher's elevated
# input helper, for windows that run as administrator) for the TV user, when the launcher finds it
# missing: a box set up or updated before it existed. SYSTEM only; it takes no argument, the user is
# the one the installed launcher or watchdog runs as (Get-ConsoleUser).
param([string]$Arg)

if (-not $script:IsSystem) { throw 'input-task runs only through the elevated task' }
if ($Arg) { throw 'Refused: input-task takes no argument' }
$exe = Join-Path $env:ProgramFiles 'HTPC\Launcher\HtpcLauncher.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "No launcher at $exe" }
$user = Get-ConsoleUser
if (-not $user) { throw 'No TV user is signed in to make the task for' }
$name = (New-Object Security.Principal.SecurityIdentifier($user.Sid)).Translate([Security.Principal.NTAccount]).Value
& "$JobLib\Register-InputTask.ps1" -User $name -Exe $exe
