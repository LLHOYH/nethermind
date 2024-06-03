// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript.Log;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal ref struct ILEvmState
{
    public byte[] MachineCode;
    // static arguments
    public ExecutionEnvironment Env;
    public TxExecutionContext TxCtx;
    public BlockExecutionContext BlkCtx;
    // in case of exceptions
    public EvmExceptionType EvmException;
    // in case of jumps crossing section boundaries
    public ushort ProgramCounter;
    public long GasAvailable;
    // in case STOP is executed
    public bool ShouldStop;
    public bool ShouldRevert;
    public bool ShouldReturn;

    public int StackHead;
    public Span<byte> Stack;

    public ref EvmPooledMemory Memory;

    public ref ReadOnlyMemory<byte> ReturnBuffer;

    public ILEvmState(byte[] machineCode, ExecutionEnvironment env, TxExecutionContext txCtx, BlockExecutionContext blkCtx, EvmExceptionType evmException, ushort programCounter, long gasAvailable, bool shouldStop, bool shouldRevert, bool shouldReturn, int stackHead, Span<byte> stack, ref EvmPooledMemory memory, ref ReadOnlyMemory<byte> returnBuffer)
    {
        MachineCode = machineCode;
        Env = env;
        TxCtx = txCtx;
        BlkCtx = blkCtx;
        EvmException = evmException;
        ProgramCounter = programCounter;
        GasAvailable = gasAvailable;
        ShouldStop = shouldStop;
        ShouldRevert = shouldRevert;
        ShouldReturn = shouldReturn;
        StackHead = stackHead;
        Stack = stack;
        Memory = ref memory;
        ReturnBuffer = ref returnBuffer;
    }
}
