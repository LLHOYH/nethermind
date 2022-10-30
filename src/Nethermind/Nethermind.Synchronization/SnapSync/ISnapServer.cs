//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync;

public interface ISnapServer
{
    public List<byte[]>? GetTrieNodes(PathGroup[] pathSet, Keccak rootHash);
    public List<byte[]> GetByteCodes(Keccak[] requestedHashes, long byteLimit);

    public (PathWithAccount[], byte[][]) GetAccountRanges(Keccak rootHash, Keccak startingHash, Keccak? limitHash,
        long byteLimit);

    public (List<PathWithStorageSlot[]>, byte[][]?) GetStorageRanges(Keccak rootHash, PathWithAccount[] accounts,
        Keccak? startingHash, Keccak? limitHash, long byteLimit);

}
