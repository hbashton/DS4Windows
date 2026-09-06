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

namespace DS4Windows
{
    public class DS4StateFieldMapping
    {
        public enum ControlType : int { Unknown = 0, Button, AxisDir, Trigger, Touch, GyroDir, SwipeDir }
        public const byte LAST_DS4_ACTION =
            (byte)DS4Controls.Switch2JoyConRightSR;
        public const byte TRIGGER_FULL_PULL_THRESHOLD = 250;
        private const ushort SWITCH2_PRO_SOURCE_CONTRACT_VERSION =
            Switch2.Switch2ProProfileInputFrame.CurrentVersion;
        private const ushort SWITCH2_JOYCON_SOURCE_CONTRACT_VERSION =
            Switch2.Switch2JoyConProfileInputFrame.CurrentVersion;

        public bool[] buttons = new bool[(int)LAST_DS4_ACTION + 1];
        public readonly AxisDirectionStore axisdirs = new((int)LAST_DS4_ACTION + 1);
        public byte[] triggers = new byte[(int)LAST_DS4_ACTION + 1];
        public int[] gryodirs = new int[(int)LAST_DS4_ACTION + 1];
        public byte[] swipedirs = new byte[(int)LAST_DS4_ACTION + 1];
        public bool[] swipedirbools = new bool[(int)LAST_DS4_ACTION + 1];
        public bool touchButton = false;
        public bool outputTouchButton = false;

        /// <summary>
        /// One authoritative mapped-axis store with the historical indexed
        /// byte surface. A byte write is an explicit replacement, including
        /// writing the byte already displayed by a precise value. Typed
        /// mapping stages use value copies rather than a raw-input sidecar.
        /// </summary>
        public sealed class AxisDirectionStore
        {
            private readonly DS4MappedStickAxis[] axes;

            internal AxisDirectionStore(int length)
            {
                axes = new DS4MappedStickAxis[length];
                // Preserve the former byte array's zero-initialized cells.
                // A populated DS4State supplies its own neutral (128) axes.
                for (int index = 0; index < axes.Length; index++)
                {
                    axes[index] = DS4MappedStickAxis.FromLegacy(0);
                }
            }

            public int Length => axes.Length;

            public byte this[int index]
            {
                get => axes[index].LegacyValue;
                set => axes[index] = DS4MappedStickAxis.FromLegacy(value);
            }

            internal DS4MappedStickAxis GetMappedAxis(int index) => axes[index];

            internal void SetMappedAxis(int index, in DS4MappedStickAxis axis)
            {
                axes[index] = axis;
            }
        }

