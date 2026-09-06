using System;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Common cold profile-mutation boundary for workers, direct loads and UI
    /// edits. Never enter from a controller report callback. This serializes
    /// writers; it does not replace a runtime action lease or revision guard.
    /// </summary>
    internal static class ProfileMutationGate
    {
        private static readonly object[] gates = CreateGates();

        private static object[] CreateGates()
        {
            var result = new object[Global.TEST_PROFILE_ITEM_COUNT];
            for (int index = 0; index < result.Length; index++)
                result[index] = new object();
            return result;
        }

        internal static Scope Enter(int slot)
        {
            if ((uint)slot >= gates.Length)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return new Scope(gates[slot]);
        }

        // Stack-only ownership keeps this synchronous boundary out of async
        // continuations. Monitor reentrancy preserves existing nested UI loads.
        internal readonly ref struct Scope
        {
            private readonly object gate;

            internal Scope(object gate)
            {
                Monitor.Enter(gate);
                this.gate = gate;
            }

            public void Dispose() => Monitor.Exit(gate);
        }
    }
}
