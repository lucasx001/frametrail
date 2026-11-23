using FrameTrail.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private ProjectCatalog projectCatalog = new();
    private ProjectLibraryWindow? projectLibraryWindow;
    private NewProjectWindow? newProjectWindow;
    private void ShowProjectLibrary()
    {
        try { ShowProjectLibrary([projectLocations.OpenDirectory(), projectLocations.OpenDirectory((opened ?? coordinator.Workspace)?.DirectoryPath)]); }
        catch (Exception error) {
            if (editorWindow is { } editor) editor.ReportProjectError(L.T("S2C98114B60E1"), error.Message);
            else ShowFeedback(L.T("S2C98114B60E1"), error.Message);
        }
    }
    private void ShowProjectLibrary(IEnumerable<string> folders)
    {
        if (projectLibraryWindow is not null) { projectLibraryWindow.Activate(); return; }
        if (busy || closing || coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) return;
        HideFlyouts();
        string? current = (opened ?? coordinator.Workspace)?.DirectoryPath;
        var window = new ProjectLibraryWindow(projectCatalog, folders, current,
            path => RunLibraryActionAsync(async () => {
                if (editorWindow is { } editor) await editor.SwitchSelectedProjectAsync(path);
                else await OpenExistingProjectAsync(path);
            }),
            path => RunLibraryActionAsync(async () => {
                if (editorWindow is { } editor && string.Equals((opened ?? coordinator.Workspace)?.DirectoryPath, path, StringComparison.OrdinalIgnoreCase))
                    await editor.DeleteCurrentProjectAsync();
                else await DeleteExistingProjectAsync(path);
            }),
            createNew: () => DispatcherQueue.TryEnqueue(ShowNewProjectWindow),
            createFromVideos: paths => RunLibraryActionAsync(() => CreateVideoProjectAsync(paths)));
        projectLibraryWindow = window;
        window.Closed += (_, _) => {
            projectLibraryWindow = null;
            if (closing) return;
            if (newProjectWindow is not null) return;
            if (editorWindow is { } editor) editor.Activate(); else { AppWindow.Show(); Activate(); }
        };
        window.Activate(); if (editorWindow is null) AppWindow.Hide();
    }
    private void ShowNewProjectWindow()
    {
        if (newProjectWindow is not null) { newProjectWindow.Activate(); return; }
        if (busy || closing || coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete)) return;
        HideFlyouts();
        string initialFolder = projects;
        var window = new NewProjectWindow(
            initialFolder,
            (paths, name, folder) => RunLibraryActionAsync(() => CreateVideoProjectAsync(paths, name, folder)),
            pickedFolder =>
            {
                RememberCatalogFolder(projects);
                projectLocations.SetRecordingDirectory(pickedFolder);
                RememberCatalogFolder(pickedFolder);
                projects = pickedFolder;
                DestinationName.Text = Path.GetFileName(pickedFolder);
                ToolTipService.SetToolTip(DestinationButton, projects);
            });
        newProjectWindow = window;
        window.Closed += (_, _) =>
        {
            newProjectWindow = null;
            if (closing) return;
            if (editorWindow is { } editor) editor.Activate();
            else if (projectLibraryWindow is null) { AppWindow.Show(); Activate(); }
        };
        window.Activate();
        if (editorWindow is null) AppWindow.Hide();
    }

    private async Task CreateVideoProjectAsync(IReadOnlyList<string> videoPaths, string? requestedName = null, string? targetFolder = null)
    {
        if (coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete))
            throw new InvalidOperationException(L.T("S0CAF0D47B58B"));
        if (videoPaths is null || videoPaths.Count == 0)
            throw new InvalidOperationException(L.T("NewProjectUnsupportedFiles"));

        string folder = string.IsNullOrWhiteSpace(targetFolder) ? projects : targetFolder;
        Directory.CreateDirectory(folder);
        string projectPath = ProjectWorkspace.ResolveUniqueProjectPath(folder, requestedName, videoPaths[0]);
        ProjectWorkspace? candidate = await ProjectWorkspace.CreateFromVideoClipsAsync(
            projectPath,
            videoPaths,
            presetLibrary: () => new VisualPresetLibrary());

        var current = editorWindow;
        EditorWindow? next = null;
        try
        {
            next = CreateEditor(candidate);
            next.SetProjectTransition(true);
            if (current is not null)
                next.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(current.AppWindow.Position.X, current.AppWindow.Position.Y, current.AppWindow.Size.Width, current.AppWindow.Size.Height));
            next.Activate();
            if (!await next.Startup.WaitAsync(TimeSpan.FromSeconds(30)))
                throw new IOException(L.T("S3A00B1E15B6E"));
            editorWindow = next;
            if (current is not null)
            {
                await current.RequestCloseAsync();
                if (!current.HasClosed) throw new IOException(L.T("S2BE3D82D2928"));
            }
            await ResetWorkspaceAsync();
            opened = candidate;
            candidate = null;
            RememberCatalogFolder(folder);
            next.SetProjectTransition(false);
            AppWindow.Hide();
            next.Activate();
        }
        catch
        {
            editorWindow = current;
            if (next is not null) await next.DiscardWindowAsync();
            if (candidate is not null)
            {
                string failedDir = candidate.DirectoryPath;
                await candidate.DisposeAsync();
                candidate = null;
                try { if (Directory.Exists(failedDir)) Directory.Delete(failedDir, recursive: true); } catch { }
            }
            if (current is not null) current.Activate();
            else if (projectLibraryWindow is null && newProjectWindow is null) { AppWindow.Show(); Activate(); }
            throw;
        }
        finally { if (candidate is not null) await candidate.DisposeAsync(); }
    }

    private void RecorderRoot_DragOver(object sender, DragEventArgs e)
    {
        bool canDrop = !busy && !closing &&
            coordinator.State is RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete &&
            e.DataView.Contains(StandardDataFormats.StorageItems);
        if (!canDrop)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
            e.DragUIOverride.Caption = L.T("DropVideoCreateProjectPrompt");
        RecorderRoot.BorderBrush = RecorderTheme.Brush(RecorderRoot, "EditorAccent");
        e.Handled = true;
    }

    private void RecorderRoot_DragLeave(object sender, DragEventArgs e)
    {
        RecorderRoot.BorderBrush = RecorderTheme.Brush(RecorderRoot, "RecorderEdge");
    }

    private async void RecorderRoot_Drop(object sender, DragEventArgs e)
    {
        RecorderRoot.BorderBrush = RecorderTheme.Brush(RecorderRoot, "RecorderEdge");
        if (busy || closing ||
            coordinator.State is not (RecordingWorkspaceState.Setup or RecordingWorkspaceState.RecordingComplete) ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.Handled = true;

        var deferral = e.GetDeferral();
        string[] paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items
                .OfType<StorageFile>()
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p) && ProjectWorkspace.IsSupportedImportVideoExtension(Path.GetExtension(p)))
                .ToArray();
        }
        catch (Exception error)
        {
            ShowFeedback(L.T("NewProjectCreateFailed"), error.Message);
            return;
        }
        finally
        {
            deferral.Complete();
        }

        if (paths.Length == 0)
        {
            ShowFeedback(L.T("NewProjectCreateFailed"), L.T("NewProjectUnsupportedFiles"));
            return;
        }

        HideFlyouts();
        await ExecuteAsync(() => CreateVideoProjectAsync(paths));
    }
    private async Task RunLibraryActionAsync(Func<Task> action)
    {
        if (busy || closing) throw new InvalidOperationException(L.T("SAC9B8D0E046B"));
        busy = true; ApplyInteraction();
        try { await action(); }
        finally { busy = false; ApplyInteraction(); }
    }
    private void RememberCatalogProject(string path)
    {
        if (Environment.GetCommandLineArgs().Contains("--inspect-ui")) return;
        try { projectCatalog.RememberProject(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SafeDiagnostics.Record(error); }
    }
    private void RememberCatalogFolder(string path)
    {
        if (Environment.GetCommandLineArgs().Contains("--inspect-ui")) return;
        try { projectCatalog.RememberFolder(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SafeDiagnostics.Record(error); }
    }
}