        public static ControlType[] mappedType = new ControlType[LAST_DS4_ACTION + 1]
        {
            ControlType.Unknown, // DS4Controls.None
            ControlType.AxisDir, // DS4Controls.LXNeg
            ControlType.AxisDir, // DS4Controls.LXPos
            ControlType.AxisDir, // DS4Controls.LYNeg
            ControlType.AxisDir, // DS4Controls.LYPos
            ControlType.AxisDir, // DS4Controls.RXNeg
            ControlType.AxisDir, // DS4Controls.RXPos
            ControlType.AxisDir, // DS4Controls.RYNeg
            ControlType.AxisDir, // DS4Controls.RYPos
            ControlType.Button, // DS4Controls.L1
            ControlType.Trigger, // DS4Controls.L2
            ControlType.Button, // DS4Controls.L3
            ControlType.Button, // DS4Controls.R1
            ControlType.Trigger, // DS4Controls.R2
            ControlType.Button, // DS4Controls.R3
            ControlType.Button, // DS4Controls.Square
            ControlType.Button, // DS4Controls.Triangle
            ControlType.Button, // DS4Controls.Circle
            ControlType.Button, // DS4Controls.Cross
            ControlType.Button, // DS4Controls.DpadUp
            ControlType.Button, // DS4Controls.DpadRight
            ControlType.Button, // DS4Controls.DpadDown
            ControlType.Button, // DS4Controls.DpadLeft
            ControlType.Button, // DS4Controls.PS
            ControlType.Touch, // DS4Controls.TouchLeft
            ControlType.Touch, // DS4Controls.TouchUpper
            ControlType.Touch, // DS4Controls.TouchMulti
            ControlType.Touch, // DS4Controls.TouchRight
            ControlType.Button, // DS4Controls.Share
            ControlType.Button, // DS4Controls.Options
            ControlType.Button, // DS4Controls.Mute
            ControlType.Button, // DS4Controls.FnL
            ControlType.Button, // DS4Controls.FnR
            ControlType.Button, // DS4Controls.BLP
            ControlType.Button, // DS4Controls.BRP
            ControlType.GyroDir, // DS4Controls.GyroXPos
            ControlType.GyroDir, // DS4Controls.GyroXNeg
            ControlType.GyroDir, // DS4Controls.GyroZPos
            ControlType.GyroDir, // DS4Controls.GyroZNeg
            ControlType.SwipeDir, // DS4Controls.SwipeLeft
            ControlType.SwipeDir, // DS4Controls.SwipeRight
            ControlType.SwipeDir, // DS4Controls.SwipeUp
            ControlType.SwipeDir, // DS4Controls.SwipeDown
            ControlType.Button, // DS4Controls.L2FullPull
            ControlType.Button, // DS4Controls.R2FullPull
            ControlType.Button, // DS4Controls.GyroSwipeLeft
            ControlType.Button, // DS4Controls.GyroSwipeRight
            ControlType.Button, // DS4Controls.GyroSwipeUp
            ControlType.Button, // DS4Controls.GyroSwipeDown
            ControlType.Button, // DS4Controls.Capture
            ControlType.Button, // DS4Controls.SideL
            ControlType.Button, // DS4Controls.SideR
            ControlType.Trigger, // DS4Controls.LSOuter
            ControlType.Trigger, // DS4Controls.RSOuter
            ControlType.Button,  // DS4Controls.TouchStarted
            ControlType.Button, // DS4Controls.TouchEnded
            ControlType.Button, // DS4Controls.Switch2C
            ControlType.Button, // DS4Controls.Switch2JoyConLeftPaddle1
            ControlType.Button, // DS4Controls.Switch2JoyConLeftPaddle2
            ControlType.Button, // DS4Controls.Switch2JoyConRightPaddle1
            ControlType.Button, // DS4Controls.Switch2JoyConRightPaddle2
            ControlType.Button, // DS4Controls.Switch2JoyConLeftIrSensor
            ControlType.Button, // DS4Controls.Switch2JoyConRightIrSensor
            ControlType.Button, // DS4Controls.Switch2JoyConLeftSL
            ControlType.Button, // DS4Controls.Switch2JoyConLeftSR
            ControlType.Button, // DS4Controls.Switch2JoyConRightSL
            ControlType.Button, // DS4Controls.Switch2JoyConRightSR
        };

        public DS4StateFieldMapping()
        {
        }

        public DS4StateFieldMapping(DS4State cState, DS4StateExposed exposeState, Mouse tp, bool priorMouse = false)
        {
            PopulateFieldMapping(cState, exposeState, tp, priorMouse);
        }

