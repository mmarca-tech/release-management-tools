# Release Management Tools

A .NET 10 command-line application for release ticket management, Jira and Azure DevOps integration, release notes, repository validation, and semantic version tags. The source project and its unit tests live in this repository; Azure Pipelines templates consume the versioned Linux package published by GitHub Actions.

## Build and Test

```powershell
dotnet build src/ReleaseTools/ReleaseTools.csproj
dotnet test tests/ReleaseTools.Tests/ReleaseTools.Tests.csproj
```

## Run Locally

Run a command through the project:

```powershell
dotnet run --project src/ReleaseTools/ReleaseTools.csproj -- --help
```

Commands that call Jira or Azure DevOps require the corresponding credentials and pipeline environment variables. Use `--help` to list available commands.

## Release Package

Push a version tag such as `v1.0.0` to run the GitHub Actions workflow. The workflow runs unit tests and attaches `release-management-tools-linux-x64.tar.gz` to the GitHub Release. The package is a self-contained Linux x64 executable and does not require the .NET runtime on the consuming agent.

The release-process templates in the `release-process-azure-pipelines` repository download this package. The pipeline repository's `scripts/run-release-tools.sh` pins the package version and can be overridden with the `RELEASE_TOOLS_VERSION` environment variable.