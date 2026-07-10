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
    public class DS4StateExposed
    {
        private DS4State _state;

        public DS4StateExposed()
        {
            _state = new DS4State();
        }

        public DS4StateExposed(DS4State state)
        {
            _state = state;
        }

        public SixAxis Motion
        {
            get => _state.Motion;
        }

        public int GyroYaw { get { return _state.Motion.gyroYaw; } }
        public int getGyroYaw()
        {
            return _state.Motion.gyroYaw;
        }

        public int GyroPitch { get { return _state.Motion.gyroPitch; } }
        public int getGyroPitch()
        {
            return _state.Motion.gyroPitch;
        }

        public int GyroRoll { get { return _state.Motion.gyroRoll; } }
        public int getGyroRoll()
        {
            return _state.Motion.gyroRoll;
        }

        public int AccelX { get { return _state.Motion.accelX; } }
        public int getAccelX()
        {
            return _state.Motion.accelX;
        }

        public int AccelY { get { return _state.Motion.accelY; } }
        public int getAccelY()
        {
            return _state.Motion.accelY;
        }

        public int AccelZ { get { return _state.Motion.accelZ; } }
        public int getAccelZ()
        {
            return _state.Motion.accelZ;
        }

        public int OutputAccelX { get { return _state.Motion.outputAccelX; } }
        public int getOutputAccelX()
        {
            return _state.Motion.outputAccelX;
        }

        public int OutputAccelY { get { return _state.Motion.outputAccelY; } }
        public int getOutputAccelY()
        {
            return _state.Motion.outputAccelY;
        }

        public int OutputAccelZ { get { return _state.Motion.outputAccelZ; } }
        public int getOutputAccelZ()
        {
            return _state.Motion.outputAccelZ;
        }
    }
}
