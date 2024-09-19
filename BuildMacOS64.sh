mkdir Build
dotnet publish -p:PublishSingleFile=true --self-contained true
cp -f "./steam-dl/bin/Release/net8.0/osx-x64/publish/steam-dl" "./Build/steam-dl"
