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
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class BindingWindowViewModel
    {
        private int deviceNum;
        private bool use360Mode;
        private DS4ControlSettings settings;
        private OutBinding currentOutBind;
        private OutBinding shiftOutBind;
        private bool showShift;
        private bool rumbleActive;

        public bool Using360Mode
        {
            get => use360Mode;
        }
        public int DeviceNum { get => deviceNum; }
        public OutBinding CurrentOutBind { get => currentOutBind; }
        public OutBinding ShiftOutBind { get => shiftOutBind; }
        public OutBinding ActionBinding { get; set; }

        public bool ShowShift { get => showShift; set => showShift = value; }
        public bool RumbleActive { get => rumbleActive; set => rumbleActive = value; }
        public DS4ControlSettings Settings { get => settings; }

        public BindingWindowViewModel(int deviceNum, DS4ControlSettings settings)
        {
            this.deviceNum = deviceNum;
            use360Mode = Global.outDevTypeTemp[deviceNum] == OutContType.X360;
            this.settings = settings;
            currentOutBind = new OutBinding();
            shiftOutBind = new OutBinding();
            shiftOutBind.shiftBind = true;
            PopulateCurrentBinds();
        }

        public void PopulateCurrentBinds()
        {
            DS4ControlSettings setting = settings;
            bool sc = setting.keyType.HasFlag(DS4KeyType.ScanCode);
            bool toggle = setting.keyType.HasFlag(DS4KeyType.Toggle);
            currentOutBind.input = setting.control;
            shiftOutBind.input = setting.control;
            if (setting.actionType != DS4ControlSettings.ActionType.Default)
            {
                switch(setting.actionType)
                {
                    case DS4ControlSettings.ActionType.Button:
                        currentOutBind.outputType = OutBinding.OutType.Button;
                        currentOutBind.control = (X360Controls)setting.action.actionBtn;
                        break;
                    case DS4ControlSettings.ActionType.Default:
                        currentOutBind.outputType = OutBinding.OutType.Default;
                        break;
                    case DS4ControlSettings.ActionType.Key:
                        currentOutBind.outputType = OutBinding.OutType.Key;
                        currentOutBind.outkey = setting.action.actionKey;
                        currentOutBind.hasScanCode = sc;
                        currentOutBind.toggle = toggle;
                        break;
                    case DS4ControlSettings.ActionType.Macro:
                        currentOutBind.outputType = OutBinding.OutType.Macro;
                        currentOutBind.macro = (int[])setting.action.actionMacro;
                        currentOutBind.macroType = settings.keyType;
                        currentOutBind.hasScanCode = sc;
                        break;
                }
            }
            else
            {
                currentOutBind.outputType = OutBinding.OutType.Default;
            }

            if (!string.IsNullOrEmpty(setting.extras))
            {
                currentOutBind.ParseExtras(setting.extras);
            }

            if (setting.shiftActionType != DS4ControlSettings.ActionType.Default)
            {
                sc = setting.shiftKeyType.HasFlag(DS4KeyType.ScanCode);
                toggle = setting.shiftKeyType.HasFlag(DS4KeyType.Toggle);
                shiftOutBind.shiftTrigger = setting.shiftTrigger;
                switch (setting.shiftActionType)
                {
                    case DS4ControlSettings.ActionType.Button:
                        shiftOutBind.outputType = OutBinding.OutType.Button;
                        shiftOutBind.control = (X360Controls)setting.shiftAction.actionBtn;
                        break;
                    case DS4ControlSettings.ActionType.Default:
                        shiftOutBind.outputType = OutBinding.OutType.Default;
                        break;
                    case DS4ControlSettings.ActionType.Key:
                        shiftOutBind.outputType = OutBinding.OutType.Key;
                        shiftOutBind.outkey = setting.shiftAction.actionKey;
                        shiftOutBind.hasScanCode = sc;
                        shiftOutBind.toggle = toggle;
                        break;
                    case DS4ControlSettings.ActionType.Macro:
                        shiftOutBind.outputType = OutBinding.OutType.Macro;
                        shiftOutBind.macro = (int[])setting.shiftAction.actionMacro;
                        shiftOutBind.macroType = setting.shiftKeyType;
                        shiftOutBind.hasScanCode = sc;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(setting.shiftExtras))
            {
                shiftOutBind.ParseExtras(setting.shiftExtras);
            }

            if (settings.LightbarMacro is not null) currentOutBind.LightbarMacro = settings.LightbarMacro;
        }

        public void PrepareSaveMacro(OutBinding bind, bool shiftBind=false)
        {
            DS4ControlSettings setting = settings;

            if (!shiftBind)
            {
                bind.outputType = OutBinding.OutType.Macro;
                bind.macro = (int[])setting.action.actionMacro;
                bind.macroType = settings.keyType;
            }
            else
            {
                bind.outputType = OutBinding.OutType.Macro;
                bind.macro = (int[])setting.shiftAction.actionMacro;
                bind.macroType = setting.shiftKeyType;
            }
        }

        public void WriteBinds()
        {
            currentOutBind.WriteBind(settings);
            shiftOutBind.WriteBind(settings);
        }

        public void StartForcedColor(Color color)
        {
            if (deviceNum < ControlService.CURRENT_DS4_CONTROLLER_LIMIT)
            {
                DS4Color dcolor = new DS4Color() { red = color.R, green = color.G, blue = color.B };
                DS4LightBar.forcedColor[deviceNum] = dcolor;
                DS4LightBar.forcedFlash[deviceNum] = 0;
                DS4LightBar.forcelight[deviceNum] = true;
            }
        }

        public void EndForcedColor()
        {
            if (deviceNum < ControlService.CURRENT_DS4_CONTROLLER_LIMIT)
            {
                DS4LightBar.forcedColor[deviceNum] = new DS4Color(0, 0, 0);
                DS4LightBar.forcedFlash[deviceNum] = 0;
                DS4LightBar.forcelight[deviceNum] = false;
            }
        }

        public void UpdateForcedColor(Color color)
        {
            if (deviceNum < ControlService.CURRENT_DS4_CONTROLLER_LIMIT)
            {
                DS4Color dcolor = new DS4Color() { red = color.R, green = color.G, blue = color.B };
                DS4LightBar.forcedColor[deviceNum] = dcolor;
                DS4LightBar.forcedFlash[deviceNum] = 0;
                DS4LightBar.forcelight[deviceNum] = true;
            }
        }
    }

    public class BindAssociation
    {
        public enum OutType : uint
        {
            Default,
            Key,
            Button,
            Macro
        }

        public OutType outputType;
        public X360Controls control;
        public int outkey;

        public bool IsMouse()
        {
            return outputType == OutType.Button && (control >= X360Controls.LeftMouse && control < X360Controls.Unbound);
        }

        public static bool IsMouseRange(X360Controls control)
        {
            return control >= X360Controls.LeftMouse && control < X360Controls.Unbound;
        }
    }

    public class OutBinding
    {
        public enum OutType : uint
        {
            Default,
            Key,
            Button,
            Macro
        }

        public DS4Controls input;
        public bool toggle;
        public bool hasScanCode;
        public OutType outputType;
        public int outkey;
        public int[] macro;
        public DS4KeyType macroType;
        public X360Controls control;
        public bool shiftBind;
        public int shiftTrigger;
        private int heavyRumble = 0;
        private int lightRumble = 0;
        private int flashRate;
        private int mouseSens = 25;
        private DS4Color extrasColor = new DS4Color(255,255,255);

        private LightbarMacro lightbarMacro;

        public LightbarMacro LightbarMacro
        {
            get => lightbarMacro;
            set
            {
                lightbarMacro = value;
                LightbarMacroChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler LightbarMacroChanged;

        private uint currentInterval;
        public uint CurrentInterval
        {
            get => currentInterval;
            set
            {
                currentInterval = value;
                CurrentIntervalChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler CurrentIntervalChanged;

        private Color currentColor;
        public Color CurrentColor
        {
            get => currentColor;
            set
            {
                currentColor = value;
                CurrentColorString = value.ToString();
                CurrentColorStringChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public string CurrentColorString { get; private set; }
        public event EventHandler CurrentColorStringChanged;

        public bool HasScanCode { get => hasScanCode; set => hasScanCode = value; }
        public bool Toggle { get => toggle; set => toggle = value; }
        public int ShiftTrigger
        {
            get => shiftTrigger;
            set => shiftTrigger = value;
        }
        public int HeavyRumble { get => heavyRumble; set => heavyRumble = value; }
        public int LightRumble { get => lightRumble; set => lightRumble = value; }
        public int FlashRate
        {
            get => flashRate;
            set
            {
                flashRate = value;
                FlashRateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler FlashRateChanged;

        public int MouseSens
        {
            get => mouseSens;
            set
            {
                mouseSens = value;
                MouseSensChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler MouseSensChanged;

        private bool useMouseSens;
        public bool UseMouseSens
        {
            get
            {
                return useMouseSens;
            }
            set
            {
                useMouseSens = value;
                UseMouseSensChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler UseMouseSensChanged;

        private bool useExtrasColor;
        public bool UseExtrasColor {
            get
            {
                return useExtrasColor;
            }
            set
            {
                useExtrasColor = value;
                UseExtrasColorChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler UseExtrasColorChanged;

        public int ExtrasColorR
        {
            get => extrasColor.red;
            set
            {
                extrasColor.red = (byte)value;
                ExtrasColorRChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ExtrasColorRChanged;

        public string ExtrasColorRString
        {
            get
            {
                string temp = $"#{extrasColor.red.ToString("X2")}FF0000";
                return temp;
            }
        }
        public event EventHandler ExtrasColorRStringChanged;
        public int ExtrasColorG
        {
            get => extrasColor.green;
            set
            {
                extrasColor.green = (byte)value;
                ExtrasColorGChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ExtrasColorGChanged;

        public string ExtrasColorGString
        {
            get
            {
                string temp = $"#{ extrasColor.green.ToString("X2")}00FF00";
                return temp;
            }
        }
        public event EventHandler ExtrasColorGStringChanged;

        public int ExtrasColorB
        {
            get => extrasColor.blue;
            set
            {
                extrasColor.blue = (byte)value;
                ExtrasColorBChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ExtrasColorBChanged;

        public string ExtrasColorBString
        {
            get
            {
                string temp = $"#{extrasColor.blue.ToString("X2")}0000FF";
                return temp;
            }
        }
        public event EventHandler ExtrasColorBStringChanged;

        public string ExtrasColorString
        {
            get => $"#FF{extrasColor.red.ToString("X2")}{extrasColor.green.ToString("X2")}{extrasColor.blue.ToString("X2")}";
        }
        public event EventHandler ExtrasColorStringChanged;

        public Color ExtrasColorMedia
        {
            get
            {
                return new Color()
                {
                    A = 255,
                    R = extrasColor.red,
                    B = extrasColor.blue,
                    G = extrasColor.green
                };
            }
        }

        private int shiftTriggerIndex;
        public int ShiftTriggerIndex { get => shiftTriggerIndex; set => shiftTriggerIndex = value; }

        public string DefaultColor
        {
            get
            {
                string color = string.Empty;
                if (outputType == OutType.Default)
                {
                    color =  Colors.LimeGreen.ToString();
                }
                else
                {
                    color = Application.Current.FindResource("SecondaryColor").ToString();
                    //color = SystemColors.ControlBrush.Color.ToString();
                }

                return color;
            }
        }

        public string UnboundColor
        {
            get
            {
                string color = string.Empty;
                if (outputType == OutType.Button && control == X360Controls.Unbound)
                {
                    color = Colors.LimeGreen.ToString();
                }
                else
                {
                    color = Application.Current.FindResource("SecondaryColor").ToString();
                    //color = SystemColors.ControlBrush.Color.ToString();
                }

                return color;
            }
        }

        public string DefaultBtnString
        {
            get
            {
                string result = "Default";
                if (shiftBind)
                {
                    result = Properties.Resources.FallBack;
                }

                return result;
            }
        }

        public Visibility MacroLbVisible
        {
            get
            {
                return outputType == OutType.Macro ? Visibility.Visible : Visibility.Hidden;
            }
        }

        public OutBinding()
        {
            ExtrasColorRChanged += OutBinding_ExtrasColorRChanged;
            ExtrasColorGChanged += OutBinding_ExtrasColorGChanged;
            ExtrasColorBChanged += OutBinding_ExtrasColorBChanged;
            UseExtrasColorChanged += OutBinding_UseExtrasColorChanged;
            CurrentColor = Color.FromRgb(255, 255, 255);
            LightbarMacro = new LightbarMacro();
        }

        private void OutBinding_ExtrasColorBChanged(object sender, EventArgs e)
        {
            ExtrasColorStringChanged?.Invoke(this, EventArgs.Empty);
            ExtrasColorBStringChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OutBinding_ExtrasColorGChanged(object sender, EventArgs e)
        {
            ExtrasColorStringChanged?.Invoke(this, EventArgs.Empty);
            ExtrasColorGStringChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OutBinding_ExtrasColorRChanged(object sender, EventArgs e)
        {
            ExtrasColorStringChanged?.Invoke(this, EventArgs.Empty);
            ExtrasColorRStringChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OutBinding_UseExtrasColorChanged(object sender, EventArgs e)
        {
            if (!useExtrasColor)
            {
                ExtrasColorR = 255;
                ExtrasColorG = 255;
                ExtrasColorB = 255;
            }
        }

        public bool IsShift()
        {
            return shiftBind;
        }

        public bool IsMouse()
        {
            return outputType == OutType.Button && (control >= X360Controls.LeftMouse && control < X360Controls.Unbound);
        }

        public static bool IsMouseRange(X360Controls control)
        {
            return control >= X360Controls.LeftMouse && control < X360Controls.Unbound;
        }

        public void ParseExtras(string extras)
        {
            string[] temp = extras.Split(',');
            if (temp.Length == 9)
            {
                int.TryParse(temp[0], out heavyRumble);
                int.TryParse(temp[1], out lightRumble);
                int.TryParse(temp[2], out int useColor);
                if (useColor == 1)
                {
                    useExtrasColor = true;
                    byte.TryParse(temp[3], out extrasColor.red);
                    byte.TryParse(temp[4], out extrasColor.green);
                    byte.TryParse(temp[5], out extrasColor.blue);
                    int.TryParse(temp[6], out flashRate);
                }
                else
                {
                    useExtrasColor = false;
                    extrasColor.red = extrasColor.green = extrasColor.blue = 255;
                    flashRate = 0;
                }

                int.TryParse(temp[7], out int useM);
                if (useM == 1)
                {
                    useMouseSens = true;
                    int.TryParse(temp[8], out mouseSens);
                }
                else
                {
                    useMouseSens = false;
                    mouseSens = 25;
                }
            }
        }

        public string CompileExtras()
        {
            string result = $"{heavyRumble},{lightRumble},";
            if (useExtrasColor)
            {
                result += $"1,{extrasColor.red},{extrasColor.green},{extrasColor.blue},{flashRate},";
            }
            else
            {
                result += "0,0,0,0,0,";
            }

            if (useMouseSens)
            {
                result += $"1,{mouseSens}";
            }
            else
            {
                result += "0,0";
            }

            return result;
        }

        public bool IsUsingExtras()
        {
            bool result = false;
            result = result || (heavyRumble != 0);
            result = result || (lightRumble != 0);
            result = result || useExtrasColor;
            result = result ||
                (extrasColor.red != 255 && extrasColor.green != 255 &&
                extrasColor.blue != 255);

            result = result || (flashRate != 0);
            result = result || useMouseSens;
            result = result || (mouseSens != 25);
            return result;
        }

        public void WriteBind(DS4ControlSettings settings)
        {
            if (!shiftBind)
            {
                settings.keyType = DS4KeyType.None;

                if (outputType == OutType.Default)
                {
                    settings.action.actionKey = 0;
                    settings.actionType = DS4ControlSettings.ActionType.Default;
                }
                else if (outputType == OutType.Button)
                {
                    settings.action.actionBtn = control;
                    settings.actionType = DS4ControlSettings.ActionType.Button;
                    if (control == X360Controls.Unbound)
                    {
                        settings.keyType |= DS4KeyType.Unbound;
                    }
                }
                else if (outputType == OutType.Key)
                {
                    settings.action.actionKey = outkey;
                    settings.actionType = DS4ControlSettings.ActionType.Key;
                    if (hasScanCode)
                    {
                        settings.keyType |= DS4KeyType.ScanCode;
                    }

                    if (toggle)
                    {
                        settings.keyType |= DS4KeyType.Toggle;
                    }
                }
                else if (outputType == OutType.Macro)
                {
                    settings.action.actionMacro = macro;
                    settings.actionType = DS4ControlSettings.ActionType.Macro;
                    if (macroType.HasFlag(DS4KeyType.HoldMacro))
                    {
                        settings.keyType |= DS4KeyType.HoldMacro;
                    }
                    else
                    {
                        settings.keyType |= DS4KeyType.Macro;
                    }

                    if (hasScanCode)
                    {
                        settings.keyType |= DS4KeyType.ScanCode;
                    }
                }

                if (IsUsingExtras())
                {
                    settings.extras = CompileExtras();
                }
                else
                {
                    settings.extras = string.Empty;
                }

                if (LightbarMacro is not null && LightbarMacro.Macro.Count > 0)
                    settings.LightbarMacroString = LightbarMacro.Compile();

                Global.RefreshActionAlias(settings, shiftBind);
            }
            else
            {
                settings.shiftKeyType = DS4KeyType.None;
                settings.shiftTrigger = shiftTrigger;

                if (outputType == OutType.Default || shiftTrigger == 0)
                {
                    settings.shiftAction.actionKey = 0;
                    settings.shiftActionType = DS4ControlSettings.ActionType.Default;
                }
                else if (outputType == OutType.Button)
                {
                    settings.shiftAction.actionBtn = control;
                    settings.shiftActionType = DS4ControlSettings.ActionType.Button;
                    if (control == X360Controls.Unbound)
                    {
                        settings.shiftKeyType |= DS4KeyType.Unbound;
                    }
                }
                else if (outputType == OutType.Key)
                {
                    settings.shiftAction.actionKey = outkey;
                    settings.shiftActionType = DS4ControlSettings.ActionType.Key;
                    if (hasScanCode)
                    {
                        settings.shiftKeyType |= DS4KeyType.ScanCode;
                    }

                    if (toggle)
                    {
                        settings.shiftKeyType |= DS4KeyType.Toggle;
                    }
                }
                else if (outputType == OutType.Macro)
                {
                    settings.shiftAction.actionMacro = macro;
                    settings.shiftActionType = DS4ControlSettings.ActionType.Macro;

                    if (macroType.HasFlag(DS4KeyType.HoldMacro))
                    {
                        settings.shiftKeyType |= DS4KeyType.HoldMacro;
                    }
                    else
                    {
                        settings.shiftKeyType |= DS4KeyType.Macro;
                    }

                    if (hasScanCode)
                    {
                        settings.shiftKeyType |= DS4KeyType.ScanCode;
                    }
                }

                if (IsUsingExtras())
                {
                    settings.shiftExtras = CompileExtras();
                }
                else
                {
                    settings.shiftExtras = string.Empty;
                }

                Global.RefreshActionAlias(settings, shiftBind);
            }
        }

        public void UpdateExtrasColor(Color color)
        {
            ExtrasColorR = color.R;
            ExtrasColorG = color.G;
            ExtrasColorB = color.B;
        }
    }

    public class LightbarMacro
    {
        private bool _active;
        public bool Active
        {
            get => _active;
            set
            {
                _active = value;
                ActiveChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler ActiveChanged;

        public ObservableCollection<LightbarMacroElement> Macro { get; set; }

        private LightbarMacroTrigger _trigger;
        public LightbarMacroTrigger Trigger
        {
            get => _trigger;
            set
            {
                _trigger = value;
                TriggerChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler TriggerChanged;

        private bool _cancelCurrent;
        /// <summary>
        ///     Indicates whether this macro should take precedence over a currently running one
        /// </summary>
        public bool CancelCurrent
        {
            get => _cancelCurrent;
            set
            {
                _cancelCurrent = value;
                CancelCurrentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler CancelCurrentChanged;

        public LightbarMacro()
        {
            Active = false;
            Macro = [];
            Trigger = LightbarMacroTrigger.Press;
            CancelCurrent = false;
        }

        /// <summary>
        ///     Automatically parses a string to this object
        /// </summary>
        /// <param name="macroString">Correctly formatted string, see <see cref="Compile"/></param>
        public LightbarMacro(string macroString)
        {
            Parse(macroString);
        }

        public LightbarMacro(bool active, LightbarMacroElement[] macro, LightbarMacroTrigger trigger, bool cancelCurrent)
        {
            Active = active;
            Trigger = trigger;
            Macro = new ObservableCollection<LightbarMacroElement>(macro);
            CancelCurrent = cancelCurrent;
        }

        /// <summary>
        ///     <para>Compiles this object to a string that can be serialised and is used in the profile XML.</para>
        ///     <para>Example: <c>True/255,255,255:100;255,0,0:50;/Press/False</c>, which means:</para>
        ///     <para><see cref="Active"/>: <c>true</c>,</para>
        ///     <para><see cref="Macro"/>: 2 elements: #FFFFFF for 100ms and #FF0000 for 50ms</para>
        ///     <para><see cref="Trigger"/>: <see cref="LightbarMacroTrigger.Press"/></para>
        ///     <para><see cref="CancelCurrent"/>: <c>false</c></para>
        /// </summary>
        /// <returns>Serialisable string</returns>
        public string Compile()
        {
            var sb = new StringBuilder();

            sb.Append(Active.ToString());

            // / between active flag, macro, trigger and cancellation flag
            sb.Append('/');

            var firstAppended = false;
            foreach (var element in Macro)
            {
                // ; after each element of the macro
                if (firstAppended)
                    sb.Append(';');
                else
                    firstAppended = true;

                // format of r,g,b to comply with TryParse method on DS4Color class
                sb.Append(element.Color.red);
                sb.Append(',');
                sb.Append(element.Color.green);
                sb.Append(',');
                sb.Append(element.Color.blue);
                // : between the colour and the timespan
                sb.Append(':');
                sb.Append(element.Length);
            }

            sb.Append('/');
            sb.Append(Trigger.ToString());

            sb.Append('/');
            sb.Append(CancelCurrent.ToString());

            return sb.ToString();
        }

        public void Parse(string macro)
        {
            var parsed = GetMacroFromString(macro);
            Active = parsed.Active;
            Macro = parsed.Macro;
            Trigger = parsed.Trigger;
            CancelCurrent = parsed.CancelCurrent;
        }

        public static LightbarMacro GetMacroFromString(string macro)
        {
            // parsing the string in general + active flag
            var fieldSplit = macro.Split('/');
            if (fieldSplit.Length is < 3 or > 4) throw new ArgumentException($"Provided string doesn't comply with the format (too few or too many sections separated with '/').\n{macro}");

            // prior to 3.9.2 cancellation was not available, so the string was different, check is done for backwards compatibility
            var oldFormat = fieldSplit.Length == 3;

            if (!bool.TryParse(fieldSplit[0], out var active)) throw new ArgumentException("Provided string doesn't comply with the format (cannot parse active flag).");

            var macroSplit = fieldSplit[1].Split(';');
            List<LightbarMacroElement> macroList = [];
            foreach (var macroElement in macroSplit)
            {
                if (string.IsNullOrEmpty(macroElement)) return new LightbarMacro();
                var elementSplit = macroElement.Split(':');
                var color = elementSplit[0];
                var length = elementSplit[1];
                DS4Color parsedColor = new();
                if (!DS4Color.TryParse(color, ref parsedColor)
                    || !uint.TryParse(length, out var parsedLength))
                    throw new ArgumentException("Provided lightbar macro string doesn't comply with the format (cannot parse color or length).");
                macroList.Add(new LightbarMacroElement(parsedColor, parsedLength));
            }

            Enum.TryParse<LightbarMacroTrigger>(fieldSplit[2], out var lightbarMacroTrigger);

            bool cancel;
            // before the choice of cancellation was introduced, default behaviour was to cancel the current macro
            if (oldFormat)
                cancel = true;
            else
                if (!bool.TryParse(fieldSplit[3], out cancel))
                    throw new ArgumentException("Provided lightbar macro string doesn't comply with the format (cannot parse cancellation flag).");

            return new LightbarMacro(active, macroList.ToArray(), lightbarMacroTrigger, cancel);
        }
    }
}
