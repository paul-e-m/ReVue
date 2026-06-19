cd "P:\pCloud Sync\Coding\ReVue\ReVue-dev"
dotnet publish .\ReVue-Judge\ReVue-Judge.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