        public void PopulateFieldMapping(DS4State cState,
            DS4StateExposed exposeState, Mouse tp, bool priorMouse = false,
            Switch2.Switch2IrActivationThreshold leftIrThreshold =
                Switch2.Switch2IrActivationThreshold.Strict,
            Switch2.Switch2IrActivationThreshold rightIrThreshold =
                Switch2.Switch2IrActivationThreshold.Strict)
        {
            unchecked
            {
                axisdirs.SetMappedAxis((int)DS4Controls.LXNeg, cState.LXAxis);
                axisdirs.SetMappedAxis((int)DS4Controls.LXPos, cState.LXAxis);
                axisdirs.SetMappedAxis((int)DS4Controls.LYNeg, cState.LYAxis);
                axisdirs.SetMappedAxis((int)DS4Controls.LYPos, cState.LYAxis);
                triggers[(int)DS4Controls.LSOuter] = cState.OutputLSOuter;

                axisdirs.SetMappedAxis((int)DS4Controls.RXNeg, cState.RXAxis);
                axisdirs.SetMappedAxis((int)DS4Controls.RXPos, cState.RXAxis);
                axisdirs.SetMappedAxis((int)DS4Controls.RYNeg, cState.RYAxis);
                axisdirs.SetMappedAxis((int)DS4Controls.RYPos, cState.RYAxis);
                triggers[(int)DS4Controls.RSOuter] = cState.OutputRSOuter;

                triggers[(int)DS4Controls.L2] = cState.L2;
                triggers[(int)DS4Controls.R2] = cState.R2;

                buttons[(int)DS4Controls.L1] = cState.L1;
                buttons[(int)DS4Controls.L2FullPull] = IsTriggerFullPull(cState.L2Raw);
                buttons[(int)DS4Controls.L3] = cState.L3;
                buttons[(int)DS4Controls.R1] = cState.R1;
                buttons[(int)DS4Controls.R2FullPull] = IsTriggerFullPull(cState.R2Raw);
                buttons[(int)DS4Controls.R3] = cState.R3;

                buttons[(int)DS4Controls.Cross] = cState.Cross;
                buttons[(int)DS4Controls.Triangle] = cState.Triangle;
                buttons[(int)DS4Controls.Circle] = cState.Circle;
                buttons[(int)DS4Controls.Square] = cState.Square;
                buttons[(int)DS4Controls.PS] = cState.PS;
                buttons[(int)DS4Controls.Options] = cState.Options;
                buttons[(int)DS4Controls.Share] = cState.Share;
                buttons[(int)DS4Controls.Mute] = cState.Mute;
                buttons[(int)DS4Controls.FnL] = cState.FnL;
                buttons[(int)DS4Controls.FnR] = cState.FnR;
                buttons[(int)DS4Controls.BLP] = cState.BLP;
                buttons[(int)DS4Controls.BRP] = cState.BRP;
                buttons[(int)DS4Controls.Capture] = cState.Capture;
                buttons[(int)DS4Controls.SideL] = cState.SideL;
                buttons[(int)DS4Controls.SideR] = cState.SideR;

                buttons[(int)DS4Controls.DpadUp] = cState.DpadUp;
                buttons[(int)DS4Controls.DpadRight] = cState.DpadRight;
                buttons[(int)DS4Controls.DpadDown] = cState.DpadDown;
                buttons[(int)DS4Controls.DpadLeft] = cState.DpadLeft;

                buttons[(int)DS4Controls.TouchLeft] = tp != null ? (!priorMouse ? tp.leftDown : tp.priorLeftDown) : false;
                buttons[(int)DS4Controls.TouchRight] = tp != null ? (!priorMouse ? tp.rightDown : tp.priorRightDown) : false;
                buttons[(int)DS4Controls.TouchUpper] = tp != null ? (!priorMouse ? tp.upperDown : tp.priorUpperDown) : false;
                buttons[(int)DS4Controls.TouchMulti] = tp != null ? (!priorMouse ? tp.multiDown : tp.priorMultiDown) : false;

                int sixAxisX = -exposeState.getOutputAccelX();
                gryodirs[(int)DS4Controls.GyroXPos] = sixAxisX > 0 ? sixAxisX : 0;
                gryodirs[(int)DS4Controls.GyroXNeg] = sixAxisX < 0 ? sixAxisX : 0;

                int sixAxisZ = exposeState.getOutputAccelZ();
                gryodirs[(int)DS4Controls.GyroZPos] = sixAxisZ > 0 ? sixAxisZ : 0;
                gryodirs[(int)DS4Controls.GyroZNeg] = sixAxisZ < 0 ? sixAxisZ : 0;

                swipedirs[(int)DS4Controls.SwipeLeft] = tp != null ? (!priorMouse ? tp.swipeLeftB : tp.priorSwipeLeftB) : (byte)0;
                swipedirs[(int)DS4Controls.SwipeRight] = tp != null ? (!priorMouse ? tp.swipeRightB : tp.priorSwipeRightB) : (byte)0;
                swipedirs[(int)DS4Controls.SwipeUp] = tp != null ? (!priorMouse ? tp.swipeUpB : tp.priorSwipeUpB) : (byte)0;
                swipedirs[(int)DS4Controls.SwipeDown] = tp != null ? (!priorMouse ? tp.swipeDownB : tp.priorSwipeDownB) : (byte)0;

                swipedirbools[(int)DS4Controls.SwipeLeft] = tp != null ? (!priorMouse ? tp.swipeLeft : tp.priorSwipeLeft) : false;
                swipedirbools[(int)DS4Controls.SwipeRight] = tp != null ? (!priorMouse ? tp.swipeRight : tp.priorSwipeRight) : false;
                swipedirbools[(int)DS4Controls.SwipeUp] = tp != null ? (!priorMouse ? tp.swipeUp : tp.priorSwipeUp) : false;
                swipedirbools[(int)DS4Controls.SwipeDown] = tp != null ? (!priorMouse ? tp.swipeDown : tp.priorSwipeDown) : false;

                buttons[(int)DS4Controls.GyroSwipeLeft] = tp != null ? tp.gyroSwipe.swipeLeft : false;
                buttons[(int)DS4Controls.GyroSwipeRight] = tp != null ? tp.gyroSwipe.swipeRight : false;
                buttons[(int)DS4Controls.GyroSwipeUp] = tp != null ? tp.gyroSwipe.swipeUp : false;
                buttons[(int)DS4Controls.GyroSwipeDown] = tp != null ? tp.gyroSwipe.swipeDown : false;
                buttons[(int)DS4Controls.TouchStarted] = tp != null ? tp.TouchStarted : false;
                buttons[(int)DS4Controls.TouchEnded] = tp != null ? tp.TouchEnded : false;

                buttons[(int)DS4Controls.Switch2C] =
                    GetValidatedSwitch2SourceButton(cState,
                        DS4Controls.Switch2C);
                buttons[(int)DS4Controls.Switch2JoyConLeftPaddle1] =
                    GetValidatedSwitch2SourceButton(cState,
                        DS4Controls.Switch2JoyConLeftPaddle1);
                buttons[(int)DS4Controls.Switch2JoyConLeftPaddle2] =
                    GetValidatedSwitch2SourceButton(cState,
                        DS4Controls.Switch2JoyConLeftPaddle2);
                buttons[(int)DS4Controls.Switch2JoyConRightPaddle1] =
                    GetValidatedSwitch2SourceButton(cState,
                        DS4Controls.Switch2JoyConRightPaddle1);
                buttons[(int)DS4Controls.Switch2JoyConRightPaddle2] =
                    GetValidatedSwitch2SourceButton(cState,
                        DS4Controls.Switch2JoyConRightPaddle2);
                buttons[(int)DS4Controls.Switch2JoyConLeftIrSensor] =
                    GetValidatedSwitch2SourceButton(cState,
                        DS4Controls.Switch2JoyConLeftIrSensor,
                        leftIrThreshold, rightIrThreshold);
                buttons[(int)DS4Controls.Switch2JoyConRightIrSensor] =
                    GetValidatedSwitch2SourceButton(cState,
                        DS4Controls.Switch2JoyConRightIrSensor,
                        leftIrThreshold, rightIrThreshold);

                touchButton = cState.TouchButton;
                buttons[(int)DS4Controls.Switch2JoyConLeftSL] =
                    GetValidatedSwitch2SourceButton(cState, DS4Controls.Switch2JoyConLeftSL);
                buttons[(int)DS4Controls.Switch2JoyConLeftSR] =
                    GetValidatedSwitch2SourceButton(cState, DS4Controls.Switch2JoyConLeftSR);
                buttons[(int)DS4Controls.Switch2JoyConRightSL] =
                    GetValidatedSwitch2SourceButton(cState, DS4Controls.Switch2JoyConRightSL);
                buttons[(int)DS4Controls.Switch2JoyConRightSR] =
                    GetValidatedSwitch2SourceButton(cState, DS4Controls.Switch2JoyConRightSR);
                outputTouchButton = cState.OutputTouchButton;
            }
        }

