set THISDIR=%~dp0
pushd %THISDIR%
set pc=..\..\..\..\Bld\drops\Debug\x64\Binaries\pc.exe
if not exist "%pc%" goto :noP

set pt=..\..\..\..\Bld\drops\Debug\x64\Binaries\pt.exe

%pc% /generate:C# /shared ..\CommonUtilities\Timer.p ..\CommonUtilities\TimerHeader.p /t:Timer.4ml /outputDir:..\CommonUtilities\

if NOT errorlevel 0 goto :eof

%pc% /generate:C# /shared ..\SMR\StateMachineReplicationHeader.p ..\SMR\StateMachineReplicationAbs.p /t:SMRAbs.4ml /outputDir:..\SMR\

if NOT errorlevel 0 goto :eof

%pc% /generate:C# /shared .\TwoPhaseCommit.p .\TwoPhaseCommitHeader.p .\TwoPhaseCommitClient.p .\TestDrivers.p .\TwoPhaseCommitSpec.p /r:..\SMR\SMRAbs.4ml /r:..\CommonUtilities\Timer.4ml

if NOT errorlevel 0 goto :eof
%pc% /generate:C# /shared /link TestScripts.p /r:TwoPhaseCommit.4ml /r:..\SMR\SMRAbs.4ml /r:..\CommonUtilities\Timer.4ml

if NOT errorlevel 0 goto :eof


%pt% /psharp Test0.dll

%pt% /psharp Test1.dll

goto :eof
:noP
echo please run ..\..\bld\build debug x64

