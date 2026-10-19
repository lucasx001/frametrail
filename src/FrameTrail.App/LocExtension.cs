using Microsoft.UI.Xaml.Markup;

namespace FrameTrail.App;

[MarkupExtensionReturnType(ReturnType=typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; }="";
    protected override object ProvideValue()=>FrameTrail.Domain.AppText.T(Key);
}
