using FrameTrail.Workspace;

namespace FrameTrail.App;

public sealed partial class MainWindow
{
    private ProjectCatalog projectCatalog = new();
    private ProjectLibraryWindow? projectLibraryWindow;
    private void ShowProjectLibrary()
    {
        try { ShowProjectLibrary([projectLocations.OpenDirectory(), projectLocations.OpenDirectory((opened ?? coordinator.Workspace)?.DirectoryPath)]); }
        catch (Exception error) {
            if (editorWindow is { } editor) editor.ReportProjectError("无法打开项目列表", error.Message);
            else ShowFeedback("无法打开项目列表", error.Message);
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
            }));
        projectLibraryWindow = window;
        window.Closed += (_, _) => {
            projectLibraryWindow = null;
            if (closing) return;
            if (editorWindow is { } editor) editor.Activate(); else { AppWindow.Show(); Activate(); }
        };
        window.Activate(); if (editorWindow is null) AppWindow.Hide();
    }
    private async Task RunLibraryActionAsync(Func<Task> action)
    {
        if (busy || closing) throw new InvalidOperationException("请等待当前操作完成，再打开或删除项目。");
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
