using System.Text.Json;
using FrameTrail.Domain;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private async Task RunP2LibraryValidationAsync(string directory,string fixture)
    {
        Directory.CreateDirectory(directory);bool success=false;string? failure=null;object? measurements=null;
        try {
            var snapshot=await File.ReadAllTextAsync(Path.Combine(fixture,"project.json"));
            for(int i=0;i<1000;i++){var path=Directory.CreateDirectory(Path.Combine(directory,"Project"+i.ToString("0000")+".frametrailproject")).FullName;await File.WriteAllTextAsync(Path.Combine(path,"project.json"),snapshot);}
            projectCatalog=new ProjectCatalog(Path.Combine(directory,"preferences","catalog.json"));ShowProjectLibrary([directory]);
            measurements=await projectLibraryWindow!.ValidateStressAsync(directory);success=true;
        }catch(Exception error){failure=error.ToString();}
        finally{projectLibraryWindow?.Close();await File.WriteAllTextAsync(Path.Combine(directory,"p2-library.json"),JsonSerializer.Serialize(new{success,measurements,failure},new JsonSerializerOptions{WriteIndented=true}));Environment.ExitCode=success?0:1;closing=true;allowClose=true;await coordinator.DisposeAsync();session?.Dispose();Close();}
    }
}
