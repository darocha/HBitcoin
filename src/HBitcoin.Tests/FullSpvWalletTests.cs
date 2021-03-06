﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HBitcoin.FullBlockSpv;
using HBitcoin.KeyManagement;
using HBitcoin.Models;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;
using QBitNinja.Client;
using QBitNinja.Client.Models;

namespace HBitcoin.Tests
{
	public class FullSpvWalletTests
	{
		[Theory]
		[InlineData("TestNet")]
		[InlineData("Main")]
		public void SyncingTest(string networkString)
		{
			// load wallet
			Network network = networkString == "TestNet"? Network.TestNet:Network.Main;
			string path = $"Wallets/Empty{network}.json";
			const string password = "";
			Safe safe;
			if(File.Exists(path))
			{
				safe = Safe.Load(password, path);
				safe.Delete();
			}
			else
			{
				Mnemonic mnemonic;
				safe = Safe.Create(out mnemonic, password, path, network);
			}

			Debug.WriteLine($"Unique Safe ID: {safe.UniqueId}");

			// create walletjob
			WalletJob.Init(safe);
			var fullyConnected = false;
			var synced = false;
			// note some event
			WalletJob.ConnectedNodeCountChanged += delegate
			{
				if(WalletJob.MaxConnectedNodeCount == WalletJob.ConnectedNodeCount)
				{
					fullyConnected = true;
					Debug.WriteLine(
						$"{nameof(WalletJob.MaxConnectedNodeCount)} reached: {WalletJob.MaxConnectedNodeCount}");
				}
				else Debug.WriteLine($"{nameof(WalletJob.ConnectedNodeCount)}: {WalletJob.ConnectedNodeCount}");
			};
			WalletJob.StateChanged += delegate
			{
				Debug.WriteLine($"{nameof(WalletJob.State)}: {WalletJob.State}");
				if(WalletJob.State == WalletState.Synced)
				{
					synced = true;
				}
				else synced = false;
			};
			Assert.True(WalletJob.SafeAccounts.Count == 0);
			Assert.True(WalletJob.ConnectedNodeCount == 0);
			var allTxCount = WalletJob.Tracker.TrackedTransactions.Count;
			Assert.True(allTxCount == 0);
			Assert.True(!WalletJob.GetSafeHistory().Any());
			Assert.True(WalletJob.State == WalletState.NotStarted);
			Assert.True(WalletJob.TracksDefaultSafe);

			// start syncing
			var cts = new CancellationTokenSource();
			var walletJobTask = WalletJob.StartAsync(cts.Token);
			Assert.True(WalletJob.State != WalletState.NotStarted);
			Task reportTask = Helpers.ReportAsync(cts.Token);

			try
			{
				// wait until fully synced and connected
				while (!fullyConnected)
				{
					Task.Delay(10).Wait();
				}

				while (!synced)
				{
					Task.Delay(1000).Wait();
				}

				Assert.True(WalletJob.State == WalletState.Synced);
				Assert.True(WalletJob.CreationHeight != Height.Unknown);
				Assert.True(WalletJob.Tracker.TrackedTransactions.Count == 0);
				Assert.True(!WalletJob.GetSafeHistory().Any());
				Height headerHeight;
				Assert.True(WalletJob.TryGetHeaderHeight(out headerHeight));
				var expectedBlockCount = headerHeight.Value - WalletJob.CreationHeight.Value + 1;
				Assert.True(WalletJob.Tracker.BlockCount == expectedBlockCount);
				Assert.True(WalletJob.Tracker.TrackedScriptPubKeys.Count > 0);
				Assert.True(WalletJob.Tracker.TrackedTransactions.Count == 0);
			}
			finally
			{
				cts.Cancel();
				Task.WhenAll(reportTask, walletJobTask).Wait();
			}
		}

		[Fact]
		public void HaveFundsTest()
		{
			// load wallet
			Network network = Network.TestNet;
			string path = $"CommittedWallets/HaveFunds{network}.json";
			const string password = "";
			Safe safe = Safe.Load(password, path);
			Assert.Equal(safe.Network, network);
			Debug.WriteLine($"Unique Safe ID: {safe.UniqueId}");

			// create walletjob
			WalletJob.Init(safe);
			// note some event
			WalletJob.ConnectedNodeCountChanged += delegate
			{
				if(WalletJob.MaxConnectedNodeCount == WalletJob.ConnectedNodeCount)
				{
					Debug.WriteLine(
						$"{nameof(WalletJob.MaxConnectedNodeCount)} reached: {WalletJob.MaxConnectedNodeCount}");
				}
				else Debug.WriteLine($"{nameof(WalletJob.ConnectedNodeCount)}: {WalletJob.ConnectedNodeCount}");
			};
			WalletJob.StateChanged += delegate
			{
				Debug.WriteLine($"{nameof(WalletJob.State)}: {WalletJob.State}");
			};

			// start syncing
			var cts = new CancellationTokenSource();
			var walletJobTask = WalletJob.StartAsync(cts.Token);
			Task reportTask = Helpers.ReportAsync(cts.Token);

			try
			{
				// wait until synced enough to have our transaction
				while (WalletJob.BestHeight.Type != HeightType.Chain || WalletJob.BestHeight < 1092448)
				{
					Task.Delay(1000).Wait();
				}

				var hasMoneyAddress = BitcoinAddress.Create("mmVZjqZjmLvxc3YFhWqYWoe5anrWVcoJcc");
				Debug.WriteLine($"Checking proper balance on {hasMoneyAddress.ToWif()}");

				var record = WalletJob.GetSafeHistory().FirstOrDefault();
				Assert.True(record != default(SafeHistoryRecord));

				Assert.True(record.Confirmed);
				Assert.True(record.Amount == new Money(0.1m, MoneyUnit.BTC));
				DateTimeOffset expTime;
				DateTimeOffset.TryParse("2017.03.06. 16:47:15 +00:00", out expTime);
				Assert.True(record.TimeStamp == expTime);
				Assert.True(record.TransactionId == new uint256("50898694f281ed059fa6b9d37ccf099ab261540be14fd43ce1a6d6684fbd4e94"));
			}
			finally
			{
				cts.Cancel();
				Task.WhenAll(reportTask, walletJobTask).Wait();
			}
		}

