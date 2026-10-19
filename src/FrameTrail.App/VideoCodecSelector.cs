using FrameTrail.Domain;
using FrameTrail.Interop;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

internal static class VideoCodecSelector
{
    internal static string Selected(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? VideoCodecs.H264;
    internal static void Populate(ComboBox picker, IReadOnlyList<VideoCodecCapabilities> capabilities, bool recording, string selected)
    {
        picker.Items.Clear();
        foreach (var item in capabilities) picker.Items.Add(new ComboBoxItem {
            Tag = item.Codec, Content = VideoCodecs.DisplayName(item.Codec) + " · " + (recording || !item.CanEncode ? item.Availability : item.HardwareCount > 0 ? L.T("SBA36B9D71F16") : L.T("SBB7C9EB35DA1")),
            IsEnabled = recording ? item.CanRecord : item.CanEncode
        });
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected && i.IsEnabled)
            ?? picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.IsEnabled);
    }
}
