// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;

namespace Nethermind.Facade;

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository) : ICodeInfoRepository
{
    private readonly Dictionary<Address, CodeInfo> _codeOverwrites = new();

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec) =>
        _codeOverwrites.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, vmSpec);

    public CodeInfo GetOrAdd(ValueHash256 codeHash, ReadOnlySpan<byte> initCode) => codeInfoRepository.GetOrAdd(codeHash, initCode);

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(state, code, codeOwner, spec);

    public void SetCodeOverwrite(
        IWorldState worldState,
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value,
        Address? redirectAddress = null)
    {
        if (redirectAddress is not null)
        {
            _codeOverwrites[redirectAddress] = GetCachedCodeInfo(worldState, key, vmSpec);
        }

        _codeOverwrites[key] = value;
    }

    /// <summary>
    /// Copy code from <paramref name="codeSource"/> and set it to override <paramref name="target"/>.
    /// Main use for this is for https://eips.ethereum.org/EIPS/eip-7702
    /// </summary>
    /// <param name="code"></param>
    public void CopyCodeAndOverwrite(
        IWorldState worldState,
        Address codeSource,
        Address target,
        IReleaseSpec vmSpec)
    {
        if (!_codeOverwrites.ContainsKey(target))
        {
            _codeOverwrites.Add(target, GetCachedCodeInfo(worldState, codeSource, vmSpec));
        }
    }

    public void ClearOverwrites()
    {
        _codeOverwrites.Clear();
    }
}
