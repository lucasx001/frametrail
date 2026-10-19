using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private bool previewFullscreen, fullscreenTransition;
    private AppWindowPresenter? previewWindowPresenter;
    private OverlappedPresenterState previewWindowState;
    private RectInt32 previewWindowBounds;
    private double previewScrollX, previewScrollY;

    private async void Fullscreen_Click(object sender, RoutedEventArgs e) => await SafeAsync(TogglePreviewFullscreenAsync);
    private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if(e.Key==VirtualKey.F6&&!previewFullscreen&&!working&&!showingExport){e.Handled=true;if(timeline.HasFocus)InspectorTabs.Children.OfType<Microsoft.UI.Xaml.Controls.Control>().FirstOrDefault(c=>c.IsEnabled)?.Focus(FocusState.Keyboard);else timeline.FocusTimeline(FocusState.Keyboard);return;}
        if (e.Key == VirtualKey.Escape && (canvasOverlay.Dragging || cropEditing)) { e.Handled=true;if(canvasOverlay.Dragging){canvasOverlay.Finish(false);await canvasCommitWork;}else await SafeAsync(ToggleCropEditingAsync);return; }
        if (!previewFullscreen) return;
        if (e.Key is VirtualKey.Escape or VirtualKey.F11) {
            e.Handled = true; await SafeAsync(TogglePreviewFullscreenAsync);
        } else if (e.Key is VirtualKey.Space or VirtualKey.Left or VirtualKey.Right) {
            // Handle before the focused button consumes Space as a click.
            e.Handled = true;
            if (working || editor.IsExporting) return;
            await SafeAsync(async () => {
                if (e.Key == VirtualKey.Space) { if (playing) Pause(); else await StartPlaybackAsync(); }
                else await SeekAsync(position + (e.Key == VirtualKey.Left ? -1 : 1) * TimeSpan.TicksPerSecond / editor.Snapshot.Export.Fps.Numerator);
            });
        }
    }
    private async void Preview_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await SafeAsync(TogglePreviewFullscreenAsync);
    }
    private async Task TogglePreviewFullscreenAsync()
    {
        if (!ready || closing || fullscreenTransition || working || switchingProjects || projectTransition || showingExport || showingDeleteConfirmation || editor.IsExporting) return;
        fullscreenTransition = true;
        try {
            if (!previewFullscreen) { await FlushVisualChangesAsync(); await FlushPictureInputsAsync(); }
            if (!closing) SetPreviewFullscreen(!previewFullscreen);
        } finally { fullscreenTransition = false; }
    }
    private void SetPreviewFullscreen(bool fullscreen)
    {
        if (previewFullscreen == fullscreen) return;
        FullscreenTip.IsOpen = false;
        ValidationTrace($"fullscreen {fullscreen}: begin playing={playing} position={position} frames={renderedFrames}");
        if (fullscreen) {
            previewWindowPresenter = AppWindow.Presenter;
            previewWindowState = (previewWindowPresenter as OverlappedPresenter)?.State ?? OverlappedPresenterState.Restored;
            previewWindowBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            previewScrollX = CanvasScroll.HorizontalOffset; previewScrollY = CanvasScroll.VerticalOffset;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        } else {
            if (previewWindowPresenter is not null) AppWindow.SetPresenter(previewWindowPresenter);
            else AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            if (AppWindow.Presenter is OverlappedPresenter restored) {
                if (previewWindowState == OverlappedPresenterState.Maximized) restored.Maximize();
                else { if (restored.State != OverlappedPresenterState.Restored) restored.Restore(); AppWindow.MoveAndResize(previewWindowBounds); }
            }
        }
        previewFullscreen = fullscreen;
        // Reparent the existing view and controls within the same XamlRoot.
        // Decoder, audio clock, position and attached swap chain remain live.
        var from = fullscreen ? PreviewStage : FullscreenHost;
        var to = fullscreen ? FullscreenHost : PreviewStage;
        from.Children.Remove(PreviewContainer); from.Children.Remove(PreviewTransport);
        to.Children.Add(PreviewContainer); to.Children.Add(PreviewTransport);
        PreviewContainer.Margin = fullscreen ? new Thickness(0) : new Thickness(16, 16, 16, 0);
        CanvasScroll.CornerRadius = new CornerRadius(fullscreen ? 0 : 10);
        Grid.SetRow(PreviewTransport, fullscreen ? 0 : 1);
        PreviewTransport.VerticalAlignment = fullscreen ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        PreviewTransport.Margin = fullscreen ? new Thickness(0, 0, 0, 20) : new Thickness(0);
        PreviewEditTools.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        TitleBand.Visibility = EditorBody.Visibility = TimelinePanel.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        FullscreenHost.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        FullscreenGlyph.Kind = fullscreen ? "ExitFullscreen" : "Fullscreen";
        AutomationProperties.SetName(FullscreenButton, fullscreen ? L.T("S0F1505B6AD3F") : L.T("S7C2561288230"));
        FullscreenTip.Content = fullscreen ? L.T("SADBD1679AB0D") : L.T("S4C1DFCF160BC");
        Root.UpdateLayout(); SizePreview(); Root.UpdateLayout();
        CanvasScroll.ChangeView(fullscreen ? 0 : previewScrollX, fullscreen ? 0 : previewScrollY, null, true);
        FullscreenButton.Focus(FocusState.Programmatic);
        ValidationTrace($"fullscreen {fullscreen}: end playing={playing} position={position} frames={renderedFrames}");
    }
}
