@echo off
echo Building release (self-contained, no .NET runtime required)...
dotnet publish FunAiGateway.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o .\publish
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)
echo.
echo Build succeeded! Output: .\publish
echo The exe can run on machines without .NET runtime installed.
pause
