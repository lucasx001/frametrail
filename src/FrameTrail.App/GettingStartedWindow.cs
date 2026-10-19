using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FrameTrail.App;

internal sealed partial class GettingStartedWindow : Window
{
    private readonly Grid root = new() { Padding = new Thickness(28), RowSpacing = 20 };
    private readonly Button start = new() { Content = L.T("S715EE9DC842F"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120 };
    private readonly TaskCompletionSource<bool> dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool ownerClosed;

    internal Task<bool> Dismissed => dismissed.Task;
    internal Task Completion { get; set; } = Task.CompletedTask;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    internal GettingStartedWindow(Window owner)
    {
        Title = L.T("S7C8F61A68034"); AppIcon.Apply(this);
        root.RequestedTheme = EditorAppearance.Current;
        root.Background = RecorderTheme.Brush(root, "EditorPanel");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = Title, FontSize = 26, TextWrapping = TextWrapping.Wrap });
        var panel = new StackPanel { Spacing = 14 };
        foreach (var text in new[] { L.T("S4BD219F9473E"), L.T("SDDBD8AE3FBCC"), L.T("S81E3E3F470FC"), L.T("S149E5E61DCAB") })
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        foreach (var (label, uri) in new[] { (L.T("S672FB69D510D"), "ms-settings:privacy-microphone"), (L.T("SC251C50237F3"), "ms-settings:privacy-webcam"), (L.T("S62A255413ADA"), "ms-settings:sound") })
            panel.Children.Add(new HyperlinkButton { Content = label, NavigateUri = new Uri(uri) });
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        AutomationProperties.SetAutomationId(start, "onboarding.start");
        start.Click += (_, _) => Close();
        Grid.SetRow(start, 2); root.Children.Add(start); Content = root;
        root.Loaded += (_, _) => start.Focus(FocusState.Programmatic);
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(); };
        root.KeyboardAccelerators.Add(escape);

        // An owned window stays above the topmost capsule without disabling it.
        // Its standard title bar also provides an independently usable close action.
        SetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(this), -8, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        double scale = RecorderWindowChrome.Scale(owner);
        var work = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)Math.Round(560 * scale), work.Width), Math.Min((int)Math.Round(640 * scale), work.Height)));
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (work.Height - AppWindow.Size.Height) / 2));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = false; presenter.IsMaximizable = false; }
        void OwnerClosed(object sender, WindowEventArgs args) { ownerClosed = true; Close(); }
        owner.Closed += OwnerClosed;
        Closed += (_, _) => {
            owner.Closed -= OwnerClosed;
            dismissed.TrySetResult(!ownerClosed);
            if (!ownerClosed) owner.Activate();
        };
    }
}
