using FrameTrail.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

    private bool CanImportClips =>
        ready && !working && !closing && !switchingProjects && !projectTransition &&
        !showingExport && !showingDeleteConfirmation && !editor.IsExporting && !flushingPictureInputs;

    private async void ImportClips_Click(object sender, RoutedEventArgs e)
    {
        if (!CanImportClips) return;
        await SafeAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".m4v");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            int? insertIndex = SelectedClip is ResolvedClip c ? editor.Timeline.Clips.IndexOf(c) + 1 : null;
            await ImportVideoFilesAsync(files.Select(f => f.Path).ToArray(), insertIndex);
        });
    }

    private void PreviewStage_DragOver(object sender, DragEventArgs e)
    {
        if (!CanImportClips || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            PreviewDropOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
            e.DragUIOverride.Caption = L.T("DropVideoAppendPrompt");
        PreviewDropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void PreviewStage_DragLeave(object sender, DragEventArgs e)
    {
        PreviewDropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void PreviewStage_Drop(object sender, DragEventArgs e)
    {
        PreviewDropOverlay.Visibility = Visibility.Collapsed;
        if (!CanImportClips || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        await SafeAsync(async () =>
        {
            var paths = await ExtractVideoPathsAsync(e);
            if (paths.Length > 0)
                await ImportVideoFilesAsync(paths, insertIndex: null);
        });
    }

    private void TimelinePanel_DragOver(object sender, DragEventArgs e)
    {
        if (!CanImportClips || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            timeline.SetDropInsertIndex(null);
            return;
        }
        var pt = e.GetPosition(timeline);
        int idx = timeline.GetDropInsertIndex(pt);
        timeline.SetDropInsertIndex(idx);
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
            e.DragUIOverride.Caption = L.T("DropVideoInsertPrompt");
        e.Handled = true;
    }

    private void TimelinePanel_DragLeave(object sender, DragEventArgs e)
    {
        timeline.SetDropInsertIndex(null);
    }

    private async void TimelinePanel_Drop(object sender, DragEventArgs e)
    {
        timeline.SetDropInsertIndex(null);
        if (!CanImportClips || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        int idx = timeline.GetDropInsertIndex(e.GetPosition(timeline));
        await SafeAsync(async () =>
        {
            var paths = await ExtractVideoPathsAsync(e);
            if (paths.Length > 0)
                await ImportVideoFilesAsync(paths, idx);
        });
    }

    private static async Task<string[]> ExtractVideoPathsAsync(DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            return items
                .OfType<StorageFile>()
                .Select(f => f.Path)
                .Where(IsSupportedVideoPath)
                .ToArray();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool IsSupportedVideoPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedVideoExtensions.Contains(Path.GetExtension(path));

    internal async Task<IReadOnlyList<string>> ImportVideoFilesAsync(IReadOnlyList<string> paths, int? insertIndex = null)
    {
        if (!CanImportClips || paths is null || paths.Count == 0)
            return Array.Empty<string>();
        var validPaths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (validPaths.Length == 0)
            return Array.Empty<string>();

        IReadOnlyList<string> addedIds = Array.Empty<string>();
        await PerformAsync(async () =>
        {
            addedIds = await editor.ImportVideoClipsAsync(validPaths, insertIndex);
        });

        if (addedIds.Count > 0)
        {
            selected = addedIds[0];
            if (editor.Timeline.Clips.FirstOrDefault(c => c.Id == selected) is { } addedClip)
                position = Math.Clamp(addedClip.OutputStart, 0, Math.Max(0, editor.Timeline.Duration - 1));
            await preview.RenderAsync(position);
            LoadInspector();
            RefreshTimeline();
            UpdateClock();
            timeline.RevealIfOutside(position);
            ShowNotice(L.T("ImportVideoClips"), L.T("ImportedVideoClipsNotice", addedIds.Count), InfoBarSeverity.Informational);
        }
        return addedIds;
    }
}
