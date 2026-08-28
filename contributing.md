## Pull Requests

Pull requests for DS4Windows are welcome. Before making a pull request, please
test your changes to ensure that the changes made do not negatively affect
the performance of other parts of the application. Some consideration will
be made during code review to try to tweak the changes in order to improve
application performance. However, there is a chance that a pull request will be
rejected if no reasonable solution can be found to incorporate code changes.

## Before opening a pull request

DS4Windows targets .NET 8 on Windows. Please build both platforms and run the tests
locally first. These are the same steps CI runs (`.github/workflows/ci-build.yml`):

    dotnet restore
    dotnet test .\DS4WindowsTests\DS4WindowsTests.csproj -c Release -p:Platform=x64
    dotnet publish .\DS4Windows\DS4WinWPF.csproj -c Release /p:platform=x64 -o .\bin\x64\Release\output
    dotnet publish .\DS4Windows\DS4WinWPF.csproj -c Release /p:platform=x86 -o .\bin\x86\Release\output

Both x64 and x86 must build and the tests should pass. After you open the PR, check
that the CI build is green.

> Three serialization "snapshot" tests (`CheckSettingsSave`, `CheckWriteProfile`,
> `CheckJaysProfileRead`) currently fail on a clean checkout: their expected-XML fixtures
> predate fields the serializer now emits. CI skips them with `--filter`, so you can
> ignore those three until the fixtures are regenerated.