		// test with a long time used testnet wallet, with exotic, including tumblebit transactions
		[Fact]
		public void RealHistoryTest()
		{
			// load wallet
			Network network = Network.TestNet;
			string path = "CommittedWallets/HiddenWallet.json";
			const string password = "";
			// I change it because I am using a very old wallet to test
			Safe.EarliestPossibleCreationTime = DateTimeOffset.ParseExact("2016-12-18", "yyyy-MM-dd", CultureInfo.InvariantCulture);
			Safe safe = Safe.Load(password, path);
			Assert.Equal(safe.Network, network);
			Debug.WriteLine($"Unique Safe ID: {safe.UniqueId}");

			// create walletjob
			WalletJob.MaxCleanAddressCount = 79;
			WalletJob.Init(safe);
			var syncedOnce = false;
			// note some event
			WalletJob.ConnectedNodeCountChanged += delegate
			{
				if (WalletJob.MaxConnectedNodeCount == WalletJob.ConnectedNodeCount)
				{
					Debug.WriteLine(
						$"{nameof(WalletJob.MaxConnectedNodeCount)} reached: {WalletJob.MaxConnectedNodeCount}");
				}
				else Debug.WriteLine($"{nameof(WalletJob.ConnectedNodeCount)}: {WalletJob.ConnectedNodeCount}");
			};
			WalletJob.StateChanged += delegate
			{
				Debug.WriteLine($"{nameof(WalletJob.State)}: {WalletJob.State}");
				
				if (WalletJob.State == WalletState.Synced)
				{
					syncedOnce = true;
				}
			};

			// start syncing
			var cts = new CancellationTokenSource();
			var walletJobTask = WalletJob.StartAsync(cts.Token);
			Task reportTask = Helpers.ReportAsync(cts.Token);

			try
			{
				// wait until fully synced
				while (!syncedOnce)
				{
					Task.Delay(1000).Wait();
				}

				Helpers.ReportFullHistory();

				// 0. Query all operations, grouped our used safe addresses
				int MinUnusedKeyNum = 37;
				Dictionary<BitcoinAddress, List<BalanceOperation>> operationsPerAddresses = Helpers.QueryOperationsPerSafeAddressesAsync(new QBitNinjaClient(safe.Network), safe, MinUnusedKeyNum).Result;

				Dictionary<uint256, List<BalanceOperation>> operationsPerTransactions = QBitNinjaJutsus.GetOperationsPerTransactions(operationsPerAddresses);

				// 3. Create history records from the transactions
				// History records is arbitrary data we want to show to the user
				var txHistoryRecords = new List<Tuple<DateTimeOffset, Money, int, uint256>>();
				foreach (var elem in operationsPerTransactions)
				{
					var amount = Money.Zero;
					foreach (var op in elem.Value)
						amount += op.Amount;

					var firstOp = elem.Value.First();

					txHistoryRecords
						.Add(new Tuple<DateTimeOffset, Money, int, uint256>(
							firstOp.FirstSeen,
							amount,
							firstOp.Confirmations,
							elem.Key));
				}

				// 4. Order the records by confirmations and time (Simply time does not work, because of a QBitNinja issue)
				var qBitHistoryRecords = txHistoryRecords
					.OrderByDescending(x => x.Item3) // Confirmations
					.ThenBy(x => x.Item1); // FirstSeen

				var fullSpvHistoryRecords = WalletJob.GetSafeHistory();

				// This won't be equal QBit doesn't show us this transaction: 2017.01.04. 16:24:49	0.00000000	True		77b10ff78aab2e41764a05794c4c464922c73f0c23356190429833ce68fd7be9
				//Assert.Equal(qBitHistoryRecords.Count(), fullSpvHistoryRecords.Count());

				HashSet<SafeHistoryRecord> qBitFoundItToo = new HashSet<SafeHistoryRecord>();
				// Assert all record found by qbit also found by spv and they are identical
				foreach (var record in qBitHistoryRecords)
				{
					// Item2 is the Amount
					SafeHistoryRecord found = fullSpvHistoryRecords.FirstOrDefault(x => x.TransactionId == record.Item4);
					Assert.True(found != default(SafeHistoryRecord));
					Assert.True(found.TimeStamp.Equals(record.Item1));
					Assert.True(found.Confirmed.Equals(record.Item3 > 0));
					Assert.True(found.Amount.Equals(record.Item2));
					qBitFoundItToo.Add(found);
				}

				foreach (var record in fullSpvHistoryRecords)
				{
					if (!qBitFoundItToo.Contains(record))
					{
						Assert.True(null == qBitHistoryRecords.FirstOrDefault(x => x.Item4 == record.TransactionId));
						Debug.WriteLine($@"QBitNinja failed to find, but SPV found it: {record.TimeStamp.DateTime}	{record.Amount}	{record.Confirmed}		{record.TransactionId}");
					}
				}
			}
			finally
			{
				cts.Cancel();
				Task.WhenAll(reportTask, walletJobTask).Wait();
			}
		}
	}
}
