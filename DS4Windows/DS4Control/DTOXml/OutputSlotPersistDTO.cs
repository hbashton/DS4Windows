/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using DS4Windows;

namespace DS4WinWPF.DS4Control.DTOXml
{
    [XmlRoot("OutputSlots")]
    public class OutputSlotPersistDTO : IDTO<OutputSlotManager>
    {
        [XmlAttribute("app_version")]
        public string AppVersion
        {
            get => Global.exeversion;
            set { }
        }

        [XmlElement("Slot")] // Use XmlElement here to skip container element
        public List<OutputSlotSerializer> SlotItems
        {
            get; set;
        }

        public OutputSlotPersistDTO()
        {
            SlotItems = new List<OutputSlotSerializer>();
        }

        public void MapFrom(OutputSlotManager source)
        {
            foreach (OutSlotDevice dev in source.OutputSlots)
            {
                if (dev.CurrentReserveStatus == OutSlotDevice.ReserveStatus.Permanent)
                {
                    OutputSlotSerializer tempSlot = new OutputSlotSerializer()
                    {
                        Index = dev.Index,
                        DeviceType = dev.PermanentType,
                    };

                    SlotItems.Add(tempSlot);
                }
            }
        }

        public void MapTo(OutputSlotManager destination)
        {
            foreach(OutputSlotSerializer tempSlot in SlotItems)
            {
                OutSlotDevice tempDev = null;
                if (tempSlot.Index >= 0 && tempSlot.Index <= 3)
                {
                    int idx = tempSlot.Index;
                    tempDev = destination.OutputSlots[idx];
                }

                if (tempDev != null)
                {
                    if (tempSlot.DeviceType == OutContType.None)
                    {
                        continue;
                    }

                    tempDev.CurrentReserveStatus = OutSlotDevice.ReserveStatus.Permanent;
                    tempDev.PermanentType = tempSlot.DeviceType;
                }
            }
        }

        internal static OutContType ParseOutputDeviceType(string value, OutContType fallback)
        {
            if (Enum.TryParse(value, true, out OutContType parsed) &&
                Enum.IsDefined(typeof(OutContType), parsed))
            {
                return parsed;
            }

            return fallback;
        }

        internal static string FormatOutputDeviceType(OutContType value)
        {
            return value switch
            {
                OutContType.DS4 => "DS4",
                OutContType.None => "None",
                _ => "X360",
            };
        }
    }

    public class OutputSlotSerializer
    {
        [XmlAttribute("idx")]
        public int Index
        {
            get; set;
        } = 0;

        [XmlIgnore]
        public OutContType DeviceType
        {
            get; set;
        }

        [XmlElement("DeviceType")]
        public string DeviceTypeString
        {
            get => OutputSlotPersistDTO.FormatOutputDeviceType(DeviceType);
            set => DeviceType = OutputSlotPersistDTO.ParseOutputDeviceType(value, OutContType.None);
        }
    }
}
