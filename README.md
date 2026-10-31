# Release Management Tools

A .NET 10 command-line application for release ticket management, Jira and Azure DevOps integration, release notes, repository validation, and semantic version tags. The source project and its unit tests live in this repository; Azure Pipelines templates consume the versioned Linux package published by GitHub Actions.

## Build and Test

```powershell
dotnet build src/ReleaseTools/ReleaseTools.csproj
dotnet test tests/ReleaseTools.Tests/ReleaseTools.Tests.csproj
```

## Run Locally

Run a command through the root-level file launcher or the project:

```powershell
dotnet run --file .\ReleaseProcess.cs -- --help
```

Commands that call Jira or Azure DevOps require the corresponding credentials and pipeline environment variables. Use `--help` to list available commands.

## Release Package

Push a version tag such as `v1.0.0` to run the GitHub Actions workflow. The workflow runs unit tests and attaches `release-management-tools-linux-x64.tar.gz` to the GitHub Release. The package is a self-contained Linux x64 executable and does not require the .NET runtime on the consuming agent.

The release-process templates in the `release-process-azure-pipelines` repository include `templates/install-release-tools.yaml` to download this package, then invoke it directly. The default is `v1.0.0`; set `RELEASE_TOOLS_VERSION` to select another version. The root-level `ReleaseProcess.cs` launcher in this repository is for local development.