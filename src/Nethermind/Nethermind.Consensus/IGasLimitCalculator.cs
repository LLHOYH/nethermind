// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus
{
    public interface IGasLimitCalculator
    {
        long GetGasLimit(BlockHeader parentHeader);
        long GetGasLimit(BlockHeader parentHeader, long? targetGasLimit);
    }
}