        public static bool IsTriggerFullPull(byte rawTriggerValue)
        {
            return rawTriggerValue >= TRIGGER_FULL_PULL_THRESHOLD;
        }

        /// <summary>
        /// Reads the append-only Switch 2 profile sources without projecting
        /// them onto an unrelated legacy DS4State button. The two sidecars are
        /// mutually exclusive at their validated profile boundaries; a state
        /// claiming both sources is ambiguous and therefore maps to released.
        /// </summary>
        internal static bool GetValidatedSwitch2SourceButton(DS4State state,
            DS4Controls control,
            Switch2.Switch2IrActivationThreshold leftIrThreshold =
                Switch2.Switch2IrActivationThreshold.Strict,
            Switch2.Switch2IrActivationThreshold rightIrThreshold =
                Switch2.Switch2IrActivationThreshold.Strict)
        {
            if (state == null)
            {
                return false;
            }

            Switch2RawInputStatus pro = state.Switch2RawInputStatus;
            Switch2JoyConRawInputStatus joyCon =
                state.Switch2JoyConRawInputStatus;
            bool proValid = pro.IsValid &&
                pro.ContractVersion == SWITCH2_PRO_SOURCE_CONTRACT_VERSION;
            bool joyConValid = joyCon.IsValid &&
                joyCon.ContractVersion == SWITCH2_JOYCON_SOURCE_CONTRACT_VERSION;

            if (proValid == joyConValid)
            {
                return false;
            }

            return control switch
            {
                DS4Controls.Switch2C => proValid ? pro.CButton :
                    joyCon.CButton,
                DS4Controls.Switch2JoyConLeftSL => joyConValid && joyCon.LeftPresent && joyCon.LeftRailSL,
                DS4Controls.Switch2JoyConLeftSR => joyConValid && joyCon.LeftPresent && joyCon.LeftRailSR,
                DS4Controls.Switch2JoyConRightSL => joyConValid && joyCon.RightPresent && joyCon.RightRailSL,
                DS4Controls.Switch2JoyConRightSR => joyConValid && joyCon.RightPresent && joyCon.RightRailSR,
                DS4Controls.Switch2JoyConLeftPaddle1 =>
                    joyConValid && joyCon.LeftPaddle1,
                DS4Controls.Switch2JoyConLeftPaddle2 =>
                    joyConValid && joyCon.LeftPaddle2,
                DS4Controls.Switch2JoyConRightPaddle1 =>
                    joyConValid && joyCon.RightPaddle1,
                DS4Controls.Switch2JoyConRightPaddle2 =>
                    joyConValid && joyCon.RightPaddle2,
                DS4Controls.Switch2JoyConLeftIrSensor => joyConValid &&
                    joyCon.LeftPresent &&
                    Switch2.Switch2IrMouseProjection.IsThresholdActive(
                        leftIrThreshold, joyCon.LeftIrRoughness,
                        joyCon.LeftIrDistance),
                DS4Controls.Switch2JoyConRightIrSensor => joyConValid &&
                    joyCon.RightPresent &&
                    Switch2.Switch2IrMouseProjection.IsThresholdActive(
                        rightIrThreshold, joyCon.RightIrRoughness,
                        joyCon.RightIrDistance),
                _ => false,
            };
        }

