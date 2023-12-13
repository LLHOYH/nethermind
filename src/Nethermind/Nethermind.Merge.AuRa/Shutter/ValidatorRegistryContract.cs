// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa.Shutter;

public class ValidatorRegistryContract : CallableContract, IValidatorRegistryContract
{
    private readonly ISigner _signer;
    private readonly ITxSender _txSender;
    private static readonly string FUNCTION_NAME = "update";
    private static readonly byte VALIDATOR_REGISTRY_MESSAGE_VERSION = 0;

    public ValidatorRegistryContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, ISigner signer, ITxSender txSender)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        _signer = signer;
        _txSender = txSender;
    }

    private void ComputeRegistryMessagePrefix(UInt64 validatorIndex, UInt64 nonce, Span<byte> registryMessagePrefix)
    {
        registryMessagePrefix[0] = VALIDATOR_REGISTRY_MESSAGE_VERSION;
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(3), BlockchainIds.Gnosis);
        Span<byte> addressSpan = registryMessagePrefix.Slice(9);
        addressSpan = ContractAddress!.Bytes;
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(31), validatorIndex);
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(39), nonce);
    }

    private byte[] ComputeDeregistrationMessage(UInt64 validatorIndex, UInt64 nonce)
    {
        Span<byte> registryMessagePrefix = stackalloc byte[46];
        ComputeRegistryMessagePrefix(validatorIndex, nonce, registryMessagePrefix);
        return registryMessagePrefix.ToArray();
    }

    private byte[] ComputeRegistrationMessage(UInt64 validatorIndex, UInt64 nonce)
    {
        Span<byte> registryMessagePrefix = stackalloc byte[46];
        ComputeRegistryMessagePrefix(validatorIndex, nonce, registryMessagePrefix);
        registryMessagePrefix[45] = 1;
        return registryMessagePrefix.ToArray();
    }

    private void SendMessage(byte[] message)
    {
        var transaction = GenerateTransaction<GeneratedTransaction>(FUNCTION_NAME, _signer.Address, message);

        // sign transaction?

        _txSender.SendTransaction(transaction, TxHandlingOptions.PersistentBroadcast);
    }

    public void Deregister(BlockHeader blockHeader)
    {
        UInt64 nonce = 0; // load nonce from disk
        UInt64 validatorIndex = 0;
        byte[] deregistrationMessage = ComputeDeregistrationMessage(nonce, validatorIndex);
        SendMessage(deregistrationMessage);
    }

    public void Register(BlockHeader blockHeader)
    {
        throw new NotImplementedException();
    }
}
