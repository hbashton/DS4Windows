# Swipe profile selection

[User guide](user-guide.md) · [Documentation index](README.md)

A two-finger touchpad swipe can switch between saved profiles.
In Settings, enable **Swipe touchpad to switch profiles**, then open
**Config Swipe Profiles** and select the profiles to cycle through.

A left/right swipe skips profiles outside the selection. Selecting no profiles
uses the original behaviour and cycles through all profiles. This also applies
to older settings files without a saved swipe list.

The selection is stored in the application settings as `SwipeProfileList`,
rather than as a binding in each profile. Contributor reference:
`DS4Windows/DS4Control/DTOXml/AppSettingsDTO.cs` and
`DS4Windows/DS4Forms/MainWindow.xaml.cs`.