        public void PopulateState(DS4State state)
        {
            unchecked
            {
                state.LXAxis = axisdirs.GetMappedAxis((int)DS4Controls.LXNeg);
                state.LXAxis = axisdirs.GetMappedAxis((int)DS4Controls.LXPos);
                state.LYAxis = axisdirs.GetMappedAxis((int)DS4Controls.LYNeg);
                state.LYAxis = axisdirs.GetMappedAxis((int)DS4Controls.LYPos);
                state.OutputLSOuter = triggers[(int)DS4Controls.LSOuter];

                state.RXAxis = axisdirs.GetMappedAxis((int)DS4Controls.RXNeg);
                state.RXAxis = axisdirs.GetMappedAxis((int)DS4Controls.RXPos);
                state.RYAxis = axisdirs.GetMappedAxis((int)DS4Controls.RYNeg);
                state.RYAxis = axisdirs.GetMappedAxis((int)DS4Controls.RYPos);
                state.OutputRSOuter = triggers[(int)DS4Controls.RSOuter];

                state.L2 = triggers[(int)DS4Controls.L2];
                state.R2 = triggers[(int)DS4Controls.R2];

                state.L1 = buttons[(int)DS4Controls.L1];
                state.L3 = buttons[(int)DS4Controls.L3];
                state.R1 = buttons[(int)DS4Controls.R1];
                state.R3 = buttons[(int)DS4Controls.R3];

                state.Cross = buttons[(int)DS4Controls.Cross];
                state.Triangle = buttons[(int)DS4Controls.Triangle];
                state.Circle = buttons[(int)DS4Controls.Circle];
                state.Square = buttons[(int)DS4Controls.Square];
                state.PS = buttons[(int)DS4Controls.PS];
                state.Options = buttons[(int)DS4Controls.Options];
                state.Share = buttons[(int)DS4Controls.Share];
                state.Mute = buttons[(int)DS4Controls.Mute];
                state.FnL = buttons[(int)DS4Controls.FnL];
                state.FnR = buttons[(int)DS4Controls.FnR];
                state.BLP = buttons[(int)DS4Controls.BLP];
                state.BRP = buttons[(int)DS4Controls.BRP];
                state.Capture = buttons[(int)DS4Controls.Capture];
                state.SideL = buttons[(int)DS4Controls.SideL];
                state.SideR = buttons[(int)DS4Controls.SideR];

                state.DpadUp = buttons[(int)DS4Controls.DpadUp];
                state.DpadRight = buttons[(int)DS4Controls.DpadRight];
                state.DpadDown = buttons[(int)DS4Controls.DpadDown];
                state.DpadLeft = buttons[(int)DS4Controls.DpadLeft];
                state.TouchButton = touchButton;
                state.OutputTouchButton = outputTouchButton;
            }
        }
    }
}
