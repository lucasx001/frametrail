using System.Globalization;
using System.Text.Json;

namespace FrameTrail.Domain;

// Application language is independent of JSON serialization and authored text.
// A running media session keeps its language snapshot until the next launch.
public static class AppText
{
    private static readonly IReadOnlyDictionary<string,string> Chinese=Load("zh-Hans"), English=Load("en");
    public static string PreferencePath { get; }=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FrameTrail","language.txt");
    public static string Preference { get; private set; }=ReadPreference();
    public static string Language { get; private set; }=Resolve(Preference,CultureInfo.CurrentUICulture.Name);
    public static string Resolve(string preference,string systemLanguage)=>preference switch {"en"=>"en","zh-Hans"=>"zh-Hans",_=>systemLanguage.StartsWith("zh",StringComparison.OrdinalIgnoreCase)?"zh-Hans":"en"};
    private static string ReadPreference(){try{return File.ReadAllText(PreferencePath).Trim();}catch(IOException){return "system";}catch(UnauthorizedAccessException){return "system";}}
    public static void Initialize(string? launchOverride=null)=>Language=Resolve(launchOverride??Preference,CultureInfo.CurrentUICulture.Name);
    public static void SavePreference(string preference)
    {
        if(preference is not ("system" or "en" or "zh-Hans"))throw new ArgumentOutOfRangeException(nameof(preference));
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);string temporary=PreferencePath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,preference);File.Move(temporary,PreferencePath,true);Preference=preference;}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static IReadOnlyDictionary<string,string> Resources(string language)=>language=="en"?English:Chinese;
    private static IReadOnlyDictionary<string,string> Load(string language)
    {
        using var stream=typeof(AppText).Assembly.GetManifestResourceStream("FrameTrail.Domain.Resources."+language+".json")??throw new InvalidOperationException("Language resource missing: "+language);
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    }
    public static string T(string key,params object?[] arguments)
    {
        var values=Resources(Language);string text=values.TryGetValue(key,out var value)?value:throw new KeyNotFoundException("Unknown language key: "+key);
        return arguments.Length==0?text:string.Format(CultureInfo.GetCultureInfo(Language),text,arguments);
    }
}
