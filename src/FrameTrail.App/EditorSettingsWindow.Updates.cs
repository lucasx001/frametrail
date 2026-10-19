using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Xaml.Controls;

namespace FrameTrail.App;

internal sealed partial class EditorSettingsWindow
{
    private static readonly HttpClient updateHttp=new(){Timeout=TimeSpan.FromSeconds(10)};
    private readonly CancellationTokenSource updateLifetime=new();
    private bool updateClosed;
    private void AddUpdateControls()
    {
        var status=Text(L.T("S278D3A421CDC"),12,true);
        var links=new StackPanel{Spacing=6};var notes=Text("",12,true);
        var check=Button(L.T("S7F68EBAD19BA"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(check,"settings.update.check");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(status,"settings.update.status");
        check.Click+=async(_,_)=>{
            check.IsEnabled=false;links.Children.Clear();notes.Text="";
            try {
                Windows.ApplicationModel.Package? package=null;
                try{package=Windows.ApplicationModel.Package.Current;}catch(InvalidOperationException){}catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x80073D54)){}
                if(package is not null) {
                    var result=await package.CheckUpdateAvailabilityAsync();
                    if(updateClosed)return;
                    status.Text=result.Availability switch {
                        Windows.ApplicationModel.PackageUpdateAvailability.Available or Windows.ApplicationModel.PackageUpdateAvailability.Required=>L.T("S90C127DD09F1"),
                        Windows.ApplicationModel.PackageUpdateAvailability.NoUpdates=>L.T("S4D00E0B3935B"),
                        _=>L.T("SA490D25ED9ED")
                    };
                    if(package.SignatureKind==Windows.ApplicationModel.PackageSignatureKind.Store)
                        AddLink(L.T("S1846AA793FFF"),new Uri("ms-windows-store://pdp/?PFN="+Uri.EscapeDataString(package.Id.FamilyName)));
                } else {
                    var release=await new ReleaseUpdateClient(updateHttp).CheckAsync(updateLifetime.Token);
                    if(updateClosed)return;
                    if(release is null){status.Text=L.T("S0BFAD7EB8D2A");return;}
                    int comparison=ReleaseUpdateClient.Compare(release.Version,typeof(App).Assembly.GetName().Version!);
                    status.Text=comparison>0?L.T("S3075AF319A3B", release.Tag):comparison==0?L.T("SA1AB97D2A801", release.Tag):L.T("S9B2DFE7998D5", release.Tag);
                    notes.Text=release.UsesWebFallback?L.T("UpdateFallback"):release.Notes;AddLink(L.T("SAA0CF1EF4C56"),release.Page);
                    if(release.Installer is not null)AddLink(L.T("SB7893F81D5F0"),release.Installer);
                    if(release.Portable is not null)AddLink(L.T("SC0037523B5EB"),release.Portable);
                }
            }catch(OperationCanceledException){if(!updateClosed)status.Text=L.T("S6FC7EF7729A1");}
             catch(Exception error){if(!updateClosed){SafeDiagnostics.Record(error);status.Text=L.T("S4DDBBF3BA248");AddLink(L.T("SDA736B4BE58B"),new Uri("https://github.com/lucasx001/frametrail/releases/latest"));}}
            finally{if(!updateClosed)check.IsEnabled=true;}
        };
        void AddLink(string label,Uri uri){var button=Button(label);button.Tag=uri;button.Click+=async(_,_)=>{try{await Windows.System.Launcher.LaunchUriAsync(uri);}catch(Exception error){status.Text=error.Message;}};links.Children.Add(button);}
        page.Children.Add(check);page.Children.Add(status);page.Children.Add(links);page.Children.Add(notes);
    }
}
