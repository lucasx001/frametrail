using System.Text.Json;
using FrameTrail.Workspace;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private async Task RunProjectLibraryValidationAsync(string directory, string fixture)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        bool success = false; string? failure = null; var checks = new List<string>();
        try {
            string first = Path.Combine(directory, "项目 A.frametrailproject"), second = Path.Combine(directory, "项目 B.frametrailproject");
            await using (var source = await ProjectWorkspace.OpenAsync(Path.GetFullPath(fixture))) {
                var editor = new EditorSession(source);
                await editor.CopySourcesAsync(first, CancellationToken.None); await editor.CopySourcesAsync(second, CancellationToken.None);
            }
            string broken = Directory.CreateDirectory(Path.Combine(directory, "损坏项目.frametrailproject")).FullName;
            projectCatalog = new ProjectCatalog(Path.Combine(directory, "preferences", "catalog.json"));
            async Task<ProjectLibraryWindow> LibraryAsync() { ShowProjectLibrary([directory]); var window = projectLibraryWindow!; await window.Ready.WaitAsync(TimeSpan.FromSeconds(15)); return window; }
            async Task WaitForAsync(Func<bool> complete) {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!complete() && DateTime.UtcNow < deadline) await Task.Delay(30);
                if (!complete()) throw new IOException("Project list action did not complete.");
            }
            var library = await LibraryAsync(); await library.ValidateListAsync(directory, checks);
            await library.ValidateDeletionAsync(first, false, Path.Combine(directory, "projects-delete-confirm.png"));
            if (!Directory.Exists(first) || library.ProjectCount != 3 || editorWindow is not null) throw new IOException("Cancel changed the project or opened an editor.");
            library.InvokeOpen(second); await WaitForAsync(() => projectLibraryWindow is null && opened?.DirectoryPath == second && editorWindow is not null);
            var original = editorWindow!; uint editedWidth = original.StageProjectValidationEdit();
            library = await LibraryAsync(); library.InvokeOpen(first);
            await WaitForAsync(() => projectLibraryWindow is null && opened?.DirectoryPath == first && editorWindow is not null);
            await using (var saved = await ProjectWorkspace.OpenAsync(second))
                if (saved.Snapshot!.Canvas.Width != editedWidth) throw new IOException("List project switching lost the pending inspector edit.");
            if (!original.HasClosed) throw new IOException("List project switching did not close the old editor.");
            checks.Add("Clicking a row opens its real editor; switching through the list saves pending edits and releases the prior writable lease. Cancelling deletion preserves the project and does not open it.");
            library = await LibraryAsync();
            await using (var lease = await ProjectWorkspace.OpenAsync(second)) {
                await library.ValidateDeletionAsync(second, true);
                if (!Directory.Exists(second) || library.ProjectCount != 3 || !library.HasError) throw new IOException("Externally locked project was removed or failed silently.");
            }
            await library.ValidateDeletionAsync(second, true);
            if (Directory.Exists(second) || library.ProjectCount != 2 || opened?.DirectoryPath != first) throw new IOException("Inactive project deletion affected the active editor or did not refresh the list.");
            await library.ValidateDeletionAsync(broken, true);
            if (Directory.Exists(broken) || library.ProjectCount != 1) throw new IOException("Broken project could not be removed from the list.");
            await library.ValidateDeletionAsync(first, true);
            if (Directory.Exists(first) || editorWindow is not null || opened is not null || library.ProjectCount != 0) throw new IOException("Active project deletion did not release resources or refresh the empty list.");
            checks.Add("Confirmed deletion recycles the selected inactive, broken and active disposable projects and refreshes the list. External writer leases are rejected with a visible error, and the active editor is retained.");
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        finally {
            closing = true; projectLibraryWindow?.Close(); projectLibraryWindow = null;
            try { if (editorWindow is { } editor) { await editor.RequestCloseAsync(); editorWindow = null; } await coordinator.DisposeAsync(); if (opened is not null) await opened.DisposeAsync(); }
            catch (Exception error) { success = false; failure += "\nCleanup: " + error; }
            await File.WriteAllTextAsync(Path.Combine(directory, "project-library-validation.json"), JsonSerializer.Serialize(new { success, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
            allowClose = true; UnregisterHotkeys(); session?.Dispose(); Environment.ExitCode = success ? 0 : 1; Close(); Microsoft.UI.Xaml.Application.Current.Exit();
        }
    }
}
