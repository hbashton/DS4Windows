# Profile schema history

[Documentation index](../README.md)

This reference preserves early profile-format changes; it is not the app release
changelog or an exhaustive description of the current profile fields. Inspect
`DS4Windows/DS4Control/DTOXml/ProfileDTO.cs` and round-trip tests for the current schema.
App/file versions and profile `config_version` are separate values.

This document will present some of the distinctive (mostly backwards incompatible)
changes that justify incrementing the version number for the profile schema.
The **config_version** attribute in a profile will describe the last known schema
used to write out a profile. Some older profiles did not contain the
**config_version** attribute so tags used in the profile might have to be examined
to determine an approximate version number corresponding to the schema version.

### 5 (DS4Windows 2.1.15)

Replace old **UseTPforControls** option with a new **TouchpadOutputMode**
option. Will likely be used in the future to allow for more than two
modes for the Touchpad. Adding absolute mouse pointer support is one
idea

### 4 (DS4Windows 2.1.6)

Replace **GyroSmoothing** and **GyroSmoothingWeight** and bundle new tags
in the **GyroMouseSmoothingSettings** group. Allow 1 Euro Filter options

### 3 (DS4Windows 2.1.3)

Version 3 was the first profile schema to have a **config_version**
specified. It was used primarily as a placeholder. I don't know
if the schema really changed from what would be considered
version 2

### 2

Changed default values for **LSDeadZone** and **RSDeadZone**.
Added anti-dead zone info

### 1 (DS4Windows 1.4.52)

Initial schema as used in the last Jays2Kings build (1.4.52)
