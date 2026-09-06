/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

namespace DS4Windows
{
    /// <summary>
    /// Xbox One semantic specialization of the single canonical ordered
    /// egress scheduler. It adds no mapping or transport policy; the future
    /// persona adapter consumes claims only after its own final admission.
    /// </summary>
    internal sealed class XboxOneEgressScheduler :
        OrderedEgressScheduler<XboxOneEgressState>
    {
        internal XboxOneEgressScheduler(long maximumOrderedAge,
            int orderedCapacity = DefaultOrderedCapacity)
            : base(XboxOneEgressState.Neutral, maximumOrderedAge,
                orderedCapacity)
        {
        }
    }
}
