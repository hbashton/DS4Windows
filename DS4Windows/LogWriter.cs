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

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DS4WinWPF
{
    public class LogWriter
    {
        private string filename;
        private List<LogItem> logCol;

        public LogWriter(string filename, List<LogItem> col)
        {
            this.filename = filename;
            logCol = col;
        }

        internal static string Format(IEnumerable<LogItem> entries)
        {
            var text = new StringBuilder();
            foreach (LogItem item in entries)
                text.AppendLine($"{item.Datetime}: {item.Message}");
            return text.ToString();
        }

        public void Process()
        {
            try
            {
                using StreamWriter stream = new StreamWriter(filename);
                stream.Write(Format(logCol));
            }
            catch { }
        }
    }
}
