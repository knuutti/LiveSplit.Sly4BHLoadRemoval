@echo off
REM Builds and runs the offline detection tests against testdata\. No LiveSplit involved: the runner
REM compiles the detection sources directly, so a threshold change can be checked without deploying
REM the DLL. Pass --verbose for per-frame output, --dump to write failing frames to testdata\_dump,
REM --measure to print the range each measured quantity takes over each labelled class.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set SOURCES=LoadDetector.cs FeatureDetector.cs MaskDetector.cs
cd /d "%~dp0"

"%CSC%" /nologo /out:tests\DetectionTests.exe tests\DetectionTests.cs %SOURCES% /r:System.Drawing.dll
if errorlevel 1 exit /b 1

"%CSC%" /nologo /out:tests\GeometryTests.exe tests\GeometryTests.cs %SOURCES% /r:System.Drawing.dll
if errorlevel 1 exit /b 1

"%CSC%" /nologo /out:tests\GateScenarios.exe tests\GateScenarios.cs %SOURCES% /r:System.Drawing.dll
if errorlevel 1 exit /b 1

tests\GeometryTests.exe
if errorlevel 1 exit /b 1

tests\GateScenarios.exe
if errorlevel 1 exit /b 1

tests\DetectionTests.exe testdata %*
