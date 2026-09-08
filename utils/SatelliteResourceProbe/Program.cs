using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.Loader;
using System.Text.Json;

// Resource-only child process. Never invoke the DS4Windows entry point,
// construct App/ControlService, or initialize controller access.
Assembly application = AssemblyLoadContext.Default.LoadFromAssemblyPath(
    Path.Combine(AppContext.BaseDirectory, "DS4Windows.dll"));
var resources = new ResourceManager("DS4WinWPF.Translations.Strings", application);
string? neutral = resources.GetString("Browse", CultureInfo.InvariantCulture);
string? german = resources.GetString("Browse", CultureInfo.GetCultureInfo("de"));
string? regionalGerman = resources.GetString("Browse", CultureInfo.GetCultureInfo("de-DE"));
string? unavailableCulture = resources.GetString("Browse", CultureInfo.GetCultureInfo("eo"));
bool unrelatedRejected = false;
try
{
    AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(
        "DS4Windows.UnrelatedProbe.resources, Culture=de"));
}
catch (FileNotFoundException)
{
    unrelatedRejected = true;
}
Assembly scheduler = AssemblyLoadContext.Default.LoadFromAssemblyPath(
    Path.Combine(AppContext.BaseDirectory, "Microsoft.Win32.TaskScheduler.dll"));
string? schedulerSatellite = null;
string? schedulerError = null;
try
{
    schedulerSatellite = scheduler.GetSatelliteAssembly(
        CultureInfo.GetCultureInfo("de")).Location;
}
catch (FileNotFoundException error)
{
    schedulerError = error.GetType().Name;
}

bool localized = !string.IsNullOrWhiteSpace(german) && german != neutral;
Console.WriteLine(JsonSerializer.Serialize(new
{
    CurrentDirectory = Environment.CurrentDirectory,
    BaseDirectory = AppContext.BaseDirectory,
    ResourceRoots = AppContext.GetData("PLATFORM_RESOURCE_ROOTS"),
    Neutral = neutral,
    German = german,
    RegionalGerman = regionalGerman,
    UnavailableCulture = unavailableCulture,
    UnrelatedRejected = unrelatedRejected,
    Localized = localized,
    SchedulerSatellite = schedulerSatellite,
    SchedulerError = schedulerError,
}));
return localized && regionalGerman == german && unavailableCulture == neutral &&
    unrelatedRejected && schedulerSatellite != null ? 0 : 1;
