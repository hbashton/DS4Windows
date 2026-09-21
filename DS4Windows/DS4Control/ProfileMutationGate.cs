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

        internal static bool TryEnter(int slot, out Scope scope)
        {
            if ((uint)slot >= gates.Length)
                throw new ArgumentOutOfRangeException(nameof(slot));
            if (!Monitor.TryEnter(gates[slot]))
            {
                scope = default;
                return false;
            }
            scope = new Scope(gates[slot], alreadyEntered: true);
            return true;
        }

        // Stack-only ownership keeps this synchronous boundary out of async
        // continuations. Monitor reentrancy preserves existing nested UI loads.
        internal readonly ref struct Scope
        {
            private readonly object gate;

            internal Scope(object gate, bool alreadyEntered = false)
            {
                if (!alreadyEntered) Monitor.Enter(gate);
                this.gate = gate;
            }

            public void Dispose()
            {
                if (gate != null) Monitor.Exit(gate);
            }
        }
    }
}
