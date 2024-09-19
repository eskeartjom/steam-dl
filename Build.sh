mkdir Build
dotnet publish -p:PublishSingleFile=true --self-contained true
cp -f ".\steam-dl\bin\Release\net8.0\win-x64\publish\steam-dl.exe" ".\Build\steam-dl.exe"
