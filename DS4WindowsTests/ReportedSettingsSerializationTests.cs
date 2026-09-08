using System.Globalization;
using System.Xml.Serialization;
using DS4Windows;
using DS4WinWPF.DS4Control.DTOXml;

namespace DS4WindowsTests;

[TestClass]
[DoNotParallelize]
public class ReportedSettingsSerializationTests
{
    [DataTestMethod]
    [DataRow("en-US", "12/05/2023 00:24:15", 12, 5)]
    [DataRow("en-GB", "12/05/2023 00:24:15", 12, 5)]
    [DataRow("fr-FR", "12/05/2023 00:24:15", 12, 5)]
    [DataRow("en-GB", "12/23/2023 00:24:15", 12, 23)]
    [DataRow("fr-FR", "12/23/2023 00:24:15", 12, 23)]
    [DataRow("th-TH", "12/05/2023 00:24:15", 12, 5)]
    public void SavedUpdateCheckDateUsesItsCanonicalFormatAcrossLocales(
        string cultureName, string savedDate, int month, int day)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            AppSettingsDTO dto = ReadSettings(savedDate);

            Assert.AreEqual(new DateTime(2023, month, day, 0, 24, 15),
                dto.LastChecked);
            Assert.AreEqual(savedDate, dto.LastCheckString);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [DataTestMethod]
    [DataRow("en-GB")]
    [DataRow("fr-FR")]
    [DataRow("fi-FI")]
    [DataRow("th-TH")]
    public void UpdateCheckDateWriterDoesNotUseLocalSeparatorsOrCalendar(
        string cultureName)
    {
        const string savedDate = "12/05/2023 00:24:15";
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            AppSettingsDTO dto = ReadSettings(savedDate);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            var serializer = new XmlSerializer(typeof(AppSettingsDTO));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            serializer.Serialize(writer, dto);
            StringAssert.Contains(writer.ToString(),
                $"<LastChecked>{savedDate}</LastChecked>");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void OlderLocalizedUpdateCheckDateRemainsReadable()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            AppSettingsDTO dto = ReadSettings("23.12.2023 18:21:05");

            Assert.AreEqual(new DateTime(2023, 12, 23, 18, 21, 5), dto.LastChecked);
            Assert.AreEqual("12/23/2023 18:21:05", dto.LastCheckString);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void InvalidUpdateCheckDateDoesNotReplaceTheLastValidValue()
    {
        AppSettingsDTO dto = ReadSettings("12/23/2023 00:24:15");
        DateTime previous = dto.LastChecked;

        dto.LastCheckString = "not a date";

        Assert.AreEqual(previous, dto.LastChecked);
    }

    [DataTestMethod]
    [DataRow("<Red>17</Red><Green>83</Green><Blue>201</Blue>", 17, 83, 201)]
    [DataRow("<Green>83</Green><Blue>201</Blue>", 0, 83, 201)]
    [DataRow("<Red>17</Red><Green>invalid</Green><Blue>201</Blue>", 17, 0, 201)]
    [DataRow("<Red>17</Red><Green>83</Green><Blue>invalid</Blue>", 17, 83, 0)]
    [DataRow("<Red>17</Red><Green>83</Green><Blue>201</Blue><Color>41,72,103</Color>", 41, 72, 103)]
    public void LegacyProfileLightbarChannelsMapIndependently(
        string colorElements, int red, int green, int blue)
    {
        var serializer = new XmlSerializer(typeof(ProfileDTO),
            ProfileDTO.GetAttributeOverrides());
        using var reader = new StringReader(
            $"<DS4Windows config_version=\"5\">{colorElements}</DS4Windows>");
        var dto = (ProfileDTO)serializer.Deserialize(reader);
        dto.DeviceIndex = Global.TEST_PROFILE_INDEX;
        BackingStore store = BackingStore.CreateProfileValidationStore();

        dto.MapTo(store);

        DS4Color actual = store.lightbarSettingInfo[Global.TEST_PROFILE_INDEX]
            .ds4winSettings.m_Led;
        Assert.AreEqual((byte)red, actual.red);
        Assert.AreEqual((byte)green, actual.green);
        Assert.AreEqual((byte)blue, actual.blue);
    }

    private static AppSettingsDTO ReadSettings(string savedDate)
    {
        var serializer = new XmlSerializer(typeof(AppSettingsDTO));
        using var reader = new StringReader(
            $"<Profile><LastChecked>{savedDate}</LastChecked></Profile>");
        return (AppSettingsDTO)serializer.Deserialize(reader);
    }
}
