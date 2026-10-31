#:property TargetFramework=net10.0
#:property PublishAot=false
#:project src/ReleaseTools/ReleaseTools.csproj

using PipelineTools.ReleaseTools;

return await ReleaseToolsProgram.RunAsync(args);