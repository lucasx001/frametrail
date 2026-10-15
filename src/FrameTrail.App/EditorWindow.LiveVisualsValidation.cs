using System.Security.Cryptography;
using FrameTrail.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FrameTrail.App;

public sealed partial class EditorWindow
{
    private async Task ValidateLiveVisualsAsync(string directory, List<string> checks)
    {
        selectedEffect = editor.Snapshot.Effects.First(e => e.Kind == "zoom").Id; RefreshVisualInspector();
        await Task.Delay(100); await FlushVisualChangesAsync();
        await SeekAsync(15_000_000);
        await preview.RenderAsync(position, true, Path.Combine(directory, "live-before.png"));
        var before = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }); var revision = editor.Snapshot.Revision;
        var session = preview.Session; var presentations = visualPreviewCount;
        position = 0; // Editing a dormant effect should locate a visible part of it.
        for (int i = 0; i < 10; i++) { effectScale.Value = 2 + i * 0.05; await Task.Delay(65); }
        await visualPreviewWork;
        if (visualPreviewCount - presentations < 3 || editor.Snapshot.Revision != revision || !ReferenceEquals(session, preview.Session) || position < 10_000_000)
            throw new InvalidOperationException("Live values did not render before commit, rebuilt media, or lost their preview interval.");
        await preview.RenderAsync(position, true, Path.Combine(directory, "live-during.png"));
        if (SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "live-before.png"))).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "live-during.png"))))) throw new InvalidOperationException("Live parameter changes did not affect pixels.");
        await FlushVisualChangesAsync();
        if (editor.Snapshot.Revision != revision + 1 || !ReferenceEquals(session, preview.Session)) throw new InvalidOperationException("One gesture was not one commit or replaced the surface.");
        var after = ProjectJson.Serialize(editor.Snapshot with { Revision = 0 });
        await PerformAsync(editor.UndoAsync);
        if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != before) throw new InvalidOperationException("One undo failed to restore the live gesture.");
        await PerformAsync(editor.RedoAsync);
        if (ProjectJson.Serialize(editor.Snapshot with { Revision = 0 }) != after) throw new InvalidOperationException("Live redo failed.");
        checks.Add("Ten successive changes render visible pixels before commit on the same media session; one flush/undo/redo covers the entire gesture.");

        var cancelled = ProjectJson.Serialize(editor.Snapshot); effectScale.Value = 2.9; await Task.Delay(150); await visualPreviewWork;
        visualCommitTimer!.Stop(); await CancelVisualChangesAsync();
        if (ProjectJson.Serialize(editor.Snapshot) != cancelled || editor.Preview is not null) throw new InvalidOperationException("Cancelled live gesture changed the project.");
        checks.Add("Cancelled live preview restores the accepted effect without adding a revision.");
        position = 0; effectScale.Value = 2.7; await FlushVisualChangesAsync();
        if (position < 10_000_000) throw new InvalidOperationException("Fast edit failed to locate its effect before rendering.");
        await PerformAsync(editor.UndoAsync);

        effectStart.StartBringIntoView(); effectStart.Focus(FocusState.Keyboard); effectStart.Text = "-";
        await Task.Delay(100); await visualPreviewWork;
        if (!ReferenceEquals(InputAncestor<NumberBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject), effectStart)) throw new InvalidOperationException("Partial number lost focus.");
        effectStart.Text = "0.7"; await Task.Delay(160); await visualPreviewWork;
        await FlushVisualChangesAsync();
        if (!ReferenceEquals(InputAncestor<NumberBox>(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject), effectStart) || Math.Abs(editor.Snapshot.Effects.First(e => e.Id == selectedEffect).SourceStart.Ticks / 10_000_000.0 - 0.7) > 0.000001)
            throw new InvalidOperationException("Numeric live preview failed or commit stole input focus.");
        effectPicker.Focus(FocusState.Programmatic);
        await PerformAsync(editor.UndoAsync);
        checks.Add("Partial numeric input remains editable; valid text previews and saves while preserving focus, with no Apply button.");
    }
}
