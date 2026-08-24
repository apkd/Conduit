#nullable enable

using System;

namespace Conduit
{
    enum BurstMemoryAccessKind : byte
    {
        None,
        Load,
        Store,
        ReadModifyWrite,
        Other,
    }

    enum BurstSimdRole : byte
    {
        None,
        Transfer,
        Lane,
        ScalarCompute,
        PackedCompute,
        Setup,
        Other,
    }

    [Flags]
    enum BurstRegisterKinds : byte
    {
        None = 0,
        Xmm = 1 << 0,
        Ymm = 1 << 1,
        Zmm = 1 << 2,
        ArmVector = 1 << 3,
        ArmScalar = 1 << 4,
        ScalableArmVector = 1 << 5,
    }

    enum BurstCallKind : byte
    {
        None,
        Direct,
        Indirect,
    }

    readonly struct BurstInstructionFacts
    {
        internal readonly BurstMemoryAccessKind MemoryAccess;
        internal readonly BurstSimdRole SimdRole;
        internal readonly BurstCallKind CallKind;

        internal BurstInstructionFacts(
            BurstMemoryAccessKind memoryAccess,
            BurstSimdRole simdRole,
            BurstCallKind callKind)
        {
            MemoryAccess = memoryAccess;
            SimdRole = simdRole;
            CallKind = callKind;
        }
    }
}
