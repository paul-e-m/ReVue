cd "P:\pCloud Sync\Coding\ReVue\ReVue-Dev"
dotnet publish .\ReVueVRO.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
