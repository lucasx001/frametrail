using System.Text.Json;
using FrameTrail.Domain;
using FrameTrail.Workspace;
using Microsoft.UI.Xaml;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private async Task RunProjectOpenValidationAsync(string directory, string fixture)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        bool success = false; string? failure = null; var checks = new List<string>();
        try
        {
            var first = Path.Combine(directory, "项目 A.frametrailproject");
            var second = Path.Combine(directory, "项目 B.frametrailproject");
            await using (var source = await ProjectWorkspace.OpenAsync(Path.GetFullPath(fixture)))
            {
                var session = new EditorSession(source);
                await session.CopySourcesAsync(first, CancellationToken.None); await session.CopySourcesAsync(second, CancellationToken.None);
            }
            var locations = new ProjectLocations(Path.Combine(directory, "preferences", "location.json"), Path.Combine(directory, "default"));
            locations.SetRecordingDirectory(directory);
            using (var picker = new ProjectFolderPicker(new ProjectLocations(Path.Combine(directory, "preferences", "location.json")).OpenDirectory()))
                if (!string.Equals(picker.InitialDirectory, directory, StringComparison.OrdinalIgnoreCase)) throw new IOException("Native dialog ignored configured project storage.");
            checks.Add("A native folder dialog reports the configured project directory after preferences reload; SetFolder overrides unrelated picker history.");
            await OpenExistingProjectAsync(first);
            var original = editorWindow!;
            if (opened?.DirectoryPath != first || !original.ProjectSwitchAvailable) throw new IOException("Capsule open did not load an editor with project switching.");
            await original.ValidateDeleteCancellationAsync(directory, checks);
            await original.ValidateExportCodecsAsync(directory, checks);
            await original.SaveProjectValidationFrameAsync(Path.Combine(directory, "project-a.png"));
            await original.ValidateLiveProjectEditsAsync(directory, checks);
            uint expectedWidth = original.StageProjectValidationEdit();
            await original.SwitchSelectedProjectAsync(second);
            if (!original.HasClosed || ReferenceEquals(original, editorWindow) || opened?.DirectoryPath != second) throw new IOException("Editor did not replace the active project.");
            await using (var saved = await ProjectWorkspace.OpenAsync(first))
                if (saved.Snapshot!.Canvas.Width != expectedWidth) throw new IOException("Pending number edit was lost while switching.");
            checks.Add("Capsule opens a local project, editor switches A to B, pending inspector edits are saved and the old writable lease is released.");
            var current = editorWindow!;
            await current.SwitchSelectedProjectAsync(second);
            if (!ReferenceEquals(current, editorWindow)) throw new IOException("Selecting the same project created a second editor.");
            var invalid = Directory.CreateDirectory(Path.Combine(directory, "invalid.frametrailproject")).FullName;
            try { await current.SwitchSelectedProjectAsync(invalid); throw new InvalidOperationException("Invalid folder was accepted."); } catch (IOException) { }
            await using (var locked = await ProjectWorkspace.OpenAsync(first))
                try { await current.SwitchSelectedProjectAsync(first); throw new InvalidOperationException("Locked project was accepted."); } catch (IOException) { }
            if (!ReferenceEquals(current, editorWindow) || opened?.DirectoryPath != second) throw new IOException("Failed open replaced the current project.");
            checks.Add("Same-project selection is a no-op; invalid and externally locked projects retain the current editor.");
            uint retryWidth;
            using (var lockedSave = new FileStream(Path.Combine(second, "project.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                retryWidth = current.StageProjectValidationEdit();
                try { await current.SwitchSelectedProjectAsync(first); throw new InvalidOperationException("Failed save allowed a project switch."); } catch (IOException) { }
                try { await current.DeleteCurrentProjectAsync(); throw new InvalidOperationException("Failed save allowed project deletion."); } catch (IOException) { }
                if (!ReferenceEquals(current, editorWindow) || current.HasClosed) throw new IOException("Failed save closed the current project.");
            }
            await current.SwitchSelectedProjectAsync(first);
            await using (var saved = await ProjectWorkspace.OpenAsync(second))
                if (saved.Snapshot!.Canvas.Width != retryWidth) throw new IOException("Retry did not preserve unsaved edits.");
            await editorWindow!.SaveProjectValidationFrameAsync(Path.Combine(directory, "project-a-return.png"));
            checks.Add("Save failure prevents switching and keeps edits in memory; retry saves them and reopens A with a working native preview.");
            using (var picker = new ProjectFolderPicker(locations.OpenDirectory(first)))
                if (!string.Equals(picker.InitialDirectory, directory, StringComparison.OrdinalIgnoreCase)) throw new IOException("Editor picker did not start at current project's parent.");
            checks.Add("Editor folder dialog starts in the current project's parent directory.");
            await using (var locked = await ProjectWorkspace.OpenAsync(second))
                try { await DeleteExistingProjectAsync(second); throw new InvalidOperationException("Locked project was deleted."); } catch (IOException) { }
            if (!Directory.Exists(second)) throw new IOException("Locked inactive project was removed.");
            await editorWindow.ValidateConfirmedDeletionAsync(checks);
            if (Directory.Exists(first) || editorWindow is not null || opened is not null || !AppWindow.IsVisible || SetupPanel.Visibility != Visibility.Visible) throw new IOException("Active project deletion did not release resources and return to the recorder.");
            await Task.Delay(100); await SaveUiFrameAsync(RecorderRoot, Path.Combine(directory, "delete-return-capsule.png"));
            await DeleteExistingProjectAsync(second);
            if (Directory.Exists(second) || !Directory.Exists(invalid)) throw new IOException("Inactive deletion affected the wrong folder.");
            var damaged = Directory.CreateDirectory(Path.Combine(directory, "damaged.frametrailproject")).FullName;
            await File.WriteAllTextAsync(Path.Combine(damaged, "project.json"), "damaged project");
            await DeleteExistingProjectAsync(damaged);
            if (Directory.Exists(damaged) || !Directory.Exists(invalid)) throw new IOException("Damaged-project cleanup affected an unrelated folder.");
            checks.Add("Locked and unsaved projects cannot be deleted; active project deletion closes preview/lease and returns to recorder; Windows recycle succeeds for active/inactive disposable fixtures without removing their parent or sibling.");
            success = true;
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            try {
                if (editorWindow is not null) { await editorWindow.RequestCloseAsync(); editorWindow = null; }
                await coordinator.DisposeAsync(); if (opened is not null) { await opened.DisposeAsync(); opened = null; }
            } catch (Exception error) { success = false; failure += "\nCleanup: " + error; }
            await File.WriteAllTextAsync(Path.Combine(directory, "project-open-validation.json"), JsonSerializer.Serialize(new { success, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
            allowClose = true; closing = true; UnregisterHotkeys(); session?.Dispose(); Environment.ExitCode = success ? 0 : 1; Close(); Application.Current.Exit();
        }
    }
}
