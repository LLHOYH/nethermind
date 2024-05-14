// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Extensions;
using NUnit.Framework;


namespace Nethermind.Consensus.Test;

public class DepositProcessorTests
{
    private AbiSignature depositEventABI = new AbiSignature("DepositEvent", AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes, AbiType.DynamicBytes);

    // ToDo this test is not finished and needs to be rewritten
    [Test]
    public void CanParseDeposit()
    {
        var deposit = new Deposit()
        {
            Amount = 32000000000,
            Index = 0,
            PubKey = Bytes.FromHexString(
                "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001"),
            Signature = Bytes.FromHexString(
                "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003"),
            WithdrawalCredentials =
                Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000002")
        };
        var bytes = Bytes.FromHexString(
            "00000000000000000000000000000000000000000000000000000000000000a000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000018000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000030000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000080040597307000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000006000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000300000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000000");
        AbiEncoder abiEncoder = new AbiEncoder();
        var result = abiEncoder.Decode(AbiEncodingStyle.None, depositEventABI, bytes);

        var newDeposit = new Deposit()
        {
            PubKey = (byte[])result[0],
            WithdrawalCredentials = (byte[])result[1],
            // Amount = ((byte[])result[2]).Reverse().ToArray().ToULongFromBigEndianByteArrayWithoutLeadingZeros(),
            Amount = BitConverter.ToUInt64(((byte[])result[2]).Reverse().ToArray(), 0),
            Signature = (byte[])result[3],
            // Index = ((byte[])result[4]).Reverse().ToArray().ToULongFromBigEndianByteArrayWithoutLeadingZeros(),
            Index = BitConverter.ToUInt64(((byte[])result[4]).Reverse().ToArray(), 0)
        };
    }
}
