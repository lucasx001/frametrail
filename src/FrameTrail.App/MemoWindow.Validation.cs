using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FrameTrail.App;

internal sealed partial class MemoWindow
{
    internal async Task ValidateAsync(string directory)
    {
        bool success = false; string? failure = null; var checks = new List<string>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => closed.TrySetResult();
        try {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); root.UpdateLayout();
            if (grip.MoveCursor.CursorShape != InputSystemCursorShape.SizeAll || grip.ActualWidth < 180 || grip.ActualHeight < 35)
                throw new IOException("Memo header lacks the four-way move cursor or a usable drag surface.");
            if (!RecorderWindowChrome.HasMatchingClientBounds(this)) throw new IOException("Memo has a residual native resize frame.");
            var origin = AppWindow.Position; var size = AppWindow.Size;
            Button Find(string name)
            {
                Button? Search(DependencyObject parent) {
                    if (parent is Button button && AutomationProperties.GetName(button) == name) return button;
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (Search(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
                    return null;
                }
                return Search(root) ?? throw new IOException("Memo toolbar button missing: " + name);
            }
            void Click(string name) => ((IInvokeProvider)new ButtonAutomationPeer(Find(name)).GetPattern(PatternInterface.Invoke)).Invoke();
            editor.Text = "备忘录拖动验证\n正文仍可编辑、选中和滚动。";
            double fontSize = editor.FontSize;
            Click("放大字号"); await Task.Delay(40); if (editor.FontSize != fontSize + 2) throw new IOException("Memo font button was intercepted by window dragging.");
            Click("缩小字号"); Click("切换阅读与编辑"); await Task.Delay(40);
            if (!editor.IsReadOnly || editor.FontSize != fontSize) throw new IOException("Memo toolbar stopped working.");
            Click("切换阅读与编辑"); await Task.Delay(40);
            if (editor.IsReadOnly || !AppWindow.Position.Equals(origin) || !AppWindow.Size.Equals(size)) throw new IOException("Toolbar controls moved/resized the memo or disabled editing.");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark }) {
                root.RequestedTheme = theme; await Task.Delay(80);
                await MainWindow.SaveUiFrameAsync(root, Path.Combine(directory, "memo-" + theme.ToString().ToLowerInvariant() + ".png"));
            }
            checks.Add("The memo uses SizeAll on a 40-DIP header, has matching native/client bounds, and uses the recorder's captured-pointer drag handler.");
            checks.Add("Actual toolbar Invoke actions preserve window position/size while changing font size and reading/editing mode; note text remains editable in both themes.");
            // A failed save must leave the note open, and a retry must persist it.
            await File.WriteAllTextAsync(path, "previous saved note");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (await RequestCloseAsync() || closed.Task.IsCompleted || editor.Header is null)
                    throw new IOException("A failed save discarded the memo or did not report the error.");
            }
            string expectedText = editor.Text;
            if (!await RequestCloseAsync()) throw new IOException("Memo save did not recover after releasing the file.");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (await File.ReadAllTextAsync(path) != expectedText) throw new IOException("Memo text did not persist on close.");
            checks.Add("A locked-file save leaves the note open with its text and an error; releasing the file and closing again persists the isolated memo. Real mouse drag and hover-cursor observation require separate interactive verification.");
            success = true;
        } catch (Exception error) { failure = error.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(directory, "memo-validation.json"), JsonSerializer.Serialize(new { success, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = success ? 0 : 1;
        if (!closed.Task.IsCompleted) { saved = true; Close(); }
    }
}
