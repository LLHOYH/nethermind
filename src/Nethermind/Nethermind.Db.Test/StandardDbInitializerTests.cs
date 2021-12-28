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

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [Parallelizable(ParallelScope.All)]
    public class StandardDbInitializerTests
    {
        private string _folderWithDbs;

        [OneTimeSetUp]
        public void Initialize()
        {
            _folderWithDbs = Guid.NewGuid().ToString();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_MemDbProvider(bool useReceipts)
        {
            IDbProvider dbProvider = await InitializeStandardDb(useReceipts, DbModeHint.Mem, "mem");
            Type receiptsType = GetReceiptsType(useReceipts, typeof(MemColumnsDb<ReceiptsColumns>));
            AssertStandardDbs(dbProvider, typeof(MemDb), receiptsType);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_RocksDbProvider(bool useReceipts)
        {
            IDbProvider dbProvider = await InitializeStandardDb(useReceipts, DbModeHint.Persisted, $"rocks_{useReceipts}");
            Type receiptsType = GetReceiptsType(useReceipts);
            AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InitializerTests_ReadonlyDbProvider(bool useReceipts)
        {
            IDbProvider dbProvider = await InitializeStandardDb(useReceipts, DbModeHint.Persisted, $"readonly_{useReceipts}");
            using ReadOnlyDbProvider readonlyDbProvider = new(dbProvider, true);
            Type receiptsType = GetReceiptsType(useReceipts);
            AssertStandardDbs(dbProvider, typeof(DbOnTheRocks), receiptsType);
            AssertStandardDbs(readonlyDbProvider, typeof(ReadOnlyDb), GetReceiptsType(false));
        }
        
        [Test]
        public async Task InitializerTests_WithPruning()
        {
            IDbProvider dbProvider = await InitializeStandardDb(false, DbModeHint.Mem, "pruning", true);
            dbProvider.StateDb.Should().BeOfType<FullPruningDb>();
        }
        
        private async Task<IDbProvider> InitializeStandardDb(bool useReceipts, DbModeHint dbModeHint, string path, bool pruning = false)
        {
            using IDbProvider dbProvider = new DbProvider(dbModeHint);
            RocksDbFactory rocksDbFactory = new(new DbConfig(), LimboLogs.Instance, Path.Combine(_folderWithDbs, path));
            StandardDbInitializer initializer = new(dbProvider, rocksDbFactory, new MemDbFactory(), Substitute.For<IFileSystem>(), pruning);
            await initializer.InitStandardDbsAsync(useReceipts);
            return dbProvider;
        }
        
        private static Type GetReceiptsType(bool useReceipts, Type receiptType = null) => useReceipts ? receiptType ?? typeof(ColumnsDb<ReceiptsColumns>) : typeof(ReadOnlyColumnsDb<ReceiptsColumns>);
        
        private void AssertStandardDbs(IDbProvider dbProvider, Type dbType, Type receiptsDb)
        {
            dbProvider.BlockInfosDb.Should().BeOfType(dbType);
            dbProvider.BlocksDb.Should().BeOfType(dbType);
            dbProvider.BloomDb.Should().BeOfType(dbType);
            dbProvider.ChtDb.Should().BeOfType(dbType);
            dbProvider.HeadersDb.Should().BeOfType(dbType);
            dbProvider.ReceiptsDb.Should().BeOfType(receiptsDb);
            dbProvider.CodeDb.Should().BeOfType(dbType);
            dbProvider.StateDb.Should().BeOfType(dbType);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folderWithDbs))
                Directory.Delete(_folderWithDbs, true);
        }
    }
}
